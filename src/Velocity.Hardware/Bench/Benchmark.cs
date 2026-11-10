using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Velocity.Hardware.Bench;

/// <summary>Одна измеренная величина. Сравнение «до/после» идёт по <see cref="Key"/>.</summary>
public sealed class BenchMetric
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public required string Unit { get; init; }
    public double Value { get; init; }
    public bool LowerIsBetter { get; init; }
    /// <summary>Что влияет на эту цифру — чтобы человек понимал, какая настройка её двигает.</summary>
    public string? Hint { get; init; }
    /// <summary>
    /// Разброс между повторами в процентах. Разница «до/после» меньше шума —
    /// это не результат, и интерфейс так и пишет.
    /// </summary>
    public double NoisePercent { get; init; }
}

public sealed class BenchResult
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string Label { get; set; } = "";
    public List<BenchMetric> Metrics { get; init; } = [];
    /// <summary>Условия замера: схема питания, разрешение таймера, питание от сети.</summary>
    public Dictionary<string, string> Context { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public double DurationSeconds { get; set; }

    public BenchMetric? Get(string key) => Metrics.FirstOrDefault(m => m.Key == key);
}

public sealed record BenchOptions
{
    public bool IncludeNetwork { get; init; } = true;
    /// <summary>Множитель длительности тестов. Самопроверка гоняет короткую версию.</summary>
    public double Scale { get; init; } = 1.0;
    public Dictionary<string, string> Context { get; init; } = [];
}

