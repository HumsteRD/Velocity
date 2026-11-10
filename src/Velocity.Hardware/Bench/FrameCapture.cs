using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Velocity.Hardware.Bench;

/// <summary>Одно событие трассировки: от кого, какое, из какого процесса и когда.</summary>
public readonly record struct EtwEvent(Guid Provider, ushort Id, int ProcessId, long Timestamp);

/// <summary>
/// Минимальный потребитель ETW (Event Tracing for Windows) без сторонних библиотек.
///
/// Тот же механизм, что у PresentMon и CapFrameX: игра ничего не замечает, в её процесс
/// ничего не внедряется, античиты к этому относятся спокойно. Реальное время требует прав
/// администратора — так устроена Windows. Чтение готового файла .etl прав не требует,
/// на нём самопроверка проверяет разбор структур.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Etw
{
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint ControlStop = 1;
    private const uint EnableProvider = 1;
    private const uint ErrorAlreadyExists = 183;
    private const uint ErrorAccessDenied = 5;
    private const ulong InvalidHandle = ulong.MaxValue;

    // Смещения полей EVENT_TRACE_LOGFILEW (x64, 448 байт) и EVENT_RECORD.
    private const int LogfileSize = 448;
    private const int OffLogFileName = 0, OffLoggerName = 8, OffProcessTraceMode = 28, OffEventRecordCallback = 424;
    private const int OffProcessId = 12, OffTimeStamp = 16, OffProviderId = 24, OffEventId = 40;

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RecordCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll")]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);

    public static bool IsSupported => Environment.Is64BitProcess;

    /// <summary>Читает файл .etl целиком. Нужен для проверки разбора без прав администратора.</summary>
    public static int ReadFile(string path, Action<EtwEvent> onEvent)
    {
        using var consumer = new Consumer(onEvent);
        return consumer.Run(fileName: path, loggerName: null);
    }

    /// <summary>Сессия трассировки реального времени.</summary>
    public sealed class Session : IDisposable
    {
        private readonly string _name;
        private ulong _handle;
        private Consumer? _consumer;
        private Thread? _thread;

        private Session(string name) => _name = name;

        /// <summary>
        /// Запускает сессию и включает поставщиков. Без прав администратора бросает
        /// <see cref="UnauthorizedAccessException"/>.
        /// </summary>
        public static Session Start(string name, IEnumerable<Guid> providers, Action<EtwEvent> onEvent)
        {
            if (!IsSupported) throw new PlatformNotSupportedException();
            var session = new Session(name);
            var props = Properties(name, out var size);
            try
            {
                var status = StartTraceW(out session._handle, name, props);
                if (status == ErrorAlreadyExists)
                {
                    // Сессия осталась от прошлого запуска, который не успел её закрыть.
                    ControlTraceW(0, name, props, ControlStop);
                    Marshal.FreeHGlobal(props);
                    props = Properties(name, out size);
                    status = StartTraceW(out session._handle, name, props);
                }
                if (status == ErrorAccessDenied) throw new UnauthorizedAccessException();
                if (status != 0) throw new InvalidOperationException($"StartTrace: {status}");
            }
            finally { Marshal.FreeHGlobal(props); }

            foreach (var provider in providers)
            {
                var guid = provider;
                EnableTraceEx2(session._handle, ref guid, EnableProvider, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            }

            session._consumer = new Consumer(onEvent);
            session._thread = new Thread(() => session._consumer.Run(fileName: null, loggerName: name))
            {
                IsBackground = true,
                Name = "VELOCITY ETW"
            };
            session._thread.Start();
            return session;
        }

        public void Stop()
        {
            if (_handle == 0) return;
            var props = Properties(_name, out _);
            try { ControlTraceW(_handle, null, props, ControlStop); }
            finally { Marshal.FreeHGlobal(props); }
            _handle = 0;
            _thread?.Join(TimeSpan.FromSeconds(3));
            _consumer?.Dispose();
        }

        public void Dispose() => Stop();
    }

    private static IntPtr Properties(string name, out int size)
    {
        var structSize = Marshal.SizeOf<TraceProperties>();
        size = structSize + (name.Length + 1) * 2 + 1024;
        var buffer = Marshal.AllocHGlobal(size);
        var zero = new byte[size];
        Marshal.Copy(zero, 0, buffer, size);
        var props = new TraceProperties
        {
            Wnode = new WnodeHeader
            {
                BufferSize = (uint)size,
                Flags = WnodeFlagTracedGuid,
                ClientContext = 1 // метки времени — QueryPerformanceCounter
            },
            BufferSize = 256,
            MinimumBuffers = 16,
            MaximumBuffers = 64,
            LogFileMode = EventTraceRealTimeMode,
            FlushTimer = 1,
            LoggerNameOffset = (uint)structSize
        };
        Marshal.StructureToPtr(props, buffer, false);
        return buffer;
    }

    private sealed class Consumer : IDisposable
    {
        private readonly Action<EtwEvent> _onEvent;
        private readonly RecordCallback _callback; // держим ссылку, иначе сборщик мусора уберёт делегат
        private ulong _trace = InvalidHandle;
        private int _count;

        public Consumer(Action<EtwEvent> onEvent)
        {
            _onEvent = onEvent;
            _callback = OnRecord;
        }

        public int Run(string? fileName, string? loggerName)
        {
            var logfile = Marshal.AllocHGlobal(LogfileSize);
            var namePtr = IntPtr.Zero;
            try
            {
                Marshal.Copy(new byte[LogfileSize], 0, logfile, LogfileSize);
                namePtr = Marshal.StringToHGlobalUni(fileName ?? loggerName);
                Marshal.WriteIntPtr(logfile, fileName is not null ? OffLogFileName : OffLoggerName, namePtr);
                var mode = ProcessTraceModeEventRecord | (fileName is null ? ProcessTraceModeRealTime : 0);
                Marshal.WriteInt32(logfile, OffProcessTraceMode, unchecked((int)mode));
                Marshal.WriteIntPtr(logfile, OffEventRecordCallback, Marshal.GetFunctionPointerForDelegate(_callback));

                _trace = OpenTraceW(logfile);
                if (_trace == InvalidHandle)
                    throw new InvalidOperationException($"OpenTrace: {Marshal.GetLastPInvokeError()}");
                ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero);
                return _count;
            }
            finally
            {
                if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
                Marshal.FreeHGlobal(logfile);
            }
        }

        private void OnRecord(IntPtr record)
        {
            try
            {
                var provider = Marshal.PtrToStructure<Guid>(record + OffProviderId);
                var id = unchecked((ushort)Marshal.ReadInt16(record, OffEventId));
                var pid = Marshal.ReadInt32(record, OffProcessId);
                var timestamp = Marshal.ReadInt64(record, OffTimeStamp);
                _count++;
                _onEvent(new EtwEvent(provider, id, pid, timestamp));
            }
            catch { /* исключение не должно уйти в нативный код */ }
        }

        public void Dispose()
        {
            if (_trace != InvalidHandle) CloseTrace(_trace);
            _trace = InvalidHandle;
        }
    }
}

