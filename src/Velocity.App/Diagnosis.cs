using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Velocity.Core;
using Velocity.Hardware.Bench;
using Velocity.Tweaks;
using Velocity.Tweaks.Guides;

namespace Velocity.App;

/// <summary>
/// Разбор ПК — разовая работа: проверить, замерить «до», исправить, замерить «после»,
/// получить отчёт. Сессия помнит исходное состояние, чтобы отчёт показал разницу,
/// даже если между шагами была перезагрузка.
/// </summary>
public sealed class DiagnosisSession
{
    public DateTime Started { get; set; } = DateTime.Now;
    public int InitialScore { get; set; }
    public List<string> InitialFindings { get; set; } = [];
    public DateTime? ReportCreated { get; set; }

    private static string FilePath => Path.Combine(AppSettings.Folder, "session.json");

    public static DiagnosisSession? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<DiagnosisSession>(File.ReadAllText(FilePath))
                : null;
        }
        catch { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* отчёт всё равно соберётся по текущему состоянию */ }
    }

    /// <summary>Текущая сессия; при первом скане создаётся с исходным состоянием системы.</summary>
    public static DiagnosisSession Ensure(HardwareReport report)
    {
        if (Load() is { } existing) return existing;
        var session = StartNew(report);
        session.Save();
        return session;
    }

    public static DiagnosisSession StartNew(HardwareReport report) => new()
    {
        Started = DateTime.Now,
        InitialScore = report.Score,
        InitialFindings = report.Findings
            .Where(f => f.Severity != FindingSeverity.Info)
            .Select(f => f.Title).ToList()
    };
}

/// <summary>Что из ручных шагов действительно нужно этой системе.</summary>
public static class DiagnosisPlan
{
    /// <summary>
    /// Ручные пункты каталога, подходящие системе (XMP, слоты памяти, кабель монитора),
    /// и руководства, у которых скан видит «не в порядке» (старый BIOS, выключенный SMT и т. п.).
    /// </summary>
    public static List<Guide> ManualGuides(AppState state)
    {
        if (state.Facts is not { } facts) return [];
        return state.Guides.Applicable(facts)
            .Where(g => (g.SourceTweakId is not null && g.Requires.Any(r => !r.StartsWith("machine.", StringComparison.Ordinal)))
                        || (g.Status is { Good: { } good } && !facts.Matches(good)))
            .ToList();
    }

    /// <summary>Находки, которые программа исправит сама.</summary>
    public static List<Finding> AutoFixable(AppState state) => state.Report?.Findings
        .Where(f => f.AutoFixable && state.Catalog.ById(f.FixTweakId!) is not null)
        .ToList() ?? [];
}