/// <summary>
/// Замеры системы до и после оптимизации.
///
/// Это не синтетический «рейтинг», а величины, которые прямо двигают настройки программы:
/// схема питания и C-States меняют задержку пробуждения потоков, таймеры — точность сна,
/// XMP и тайминги — задержку памяти, энергосбережение сети — разброс пинга.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BenchmarkRunner
{
    private const string GroupLatency = "Отклик системы";
    private const string GroupCpu = "Процессор";
    private const string GroupMemory = "Память";
    private const string GroupNetwork = "Сеть";

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    public static BenchResult Run(BenchOptions options, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var result = new BenchResult
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Context = new Dictionary<string, string>(options.Context)
        };

        void Step(string what, Action body)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(what);
            try { body(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Errors.Add($"{what}: {ex.Message}"); }
        }

        Step("Точность системного таймера", () => result.Metrics.AddRange(TimerPrecision(options.Scale)));
        Step("Задержка пробуждения потоков", () => result.Metrics.AddRange(WakeLatency(options.Scale)));
        Step("Производительность процессора", () => result.Metrics.AddRange(CpuThroughput(options.Scale)));
        Step("Задержка памяти", () => result.Metrics.Add(MemoryLatency(options.Scale)));
        Step("Пропускная способность памяти", () => result.Metrics.Add(MemoryBandwidth(options.Scale)));
        if (options.IncludeNetwork)
            Step("Пинг до роутера и интернета", () => result.Metrics.AddRange(Network(options.Scale, result.Errors, ct)));

        result.DurationSeconds = started.Elapsed.TotalSeconds;
        return result;
    }

    // ───────────────────────────── Таймер ─────────────────────────────

    /// <summary>
    /// Sleep(1) при запрошенном разрешении 1 мс — ровно то, чем пользуются ограничители кадров.
    /// Идеал — 1000 мкс. Всё, что сверху, и разброс — это то, насколько неточно система будит поток.
    /// </summary>
    private static IEnumerable<BenchMetric> TimerPrecision(double scale)
    {
        int count = Math.Max(100, (int)(600 * scale));
        var samples = new double[count];

        timeBeginPeriod(1);
        try
        {
            for (int i = 0; i < 20; i++) Thread.Sleep(1);
            for (int i = 0; i < count; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                Thread.Sleep(1);
                samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            }
        }
        finally { timeEndPeriod(1); }

        Array.Sort(samples);
        // p95, а не p99: единичные просыпания на 2–3 мс бывают у любой системы
        // и делают сравнение «до/после» случайным.
        double p50 = Percentile(samples, 0.50), p95 = Percentile(samples, 0.95);

        yield return new BenchMetric
        {
            Key = "timer.sleep_p50",
            Group = GroupLatency,
            Title = "Сон на 1 мс длится (медиана)",
            Unit = "мкс",
            Value = p50,
            LowerIsBetter = true,
            Hint = "Идеал — 1000 мкс. Зависит от разрешения таймера и dynamic tick",
            NoisePercent = 3
        };
        yield return new BenchMetric
        {
            Key = "timer.sleep_spread",
            Group = GroupLatency,
            Title = "Разброс сна на 1 мс (p95 − медиана)",
            Unit = "мкс",
            Value = Math.Max(0, p95 - p50),
            LowerIsBetter = true,
            Hint = "Неравномерность пробуждения: таймеры, фоновые задачи, драйверы",
            NoisePercent = 35
        };
    }

    // ───────────────────────── Пробуждение потока ─────────────────────────

    /// <summary>
    /// Один поток будит другой через событие ядра, между пробуждениями — пауза, за которую
    /// ядро успевает уснуть. Время от сигнала до реакции включает выход ядра из C-State,
    /// работу планировщика и задержки драйверов (DPC/ISR). Именно эту цифру двигают
    /// схема питания, C-States и «Запретить ядрам засыпать».
    /// </summary>
    private static IEnumerable<BenchMetric> WakeLatency(double scale)
    {
        int count = Math.Max(200, (int)(1200 * scale));
        var samples = new double[count];
        using var signal = new AutoResetEvent(false);
        using var done = new AutoResetEvent(false);
        long signaledAt = 0;

        var waiter = new Thread(() =>
        {
            // Исключение в фоновом потоке роняет весь процесс, поэтому ловим всё:
            // если замер прервался, главный поток сам сообщит об ошибке по таймауту.
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (!signal.WaitOne(5000)) return;
                    long now = Stopwatch.GetTimestamp();
                    samples[i] = Stopwatch.GetElapsedTime(Volatile.Read(ref signaledAt), now).TotalMicroseconds;
                    done.Set();
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "velocity-bench-wake" };

        timeBeginPeriod(1);
        try
        {
            waiter.Start();
            var random = new Random(42);
            for (int i = 0; i < count; i++)
            {
                // Пауза 1–2 мс: достаточно, чтобы ядро ожидающего потока ушло в простой.
                Thread.Sleep(1 + random.Next(2));
                Volatile.Write(ref signaledAt, Stopwatch.GetTimestamp());
                signal.Set();
                if (!done.WaitOne(1000)) throw new TimeoutException("поток не проснулся за секунду");
            }
            waiter.Join(2000);
        }
        finally { timeEndPeriod(1); }

        Array.Sort(samples);
        yield return new BenchMetric
        {
            Key = "wake.p50",
            Group = GroupLatency,
            Title = "Пробуждение потока (медиана)",
            Unit = "мкс",
            Value = Percentile(samples, 0.50),
            LowerIsBetter = true,
            Hint = "Схема питания, C-States, минимальное состояние процессора",
            NoisePercent = 10
        };
        yield return new BenchMetric
        {
            Key = "wake.p99",
            Group = GroupLatency,
            Title = "Пробуждение потока (1% худших)",
            Unit = "мкс",
            Value = Percentile(samples, 0.99),
            LowerIsBetter = true,
            Hint = "Редкие задержки: драйверы (DPC/ISR), глубокий сон ядер, фоновые службы",
            NoisePercent = 25
        };
    }

    // ───────────────────────────── Процессор ─────────────────────────────

    private static IEnumerable<BenchMetric> CpuThroughput(double scale)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0.4, 1.5 * scale));

        // Прогрев: процессору нужно время выйти на буст, JIT — на оптимизированный код.
        RunFor(TimeSpan.FromMilliseconds(300));

        double single = RunFor(duration) / duration.TotalSeconds / 1e6;

        int threads = Environment.ProcessorCount;
        var totals = new double[threads];
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads },
            i => totals[i] = RunFor(duration));
        double multi = totals.Sum() / duration.TotalSeconds / 1e6;

        yield return new BenchMetric
        {
            Key = "cpu.single",
            Group = GroupCpu,
            Title = "Одно ядро",
            Unit = "млн оп/с",
            Value = single,
            Hint = "Частота буста: схема питания, андервольт, Curve Optimizer, охлаждение",
            NoisePercent = 3
        };
        yield return new BenchMetric
        {
            Key = "cpu.multi",
            Group = GroupCpu,
            Title = "Все ядра",
            Unit = "млн оп/с",
            Value = multi,
            Hint = "Частота под полной нагрузкой и SMT. Чувствительна к фоновым процессам",
            NoisePercent = 4
        };
    }

    /// <summary>Целочисленная нагрузка с ветвлениями. Возвращает число выполненных итераций.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long RunFor(TimeSpan duration)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        ulong x = 88172645463325252UL, acc = 0;
        long iterations = 0;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            for (int i = 0; i < 200_000; i++)
            {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                acc += (x % 7 == 0) ? x >> 3 : x & 0xFF;
            }
            iterations += 200_000;
        }

        // Результат должен «использоваться», иначе JIT вправе выкинуть цикл.
        if (acc == 42) Console.Write("");
        return iterations;
    }

    // ───────────────────────────── Память ─────────────────────────────

    /// <summary>
    /// Переход по случайной цепочке внутри буфера в 256 МБ — больше любого кэша процессора,
    /// включая 3D V-Cache. Каждый шаг зависит от предыдущего, поэтому время шага —
    /// это задержка памяти. Абсолютное значение чуть выше, чем у AIDA64 (страницы 4 КБ),
    /// но для сравнения «до/после» на одной машине годится.
    /// </summary>
    private static unsafe BenchMetric MemoryLatency(double scale)
    {
        const int bufferBytes = 256 * 1024 * 1024;
        const int lineInts = 64 / sizeof(int);
        int lines = bufferBytes / 64;

        var buffer = GC.AllocateUninitializedArray<int>(bufferBytes / sizeof(int), pinned: true);
        try
        {
            // Случайный цикл по строкам кэша (алгоритм Саттоло: один цикл через все элементы).
            var order = new int[lines];
            for (int i = 0; i < lines; i++) order[i] = i;
            var random = new Random(7);
            for (int i = lines - 1; i > 0; i--)
            {
                int j = random.Next(i);
                (order[i], order[j]) = (order[j], order[i]);
            }
            for (int i = 0; i < lines; i++)
                buffer[order[i] * lineInts] = order[(i + 1) % lines] * lineInts;
            order = null!;

            long hops = Math.Max(2_000_000, (long)(12_000_000 * scale));
            double best = double.MaxValue;
            var runs = new List<double>();

            fixed (int* p = buffer)
            {
                for (int run = 0; run < 3; run++)
                {
                    int index = 0;
                    long t0 = Stopwatch.GetTimestamp();
                    for (long h = 0; h < hops; h++) index = p[index];
                    double ns = Stopwatch.GetElapsedTime(t0).TotalNanoseconds / hops;
                    if (index == -1) Console.Write("");
                    runs.Add(ns);
                    best = Math.Min(best, ns);
                }
            }

            return new BenchMetric
            {
                Key = "mem.latency",
                Group = GroupMemory,
                Title = "Задержка памяти",
                Unit = "нс",
                Value = best,
                LowerIsBetter = true,
                Hint = "XMP/EXPO, тайминги, двухканальный режим",
                NoisePercent = Math.Max(2, Spread(runs))
            };
        }
        finally
        {
            buffer = null!;
            GC.Collect();
        }
    }

    private static unsafe BenchMetric MemoryBandwidth(double scale)
    {
        const int bytes = 128 * 1024 * 1024;
        var src = GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        var dst = GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        try
        {
            new Random(3).NextBytes(src.AsSpan(0, 4096));
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 16);
            int chunk = bytes / threads;
            int passes = Math.Max(3, (int)(8 * scale));

            fixed (byte* s = src)
            fixed (byte* d = dst)
            {
                var sp = (nint)s;
                var dp = (nint)d;

                // Прогрев: первая запись в страницы — это ещё и их выделение системой.
                Parallel.For(0, threads, i => Buffer.MemoryCopy((void*)(sp + i * chunk), (void*)(dp + i * chunk), chunk, chunk));

                double best = 0;
                var runs = new List<double>();
                for (int pass = 0; pass < passes; pass++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads },
                        i => Buffer.MemoryCopy((void*)(sp + i * chunk), (void*)(dp + i * chunk), chunk, chunk));
                    double gbps = bytes / Stopwatch.GetElapsedTime(t0).TotalSeconds / 1e9;
                    runs.Add(gbps);
                    best = Math.Max(best, gbps);
                }

                return new BenchMetric
                {
                    Key = "mem.copy",
                    Group = GroupMemory,
                    Title = "Копирование памяти",
                    Unit = "ГБ/с",
                    Value = best,
                    Hint = "Частота памяти и число каналов",
                    NoisePercent = Math.Max(3, Spread(runs))
                };
            }
        }
        finally
        {
            src = null!;
            dst = null!;
            GC.Collect();
        }
    }

    // ───────────────────────────── Сеть ─────────────────────────────

    private static IEnumerable<BenchMetric> Network(double scale, List<string> errors, CancellationToken ct)
    {
        int count = Math.Max(15, (int)(40 * scale));
        var gateway = NetworkProbe.DefaultGateway();
        var tasks = new List<Task<PingStats>>();

        if (gateway is not null)
            tasks.Add(NetworkProbe.PingAsync("Роутер", gateway, count, 50, ct));
        tasks.Add(NetworkProbe.PingAsync("Интернет", System.Net.IPAddress.Parse("1.1.1.1"), count, 50, ct));
        Task.WaitAll([.. tasks], ct);

        var list = new List<BenchMetric>();
        var router = gateway is null ? null : tasks[0].Result;
        var internet = tasks[^1].Result;

        if (router is not null && router.Received > 0)
        {
            list.Add(new BenchMetric
            {
                Key = "net.router_jitter", Group = GroupNetwork, Title = "Разброс пинга до роутера", Unit = "мс",
                Value = router.JitterMs, LowerIsBetter = true,
                Hint = "Wi-Fi против кабеля, энергосбережение сетевой карты", NoisePercent = 30
            });
        }
        // На ping ответил кто-то на этом компьютере (VPN в режиме TUN): цифры интернета
        // ничего не значат, записывать их в историю — значит потом сравнивать мусор.
        if (NetworkProbe.LooksIntercepted(router, internet))
        {
            var who = NetworkProbe.TrafficInterceptors();
            errors.Add("пинг до интернета перехвачен локально" +
                       (who.Count > 0 ? $" ({string.Join(", ", who)})" : " (VPN или прокси)") +
                       " — показатели интернета не записаны");
            return list;
        }

        if (internet.Received > 0)
        {
            list.Add(new BenchMetric
            {
                Key = "net.inet_avg", Group = GroupNetwork, Title = "Пинг до интернета (1.1.1.1)", Unit = "мс",
                Value = internet.AvgMs, LowerIsBetter = true,
                Hint = "Провайдер и маршрут. Настройки Windows влияют мало", NoisePercent = 10
            });
            list.Add(new BenchMetric
            {
                Key = "net.inet_jitter", Group = GroupNetwork, Title = "Разброс пинга до интернета", Unit = "мс",
                Value = internet.JitterMs, LowerIsBetter = true,
                Hint = "Загрузка канала дома (bufferbloat), Wi-Fi", NoisePercent = 30
            });
        }
        list.Add(new BenchMetric
        {
            Key = "net.loss", Group = GroupNetwork, Title = "Потери пакетов", Unit = "%",
            Value = internet.LossPercent, LowerIsBetter = true,
            Hint = "Wi-Fi, кабель, роутер, провайдер", NoisePercent = 0
        });
        return list;
    }

    // ───────────────────────────── Служебное ─────────────────────────────

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Clamp(Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>Разброс повторов в процентах от среднего.</summary>
    private static double Spread(List<double> runs)
    {
        if (runs.Count < 2) return 0;
        var avg = runs.Average();
        return avg <= 0 ? 0 : (runs.Max() - runs.Min()) / avg * 100;
    }
}