/// <summary>Кадры одного процесса за время замера.</summary>
public sealed record CapturedProcess(int ProcessId, string Name, List<double> FrameTimesMs);

/// <summary>
/// Замер кадров в игре по событиям Present (DirectX 9, 10, 11, 12).
/// Считается время между вызовами Present — то же, что MsBetweenPresents у PresentMon.
/// Vulkan и OpenGL через эти события не видны: для них есть импорт CSV из PresentMon.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FrameCapture : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;

    /// <summary>Процессы, которые рисуют всегда и игрой не являются.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "Velocity", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "ApplicationFrameHost", "SystemSettings", "LockApp", "steamwebhelper",
        "msedgewebview2", "EpicWebHelper", "RiotClientUx", "RiotClientUxRender", "Discord", "obs64"
    };

    private readonly ConcurrentDictionary<int, List<long>> _presents = new();
    private readonly Etw.Session _session;
    private readonly long _started = Stopwatch.GetTimestamp();

    private FrameCapture()
    {
        _session = Etw.Session.Start("VELOCITY-FrameCapture", [Dxgi, D3D9], OnEvent);
    }

    /// <summary>Начинает замер. Без прав администратора бросает UnauthorizedAccessException.</summary>
    public static FrameCapture Start() => new();

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    private void OnEvent(EtwEvent e)
    {
        bool present = (e.Provider == Dxgi && e.Id is DxgiPresentStart or DxgiPresentMpoStart)
                       || (e.Provider == D3D9 && e.Id == D3D9PresentStart);
        if (!present || e.ProcessId <= 4) return;
        var list = _presents.GetOrAdd(e.ProcessId, _ => new List<long>(8192));
        lock (list) list.Add(e.Timestamp);
    }

    /// <summary>Самый «рисующий» процесс, похожий на игру, и его кадры в секунду сейчас.</summary>
    public (int ProcessId, string Name, int Frames)? Leader()
    {
        var best = _presents
            .Select(p => (Pid: p.Key, Count: Count(p.Value)))
            .OrderByDescending(p => p.Count)
            .Select(p => (p.Pid, Name: ProcessName(p.Pid), p.Count))
            .FirstOrDefault(p => p.Name is not null && !NotGames.Contains(p.Name));
        return best.Name is null ? null : (best.Pid, best.Name, best.Count);
    }

    /// <summary>Останавливает замер и отдаёт кадры по процессам, от самых частых.</summary>
    public List<CapturedProcess> Stop()
    {
        _session.Stop();
        var frequency = (double)Stopwatch.Frequency;
        var result = new List<CapturedProcess>();
        foreach (var (pid, stamps) in _presents)
        {
            long[] sorted;
            lock (stamps) sorted = [.. stamps];
            Array.Sort(sorted);
            if (sorted.Length < 10) continue;
            var times = new List<double>(sorted.Length - 1);
            for (var i = 1; i < sorted.Length; i++)
            {
                var ms = (sorted[i] - sorted[i - 1]) * 1000.0 / frequency;
                if (ms > 0) times.Add(ms);
            }
            result.Add(new CapturedProcess(pid, ProcessName(pid) ?? $"PID {pid}", times));
        }
        return [.. result
            .OrderBy(p => NotGames.Contains(p.Name) ? 1 : 0)
            .ThenByDescending(p => p.FrameTimesMs.Count)];
    }

    public void Dispose() => _session.Stop();

    private static int Count(List<long> list) { lock (list) return list.Count; }

    private static string? ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }
}

