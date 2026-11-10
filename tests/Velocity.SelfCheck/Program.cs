using Velocity.Hardware.Cleanup;
using System.Diagnostics;
// Самопроверка перед выпуском.
//
// Это не модульные тесты: здесь проверяется, что каталог непротиворечив,
// сканер отрабатывает на настоящем железе, состояние твиков определяется,
// а применение и откат действительно возвращают систему в исходное состояние.
// Запускать на реальной машине: dotnet run --project tests/Velocity.SelfCheck
//
// Меняются только ключи HKCU, и каждый из них откатывается в конце проверки.

using Microsoft.Win32;
using Velocity.Hardware;
using Velocity.Tweaks;
using Velocity.Tweaks.Games;
using Velocity.Tweaks.Guides;
using Velocity.Tweaks.Startup;
using Velocity.Tweaks.Actions;
using Velocity.Hardware.Bench;

Console.OutputEncoding = System.Text.Encoding.UTF8;

int failures = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"  {(ok ? "OK  " : "СБОЙ")}  {what}{(detail is null ? "" : "  — " + detail)}");
    if (!ok) failures++;
}

Console.WriteLine("=== КАТАЛОГ ===");
var catalog = TweakCatalog.Load();
Check($"загружено твиков: {catalog.All.Count}", catalog.All.Count >= 85);
Check("нет ошибок разбора каталога", catalog.LoadErrors.Count == 0,
    string.Join("; ", catalog.LoadErrors));

var dupes = catalog.All.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
Check("нет повторяющихся идентификаторов", dupes.Count == 0, string.Join(", ", dupes));

var noPreset = catalog.All.Where(t => t.Presets.Count == 0).Select(t => t.Id).ToList();
Check("каждый твик входит хотя бы в один набор", noPreset.Count == 0, string.Join(", ", noPreset));

var noGain = catalog.All.Where(t => t.ExpectedGain is null).Select(t => t.Id).ToList();
Check("у каждого твика заявлен ожидаемый эффект", noGain.Count == 0, string.Join(", ", noGain));

// Необратимое применять нельзя: команда без описанного отката — ошибка каталога.
var irreversible = catalog.All
    .Where(t => t.Apply.OfType<CommandAction>().Any() && t.Revert.Count == 0)
    .Select(t => t.Id).ToList();
Check("у всех командных твиков описан откат", irreversible.Count == 0, string.Join(", ", irreversible));

Console.WriteLine("\n=== КАТАЛОГ ОЧИСТКИ ===");

// Опасные места: путь очистки, ведущий сюда, означает потерю данных пользователя.
string[] forbidden =
[
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    Environment.GetFolderPath(Environment.SpecialFolder.System),
    "C:\\"
];

var dangerous = new List<string>();
var shallow = new List<string>();