/// <summary>История замеров на диске: %LocalAppData%\Velocity\benchmarks.</summary>
public sealed class BenchStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string RootPath { get; }

    public BenchStore(string? root = null)
    {
        RootPath = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "benchmarks");
        Directory.CreateDirectory(RootPath);
    }

    public void Save(BenchResult result)
    {
        var path = Path.Combine(RootPath, result.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, Json));
    }

    public List<BenchResult> List()
    {
        var list = new List<BenchResult>();
        foreach (var file in Directory.EnumerateFiles(RootPath, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BenchResult>(File.ReadAllText(file), Json) is { } r) list.Add(r);
            }
            catch { /* повреждённый файл не роняет историю */ }
        }
        return [.. list.OrderBy(r => r.CreatedAt)];
    }

    public void Delete(string id)
    {
        var path = Path.Combine(RootPath, id + ".json");
        if (File.Exists(path)) File.Delete(path);
        if (BaselineId == id) BaselineId = null;
    }

    /// <summary>Замер, с которым сравниваются остальные («до»). Хранится отдельным файлом.</summary>
    public string? BaselineId
    {
        get
        {
            try
            {
                var path = Path.Combine(RootPath, "baseline.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch { return null; }
        }
        set
        {
            try
            {
                var path = Path.Combine(RootPath, "baseline.txt");
                if (value is null) { if (File.Exists(path)) File.Delete(path); }
                else File.WriteAllText(path, value);
            }
            catch { }
        }
    }
}