/// <summary>
/// Отчёт о разборе в HTML: открывается в любом браузере, печатается в PDF,
/// отправляется другу или в поддержку. Только факты и цифры, без украшательства.
/// </summary>
public static class ReportBuilder
{
    public static string Build(AppState state, DiagnosisSession session, GameChecklistStore progress)
    {
        var r = state.Report!;
        var bench = new BenchStore();
        var runs = bench.List();
        var baseline = runs.FirstOrDefault(x => x.Id == bench.BaselineId) ?? runs.LastOrDefault();
        var latest = runs.FirstOrDefault(x => x.Id != baseline?.Id && x.CreatedAt > (baseline?.CreatedAt ?? DateTimeOffset.MinValue));
        var frames = new FrameStore().List();
        var changes = new SnapshotStore().List()
            .Where(s => s.CreatedAt.LocalDateTime >= session.Started && !s.Reverted)
            .SelectMany(s => s.Changes.Where(c => !c.Reverted))
            .GroupBy(c => c.TweakId).Select(g => g.First().TweakTitle).ToList();
        var current = r.Findings.Where(f => f.Severity != FindingSeverity.Info).ToList();
        var fixedCount = session.InitialFindings.Count(t => current.All(f => f.Title != t));

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(Loc.Language).Append("\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(Loc.T("Отчёт VELOCITY"))).Append("</title><style>").Append(Css).Append("</style></head><body><main>");

        // ── Шапка
        sb.Append("<header><div class=\"brand\">VELOCITY</div><h1>").Append(E(Loc.T("Отчёт о разборе ПК"))).Append("</h1>");
        sb.Append("<p class=\"muted\">").Append(E(Loc.F("Начат {0:dd.MM.yyyy HH:mm} · отчёт {1:dd.MM.yyyy HH:mm}", session.Started, DateTime.Now))).Append("</p></header>");

        // ── Итог
        sb.Append("<section class=\"tiles\">");
        Tile(sb, Loc.T("Оценка готовности"), $"{session.InitialScore} → {r.Score}", r.Score >= session.InitialScore ? "good" : "bad");
        Tile(sb, Loc.T("Проблем найдено"), session.InitialFindings.Count.ToString(CultureInfo.InvariantCulture), "");
        Tile(sb, Loc.T("Устранено"), fixedCount.ToString(CultureInfo.InvariantCulture), fixedCount > 0 ? "good" : "");
        Tile(sb, Loc.T("Настроек применено"), changes.Count.ToString(CultureInfo.InvariantCulture), "");
        sb.Append("</section>");

        // ── Конфигурация
        sb.Append("<h2>").Append(E(Loc.T("Конфигурация"))).Append("</h2><table class=\"kv\">");
        Row(sb, Loc.T("Процессор"), $"{r.Cpu.Name} · {r.Cpu.PhysicalCores}C/{r.Cpu.LogicalCores}T");
        Row(sb, Loc.T("Память"), Loc.F("{0:0.#} ГБ {1} @ {2} МГц · {3}-канал", r.Memory.TotalGb, r.Memory.Kind, r.Memory.ConfiguredSpeedMhz, r.Memory.Channels));
        foreach (var gpu in r.Gpus.Where(g => !g.IsIntegrated || r.Gpus.Count == 1))
            Row(sb, Loc.T("Видеокарта"), $"{gpu.Name} · {gpu.VramGb:0} GB · {gpu.VendorDriverVersion ?? gpu.DriverVersion}");
        Row(sb, Loc.T("Материнская плата"), $"{r.Machine.BoardManufacturer} {r.Machine.BoardProduct} · BIOS {r.Machine.BiosVersion} ({r.Machine.BiosDate:dd.MM.yyyy})");
        Row(sb, "Windows", $"{r.Os.ProductName} {r.Os.DisplayVersion} · build {r.Os.Build}.{r.Os.Ubr}");
        foreach (var d in r.Displays)
            Row(sb, Loc.T("Монитор"), $"{d.FriendlyName ?? d.DeviceName} · {d.Width}×{d.Height} @ {d.RefreshHz} Hz");
        sb.Append("</table>");

        // ── Замеры системы
        if (baseline is not null)
        {
            sb.Append("<h2>").Append(E(Loc.T("Отклик системы: до и после"))).Append("</h2>");
            if (latest is null)
                sb.Append("<p class=\"muted\">").Append(E(Loc.T("Есть только замер «до». Повтори замер после оптимизации, чтобы увидеть разницу."))).Append("</p>");
            sb.Append("<table><tr><th>").Append(E(Loc.T("Показатель"))).Append("</th><th>").Append(E(Loc.T("До"))).Append("</th>");
            if (latest is not null) sb.Append("<th>").Append(E(Loc.T("После"))).Append("</th><th>").Append(E(Loc.T("Изменение"))).Append("</th>");
            sb.Append("</tr>");
            foreach (var m in baseline.Metrics)
            {
                sb.Append("<tr><td>").Append(E(Loc.T(m.Title))).Append("</td><td>").Append(E(Num(m.Value))).Append(' ').Append(E(Loc.T(m.Unit))).Append("</td>");
                if (latest?.Get(m.Key) is { } after && m.Value > 0)
                {
                    var change = (after.Value - m.Value) / m.Value * 100;
                    var better = m.LowerIsBetter ? change < -2 : change > 2;
                    var worse = m.LowerIsBetter ? change > 2 : change < -2;
                    sb.Append("<td>").Append(E(Num(after.Value))).Append(' ').Append(E(Loc.T(after.Unit))).Append("</td><td class=\"")
                      .Append(better ? "good" : worse ? "bad" : "muted").Append("\">")
                      .Append(E(Math.Abs(change) < 2 ? Loc.T("без изменений") : $"{change:+0;-0}%")).Append("</td>");
                }
                else if (latest is not null) sb.Append("<td>—</td><td></td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        // ── FPS в играх
        var games = frames.Select(f => f.Game).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (games.Count > 0)
        {
            sb.Append("<h2>").Append(E(Loc.T("FPS в играх"))).Append("</h2><table><tr><th>").Append(E(Loc.T("Игра"))).Append("</th><th>")
              .Append(E(Loc.T("Этап"))).Append("</th><th>").Append(E(Loc.T("Средний FPS"))).Append("</th><th>1% low</th><th>0.1% low</th><th>")
              .Append(E(Loc.T("Рывки"))).Append("</th></tr>");
            foreach (var game in games)
            {
                var (before, after) = FrameStore.Pair(frames, game);
                foreach (var (run, stage) in new[] { (before, Loc.T("До")), (after, Loc.T("После")) })
                {
                    if (run is null) continue;
                    var s = run.Stats;
                    sb.Append("<tr><td>").Append(E(game)).Append("</td><td>").Append(E(stage)).Append("</td><td>")
                      .Append(s.AvgFps.ToString("0", Loc.Culture)).Append("</td><td>").Append(s.Low1Fps.ToString("0", Loc.Culture))
                      .Append("</td><td>").Append(s.Low01Fps.ToString("0", Loc.Culture)).Append("</td><td>").Append(s.Stutters).Append("</td></tr>");
                }
                if (before is not null && after is not null && before.Stats.AvgFps > 0)
                {
                    double d(double a, double b) => b > 0 ? (a - b) / b * 100 : 0;
                    sb.Append("<tr class=\"delta\"><td></td><td>").Append(E(Loc.T("Разница"))).Append("</td><td>")
                      .Append($"{d(after.Stats.AvgFps, before.Stats.AvgFps):+0;-0}%</td><td>{d(after.Stats.Low1Fps, before.Stats.Low1Fps):+0;-0}%</td><td>")
                      .Append($"{d(after.Stats.Low01Fps, before.Stats.Low01Fps):+0;-0}%</td><td>{after.Stats.Stutters - before.Stats.Stutters:+0;-0;0}</td></tr>");
                }
            }
            sb.Append("</table>");
        }

        // ── Что сделано
        sb.Append("<h2>").Append(E(Loc.T("Что изменено автоматически"))).Append("</h2>");
        if (changes.Count == 0) sb.Append("<p class=\"muted\">").Append(E(Loc.T("За время разбора настройки не применялись."))).Append("</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var title in changes) sb.Append("<li>").Append(E(title)).Append("</li>");
            sb.Append("</ul><p class=\"muted\">").Append(E(Loc.T("Всё это откатывается в VELOCITY → «Точки отката»."))).Append("</p>");
        }

        // ── Что осталось
        sb.Append("<h2>").Append(E(Loc.T("Что осталось"))).Append("</h2>");
        if (current.Count == 0) sb.Append("<p class=\"good\">").Append(E(Loc.T("Найденных проблем не осталось."))).Append("</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var f in current)
                sb.Append("<li><b>").Append(E(f.Title)).Append("</b>")
                  .Append(f.Recommendation is null ? "" : " — " + E(f.Recommendation)).Append("</li>");
            sb.Append("</ul>");
        }

        var manual = DiagnosisPlan.ManualGuides(state);
        if (manual.Count > 0)
        {
            sb.Append("<h2>").Append(E(Loc.T("Ручные шаги, которые стоит сделать"))).Append("</h2><ul>");
            foreach (var g in manual)
            {
                var done = Enumerable.Range(0, g.Steps.Count).Count(i => progress.IsComplete($"{g.Id}#{i}"));
                sb.Append("<li><b>").Append(E(g.Title)).Append("</b> — ").Append(E(g.Gain))
                  .Append(" <span class=\"muted\">(").Append(E(Loc.F("выполнено {0} из {1}", done, g.Steps.Count))).Append(")</span></li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("<footer>").Append(E(Loc.T("VELOCITY не меняет BIOS и не трогает файлы игр без спроса. Цифры замеров зависят от условий: сравнивай прогоны, сделанные одинаково."))).Append("</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>Сохраняет отчёт в «Документы\VELOCITY» и открывает в браузере.</summary>
    public static string SaveAndOpen(string html)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VELOCITY");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"velocity-report-{DateTime.Now:yyyy-MM-dd-HHmm}.html");
        File.WriteAllText(path, html, new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return path;
    }

    private static void Tile(StringBuilder sb, string label, string value, string tone) =>
        sb.Append("<div class=\"tile\"><div class=\"label\">").Append(E(label)).Append("</div><div class=\"value ")
          .Append(tone).Append("\">").Append(E(value)).Append("</div></div>");

    private static void Row(StringBuilder sb, string key, string value) =>
        sb.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value.Trim())).Append("</td></tr>");

    private static string Num(double v) => v >= 100 ? v.ToString("0", Loc.Culture) : v.ToString("0.##", Loc.Culture);
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{--bg:#f6f7f9;--panel:#fff;--line:#e3e6ea;--text:#16191d;--muted:#69717c;--good:#1e8a4c;--bad:#c42b31;--accent:#2563eb}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.55 "Segoe UI",system-ui,sans-serif}
        main{max-width:920px;margin:0 auto;padding:40px 28px 60px}
        header{border-bottom:1px solid var(--line);padding-bottom:18px;margin-bottom:22px}
        .brand{font-weight:700;letter-spacing:.12em;font-size:12px;color:var(--accent)}
        h1{font-size:26px;margin:6px 0 4px}h2{font-size:17px;margin:30px 0 10px}
        .muted{color:var(--muted)}.good{color:var(--good)}.bad{color:var(--bad)}
        .tiles{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}
        .tile{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
        .tile .label{color:var(--muted);font-size:12px}.tile .value{font-size:22px;font-weight:650;margin-top:2px}
        table{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden}
        th,td{text-align:left;padding:8px 12px;border-bottom:1px solid var(--line);vertical-align:top}
        tr:last-child th,tr:last-child td{border-bottom:0}th{font-weight:600;color:var(--muted);font-size:12.5px}
        table.kv th{width:190px}tr.delta td{font-weight:650}
        ul{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 16px 12px 34px;margin:0}
        li{margin:4px 0}footer{margin-top:34px;color:var(--muted);font-size:12px}
        @media print{body{background:#fff}main{padding:0}}
        @media(max-width:640px){.tiles{grid-template-columns:repeat(2,1fr)}}
        """;
}