/// <summary>Итог замера: средний FPS, «низы» и рывки.</summary>
public sealed record FrameStats(
    int Frames, double DurationS, double AvgFps, double Low1Fps, double Low01Fps,
    double MedianMs, double P99Ms, double MaxMs, int Stutters)
{
    /// <summary>
    /// 1% low считается как FPS по 99-му перцентилю времени кадра (как в CapFrameX «P1»):
    /// 99% кадров приходят быстрее. Рывок — кадр дольше двойной медианы и минимум на 5 мс.
    /// </summary>
    public static FrameStats From(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0) return new FrameStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var sorted = frameTimesMs.Order().ToArray();
        var total = frameTimesMs.Sum();
        var median = Percentile(sorted, 0.5);
        var p99 = Percentile(sorted, 0.99);
        var p999 = Percentile(sorted, 0.999);
        var stutters = frameTimesMs.Count(ms => ms > median * 2 && ms - median > 5);
        return new FrameStats(
            frameTimesMs.Count, total / 1000, frameTimesMs.Count / (total / 1000),
            1000 / p99, 1000 / p999, median, p99, sorted[^1], stutters);
    }

    private static double Percentile(double[] sorted, double p)
    {
        var index = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(index);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
    }
}

/// <summary>Сохранённый замер кадров.</summary>
public sealed class FrameRun
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    public DateTime Started { get; set; } = DateTime.Now;
    public string Game { get; set; } = "";
    /// <summary>"before" или "after" — точка сравнения.</summary>
    public string Stage { get; set; } = "before";
    /// <summary>"capture" — замер программой, "csv" — импорт PresentMon/CapFrameX.</summary>
    public string Source { get; set; } = "capture";
    public FrameStats Stats { get; set; } = FrameStats.From([]);
    public List<double> FrameTimesMs { get; set; } = [];

    public static FrameRun Create(string game, string stage, string source, List<double> frameTimes) => new()
    {
        Game = game,
        Stage = stage,
        Source = source,
        Stats = FrameStats.From(frameTimes),
        FrameTimesMs = frameTimes.Select(ms => Math.Round(ms, 3)).ToList()
    };

    /// <summary>
    /// Разбор CSV из PresentMon (MsBetweenPresents или FrameTime) и CapFrameX.
    /// Столбец с именем процесса, если есть, становится названием игры.
    /// </summary>
    public static FrameRun FromCsv(string path, string stage)
    {
        using var reader = new StreamReader(path);
        var header = (reader.ReadLine() ?? "").Split(',').Select(h => h.Trim().Trim('"')).ToList();
        int column = header.FindIndex(h => h.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
        if (column < 0) column = header.FindIndex(h => h.Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
        if (column < 0) column = header.FindIndex(h => h.Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase));
        if (column < 0) throw new InvalidDataException("MsBetweenPresents / FrameTime");
        int app = header.FindIndex(h => h.Equals("Application", StringComparison.OrdinalIgnoreCase)
                                        || h.Equals("ProcessName", StringComparison.OrdinalIgnoreCase));

        var times = new List<double>();
        string? game = null;
        while (reader.ReadLine() is { } line)
        {
            var cells = line.Split(',');
            if (column >= cells.Length) continue;
            if (double.TryParse(cells[column].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 5000)
                times.Add(ms);
            if (game is null && app >= 0 && app < cells.Length) game = Path.GetFileNameWithoutExtension(cells[app].Trim('"'));
        }
        if (times.Count < 10) throw new InvalidDataException("frames < 10");
        return Create(game ?? Path.GetFileNameWithoutExtension(path), stage, "csv", times);
    }
}

/// <summary>Хранилище замеров кадров: %LOCALAPPDATA%\Velocity\frames.</summary>
public sealed class FrameStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _folder;

    public FrameStore(string? folder = null)
    {
        _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "frames");
    }

    public List<FrameRun> List()
    {
        if (!Directory.Exists(_folder)) return [];
        var runs = new List<FrameRun>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<FrameRun>(File.ReadAllText(file), Options) is { } run) runs.Add(run);
            }
            catch { /* повреждённый файл не ломает список */ }
        }
        return [.. runs.OrderByDescending(r => r.Started)];
    }

    public void Save(FrameRun run)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, run.Id + ".json"), JsonSerializer.Serialize(run, Options));
    }

    public void Delete(string id)
    {
        var path = Path.Combine(_folder, id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Пара для сравнения: последний «до» и последний «после» одной игры.</summary>
    public static (FrameRun? Before, FrameRun? After) Pair(IEnumerable<FrameRun> runs, string game)
    {
        var same = runs.Where(r => r.Game.Equals(game, StringComparison.OrdinalIgnoreCase)).ToList();
        return (same.Where(r => r.Stage == "before").MaxBy(r => r.Started),
                same.Where(r => r.Stage == "after").MaxBy(r => r.Started));
    }
}