foreach (var target in CleanupCatalog.All)
foreach (var template in target.Paths)
{
    var resolved = Path.GetFullPath(CleanupCatalog.Resolve(template)).TrimEnd('\\');

    if (forbidden.Any(f => resolved.Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        dangerous.Add($"{target.Id} → {resolved}");

    // Путь из одного сегмента слишком близок к корню диска. Исключение —
    // известные папки распаковки установщиков и старая копия Windows.
    var depth = resolved.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
    if (depth < 2
        && !resolved.EndsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)
        && !resolved.EndsWith("AMD", StringComparison.OrdinalIgnoreCase)
        && !resolved.EndsWith("Windows.old", StringComparison.OrdinalIgnoreCase))
        shallow.Add($"{target.Id} → {resolved}");
}

Check($"категорий очистки: {CleanupCatalog.All.Count}", CleanupCatalog.All.Count >= 8);
Check("ни один путь не ведёт в личные папки пользователя", dangerous.Count == 0,
    string.Join("; ", dangerous));
Check("нет путей опасно близко к корню диска", shallow.Count == 0, string.Join("; ", shallow));

var noConsequence = CleanupCatalog.All.Where(t => t.Consequence is null).Select(t => t.Id).ToList();
Check("у каждой категории описаны последствия удаления", noConsequence.Count == 0,
    string.Join(", ", noConsequence));

// Поиск мусора запускается по кнопке, и пользователь ждёт результата на экране.
var cleanupWatch = Stopwatch.StartNew();
var junk = new CleanupScanner().Scan();
cleanupWatch.Stop();
Check($"поиск мусора за {cleanupWatch.ElapsedMilliseconds} мс", cleanupWatch.ElapsedMilliseconds < 5000);
Console.WriteLine($"        найдено к удалению: {junk.Sum(j => j.SizeBytes) / 1024 / 1024} МБ " +
                  $"в {junk.Count} категориях");

Console.WriteLine("\n=== ПРОФИЛИ ИГР ===");
var profiles = GameProfileEngine.Load();
Check($"загружено профилей: {profiles.Profiles.Count}", profiles.Profiles.Count >= 8);
Check("нет ошибок разбора профилей", profiles.LoadErrors.Count == 0,
    string.Join("; ", profiles.LoadErrors));

Console.WriteLine("\n=== СКАН ===");
var report = new HardwareScanner().Scan();
Check($"скан за {report.ScanDurationMs} мс", report.ScanDurationMs < 2000);
Check("нет ошибок сканеров", report.ScanErrors.Count == 0, string.Join("; ", report.ScanErrors));
Check("процессор определён", report.Cpu.Name is not null);
Check("видеокарта определена", report.Gpus.Count > 0);
Check("мониторы определены", report.Displays.Count > 0);

var facts = SystemFacts.From(report);
var engine = new TweakEngine(facts);

Console.WriteLine("\n=== ОПРЕДЕЛЕНИЕ СОСТОЯНИЯ ===");
var statuses = engine.GetStatuses(catalog.All);
var unknown = statuses.Where(s => s.State == TweakState.Unknown && s.Tweak.Detect is not null).ToList();
Check("состояние определяется у всех твиков с детектом", unknown.Count == 0,
    string.Join(", ", unknown.Select(u => u.Tweak.Id)));

foreach (var group in statuses.GroupBy(s => s.State).OrderBy(g => g.Key))
    Console.WriteLine($"        {group.Key,-15} {group.Count()}");

Console.WriteLine("\n=== РУКОВОДСТВА ===");
var guides = GuideCatalog.Load(catalog);
Check($"загружено руководств: {guides.All.Count}", guides.All.Count >= 30);
Check("нет ошибок разбора руководств", guides.LoadErrors.Count == 0, string.Join("; ", guides.LoadErrors));
var thin = guides.All.Where(g => g.Steps.Count < 3).Select(g => g.Id).ToList();
Check("в каждом руководстве не меньше трёх шагов", thin.Count == 0, string.Join(", ", thin));
var badLinks = guides.All.SelectMany(g => g.Tools.Select(t => (g.Id, t.Url)))
    .Where(x => !x.Url.StartsWith("https://") && !x.Url.StartsWith("http://")).Select(x => $"{x.Id}: {x.Url}").ToList();
Check("ссылки на утилиты — только http(s)", badLinks.Count == 0, string.Join("; ", badLinks));
var missingFacts = guides.All.Where(g => g.Status is { } st && facts.Get(st.Fact) is null)
    .Select(g => $"{g.Id} ({g.Status!.Fact})").ToList();
if (missingFacts.Count > 0)
    Console.WriteLine($"        без данных для статуса на этой машине: {string.Join(", ", missingFacts)}");
Console.WriteLine($"        подходит этой системе: {guides.Applicable(facts).Count}");

Console.WriteLine("\n=== СЕТЬ, ЗАДАЧИ, DNS ===");
var adapters = NetAdapters.Physical();
Check($"физических сетевых адаптеров: {adapters.Count}", adapters.Count > 0,
    "нет ни одного адаптера PCI/USB — правила сети будут считаться применёнными");
foreach (var a in adapters) Console.WriteLine($"        {a.Description}");
var interfaces = RegistryEachExecutor.Expand(new RegistryEachAction
    { Scope = "tcp_interfaces", Name = "TcpAckFrequency", Value = "1" });
Console.WriteLine($"        TCP-интерфейсов для правил Нейгла: {interfaces.Count}");
var msi = RegistryEachExecutor.Expand(new RegistryEachAction { Scope = "msi_display", Name = "MSISupported", Value = "1" });
Console.WriteLine($"        видеокарт с ключом MSI: {msi.Count}");

bool? appraiser = null;
string? taskError = null;
try { appraiser = TaskExecutor.ReadEnabled(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"); }
catch (Exception ex) { taskError = ex.Message; }
Check("планировщик заданий читается через COM", taskError is null, taskError);
Console.WriteLine($"        Compatibility Appraiser: {(appraiser is null ? "нет в этой сборке" : appraiser.Value ? "включена" : "выключена")}");

var query = NetworkProbe.BuildQuery(0x1234, "example.com");
Check("DNS-запрос собирается по формату", query.Length == 29 && query[0] == 0x12 && query[1] == 0x34 && query[12] == 7);
try
{
    var dns = NetworkProbe.BenchDnsAsync(NetworkProbe.PublicDns[0], rounds: 1).GetAwaiter().GetResult();
    Console.WriteLine($"        Cloudflare DNS: {(dns.Failed ? "нет ответа" : $"{dns.MedianMs:0} мс, ответов {dns.Answered}/{dns.Asked}")}");
}
catch (Exception ex) { Console.WriteLine($"        DNS недоступен: {ex.Message}"); }

Console.WriteLine("\n=== АВТОЗАГРУЗКА ===");
var startupWatch = Stopwatch.StartNew();
var startup = StartupManager.Scan();
startupWatch.Stop();
Check($"автозагрузка прочитана за {startupWatch.ElapsedMilliseconds} мс: {startup.Count} записей",
    startup.Count > 0 && startupWatch.ElapsedMilliseconds < 8000);
foreach (var group in startup.GroupBy(e => e.Kind))
    Console.WriteLine($"        {group.Key,-8} {group.Count(),3}  (включено {group.Count(e => e.Enabled)})");
foreach (var e in startup.Take(40))
    Console.WriteLine($"        [{e.Kind,-7}] {(e.Enabled ? "вкл " : "выкл")} {e.Advice,-8} {e.Name} · {e.Publisher}");
var toggles = startup.Select(e => StartupManager.BuildToggle(e, !e.Enabled)).ToList();
Check("переключатели собираются для каждой записи", toggles.Count == startup.Count && toggles.All(t => t.Apply.Count == 1));
var usbKeys = RegistryEachExecutor.Expand(new RegistryEachAction
    { Scope = "usb_devices", Name = "EnhancedPowerManagementEnabled", Value = "0", OnlyIfExists = true });
Console.WriteLine($"        USB-устройств с управлением питанием: {usbKeys.Count}");

Console.WriteLine("\n=== ОТКАТ УБИРАЕТ СОЗДАННЫЕ КЛЮЧИ ===");
const string probeRoot = @"Software\VelocitySelfCheck";
Registry.CurrentUser.DeleteSubKeyTree(probeRoot, throwOnMissingSubKey: false);
var probe = new TweakDefinition
{
    Id = "selfcheck.key_cleanup",
    Category = TweakCategory.Windows,
    Title = "Проверка очистки ключей",
    Description = "Создаёт значение в несуществующем ключе HKCU и откатывает его",
    RequiresGameClosed = false,
    Apply = [new RegistryAction { Hive = "HKCU", Path = probeRoot + @"\Deep\Leaf", Name = "Probe", Value = "1" }]
};
var probeSession = engine.Apply([probe], new TweakEngine.ApplyOptions { Reason = "Автопроверка ключей", IgnoreAntiCheat = true });
Check("значение в новом ключе создано", Peek(RegistryHive.CurrentUser, probeRoot + @"\Deep\Leaf", "Probe") == "1");
if (probeSession.SnapshotId is { } probeId)
{
    engine.Revert(probeId);
    using (var leftover = Registry.CurrentUser.OpenSubKey(probeRoot))
        Check("после отката не осталось пустых ключей", leftover is null);
    new SnapshotStore().Delete(probeId);
}
Registry.CurrentUser.DeleteSubKeyTree(probeRoot, throwOnMissingSubKey: false);

Console.WriteLine("\n=== ЗАМЕРЫ (короткий прогон) ===");
var bench = BenchmarkRunner.Run(new BenchOptions { Scale = 0.25, IncludeNetwork = false });
Check($"замеры без ошибок за {bench.DurationSeconds:0.0} с", bench.Errors.Count == 0, string.Join("; ", bench.Errors));
foreach (var m in bench.Metrics) Console.WriteLine($"        {m.Title,-40} {m.Value,10:0.##} {m.Unit}");
var memLatency = bench.Get("mem.latency")?.Value ?? 0;
Check("задержка памяти правдоподобна (20–300 нс)", memLatency is > 20 and < 300, $"{memLatency:0.0} нс");
var sleep = bench.Get("timer.sleep_p50")?.Value ?? 0;
Check("сон на 1 мс правдоподобен (0,9–20 мс)", sleep is > 900 and < 20_000, $"{sleep:0} мкс");

Console.WriteLine("\n=== ПРИМЕНЕНИЕ И ОТКАТ (только безопасное, без прав админа) ===");

string Peek(RegistryHive hive, string path, string name)
{
    using var k = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path);
    return k?.GetValue(name)?.ToString() ?? "<нет>";
}

// Берём только твики, которые пишут в HKCU: их можно применить и откатить без админа.
var subject = catalog.All
    .Where(t => !t.RequiresElevation
             && t.Apply.All(a => a is RegistryAction r && r.Hive == "HKCU")
             && facts.MatchesAll(t.Requires))
    .ToList();

Console.WriteLine($"  выбрано для проверки: {subject.Count}");

var targets = subject.SelectMany(t => t.Apply.OfType<RegistryAction>())
    .Select(a => (a.Path, a.Name)).Distinct().ToList();
var before = targets.ToDictionary(t => t, t => Peek(RegistryHive.CurrentUser, t.Path, t.Name));

// Самопроверка меняет настоящие значения пользователя. Что бы ни случилось дальше,
// при выходе она откатывает все свои снимки — система не должна остаться изменённой.
var selfCheckStarted = DateTimeOffset.Now.AddSeconds(-1);
AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupSelfCheckSnapshots();
AppDomain.CurrentDomain.UnhandledException += (_, _) => CleanupSelfCheckSnapshots();
void CleanupSelfCheckSnapshots()
{
    try
    {
        var store = new SnapshotStore();
        foreach (var leftover in store.List().Where(x => x.Reason.StartsWith("Автопроверка") && x.CreatedAt >= selfCheckStarted))
        {
            if (!leftover.Reverted) engine.Revert(leftover.Id);
            store.Delete(leftover.Id);
        }
    }
    catch { }
}

var session = engine.Apply(subject, new TweakEngine.ApplyOptions { Reason = "Автопроверка" });
Check("применение не прервано", session.AbortReason is null, session.AbortReason);
Check($"применено {session.Results.Count(r => r.Outcome == ApplyOutcome.Applied)} из {subject.Count}",
    session.Results.All(r => r.Outcome is ApplyOutcome.Applied or ApplyOutcome.Skipped),
    string.Join("; ", session.Results.Where(r => r.Outcome is ApplyOutcome.Failed or ApplyOutcome.VerifyFailed)
        .Select(r => $"{r.TweakId}: {r.Message}")));

int changed = targets.Count(t => Peek(RegistryHive.CurrentUser, t.Path, t.Name) != before[t]);
Check($"значений реально изменилось: {changed}", changed > 0);

if (session.SnapshotId is { } id)
{
    var reverted = engine.Revert(id);
    Check("откат без ошибок", reverted.All(r => r.Outcome == ApplyOutcome.Applied),
        string.Join("; ", reverted.Where(r => r.Outcome != ApplyOutcome.Applied).Select(r => r.Message)));

    var mismatched = targets
        .Where(t => Peek(RegistryHive.CurrentUser, t.Path, t.Name) != before[t])
        .Select(t => $"{t.Path}\\{t.Name}")
        .ToList();
    Check("система вернулась в исходное состояние", mismatched.Count == 0,
        string.Join(", ", mismatched));

    new SnapshotStore().Delete(id);
}

Console.WriteLine("\n=== ЗАМЕР КАДРОВ В ИГРАХ ===");
{
    // Разбор структур ETW проверяется на готовом файле трассировки: прав администратора не нужно.
    var etl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Explorer\ExplorerStartupLog.etl");
    if (File.Exists(etl))
    {
        var events = new List<EtwEvent>();
        int read = 0;
        try { read = Etw.ReadFile(etl, e => { if (events.Count < 5000) events.Add(e); }); }
        catch (Exception ex) { Check("чтение трассировки .etl", false, ex.Message); }
        var stamps = events.Select(e => e.Timestamp).ToList();
        Check($"события ETW читаются: {read}", read > 0);
        Check("у событий есть поставщик, процесс и время",
            events.Count > 0 && events.All(e => e.Provider != Guid.Empty && e.Timestamp > 0) && events.Any(e => e.ProcessId > 4),
            events.Count > 0 ? $"pid {events[0].ProcessId}, id {events[0].Id}, {events[0].Provider}" : null);
        Check("метки времени не убывают", stamps.Zip(stamps.Skip(1)).All(p => p.Second >= p.First));
    }
    else Console.WriteLine("  файла трассировки нет — разбор ETW не проверен");

    var synthetic = Enumerable.Range(0, 1000).Select(i => i % 50 == 0 ? 25.0 : 4.0).ToList();
    var stats = FrameStats.From(synthetic);
    Check($"расчёт FPS: средний {stats.AvgFps:0}, 1% low {stats.Low1Fps:0}, рывков {stats.Stutters}",
        Math.Abs(stats.MedianMs - 4) < 0.01 && stats.Stutters == 20 && stats.AvgFps > 200 && stats.AvgFps < 250 && stats.Low1Fps < 60);

    var csv = Path.Combine(Path.GetTempPath(), "velocity-selfcheck-presentmon.csv");
    File.WriteAllLines(csv, ["Application,ProcessID,MsBetweenPresents",
        .. Enumerable.Range(0, 200).Select(i => $"cs2.exe,123,{(i % 50 == 0 ? 16.6 : 6.9).ToString(System.Globalization.CultureInfo.InvariantCulture)}")]);
    var imported = FrameRun.FromCsv(csv, "before");
    File.Delete(csv);
    Check($"импорт CSV PresentMon: {imported.Game}, {imported.Stats.Frames} кадров", imported.Game == "cs2" && imported.Stats.Frames == 200);

    if (TweakEngine.IsElevated())
    {
        try
        {
            using var capture = FrameCapture.Start();
            Thread.Sleep(2000);
            var processes = capture.Stop();
            Check($"захват кадров в реальном времени: процессов {processes.Count}", true,
                processes.Count > 0 ? $"{processes[0].Name}: {processes[0].FrameTimesMs.Count} кадров" : "никто не рисовал");
        }
        catch (Exception ex) { Check("захват кадров в реальном времени", false, ex.Message); }
    }
    else Console.WriteLine("  без прав администратора захват в реальном времени не проверяется");
}

Console.WriteLine("\n=== ПЕРЕВОД НА АНГЛИЙСКИЙ ===");
foreach (var (what, ok, detail) in TranslationCheck.Run())
    Check(what, ok, detail);

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ"
    : $"ПРОВАЛЕНО ПРОВЕРОК: {failures}");
return failures == 0 ? 0 : 1;
