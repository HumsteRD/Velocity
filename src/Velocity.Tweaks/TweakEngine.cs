using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Velocity.Core;
using Velocity.Tweaks.Actions;

namespace Velocity.Tweaks;

public enum TweakState
{
    /// <summary>Состояние прочитать не удалось.</summary>
    Unknown,
    /// <summary>Твик уже применён.</summary>
    Applied,
    /// <summary>Твик не применён — есть что улучшить.</summary>
    NotApplied,
    /// <summary>Не подходит этой системе (не совпали условия).</summary>
    NotApplicable
}

public sealed record TweakStatus(TweakDefinition Tweak, TweakState State, string? Reason = null);

public enum ApplyOutcome { Applied, Skipped, Failed, VerifyFailed }

public sealed record ApplyResult(
    string TweakId,
    string Title,
    ApplyOutcome Outcome,
    string? Message = null,
    bool NeedsReboot = false);

/// <summary>
/// Применяет твики. Порядок операций жёсткий и нарушать его нельзя
/// (docs/02-OPTIMIZATION-SPEC.md, раздел 5):
///
///   проверка условий → снятие снимка → применение → проверка результата
///                                          ↓ не сошлось
///                                     откат этого твика
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TweakEngine(SystemFacts facts, SnapshotStore? store = null)
{
    private readonly SnapshotStore _store = store ?? new SnapshotStore();

    public SystemFacts Facts { get; } = facts;

    /// <summary>Исполняемые файлы античитов: при их активности систему не трогаем.</summary>
    private static readonly string[] AntiCheatProcesses =
        ["vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEITService", "ESEAClient"];

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Активные античиты. Пустой список — можно работать.</summary>
    public static List<string> DetectActiveAntiCheats()
    {
        var found = new List<string>();
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return found; }

        foreach (var p in all)
        {
            try
            {
                if (AntiCheatProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)
                    && !found.Contains(p.ProcessName)) found.Add(p.ProcessName);
            }
            catch { }
            finally { p.Dispose(); }
        }

        return found;
    }

    // ─────────────────────────── Состояние ───────────────────────────

    public TweakStatus GetStatus(TweakDefinition tweak)
    {
        if (!Facts.MatchesAll(tweak.Requires))
            return new TweakStatus(tweak, TweakState.NotApplicable, L10n.T("не подходит этой конфигурации"));

        var detect = DetectActions(tweak);
        if (detect.Count == 0)
            return new TweakStatus(tweak, TweakState.Unknown);

        try
        {
            bool applied = detect.All(CheckAction);
            return new TweakStatus(tweak, applied ? TweakState.Applied : TweakState.NotApplied);
        }
        catch (Exception ex)
        {
            return new TweakStatus(tweak, TweakState.Unknown, ex.Message);
        }
    }

    private static List<TweakAction> DetectActions(TweakDefinition tweak)
    {
        if (tweak.DetectAll) return [.. tweak.Apply.Where(TweakDefinition.IsDetectable)];
        return tweak.Detect is { } single ? [single] : [];
    }

    private static bool CheckAction(TweakAction action) => action switch
    {
        RegistryAction reg => RegistryExecutor.Check(reg),
        PowerCfgAction pc => CheckPower(pc),
        DisplayModeAction dm => CheckDisplay(dm),
        ServiceAction svc => CheckService(svc),
        NvidiaProfileAction nv => CheckNvidia(nv),
        JsonFileAction js => JsonFileExecutor.Check(js),
        ScheduledTaskAction task => TaskExecutor.Check(task),
        RegistryEachAction each => RegistryEachExecutor.Check(each),
        DnsAction dns => DnsExecutor.Check(dns),
        WindowsFeatureAction feature => FeatureExecutor.Check(feature),
        _ => false
    };

    public List<TweakStatus> GetStatuses(IEnumerable<TweakDefinition> tweaks)
        => [.. tweaks.Select(GetStatus)];

    private static bool CheckPower(PowerCfgAction action) => action.Operation switch
    {
        "activate" => string.Equals(PowerExecutor.ReadActiveScheme(), action.SchemeGuid,
            StringComparison.OrdinalIgnoreCase),
        "setvalue" => PowerExecutor.ReadSetting(action) is { } current
                      && action.AcValue is { } expected
                      && current == expected,
        _ => false
    };

    private static bool CheckNvidia(NvidiaProfileAction action)
    {
        if (!uint.TryParse(action.Setting.Replace("0x", ""),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var id)) return false;

        return NvidiaExecutor.Read(id) == action.Value;
    }

    private static bool CheckDisplay(DisplayModeAction action)
    {
        if (action.Mode != "max_refresh") return false;

        // Твик считается применённым, когда каждый монитор уже работает на своём максимуме.
        return DisplayExecutor.Enumerate().All(t =>
        {
            var max = DisplayExecutor.MaxRefreshAt(t.Device, t.Width, t.Height);
            return max <= 0 || t.RefreshHz >= max - 1;
        });
    }

    private static bool CheckService(ServiceAction action)
    {
        var expected = action.StartMode.ToLowerInvariant() switch
        {
            "auto" => 2,
            "manual" => 3,
            "disabled" => 4,
            _ => -1
        };
        // Отсутствующая служба считается приведённой в нужное состояние: отключать нечего.
        return ServiceExecutor.ReadStart(action.Name) is not { } current || current == expected;
    }

    // ─────────────────────────── Применение ───────────────────────────

    public sealed record ApplyOptions
    {
        /// <summary>Ничего не менять, только показать, что было бы сделано.</summary>
        public bool DryRun { get; init; }
        /// <summary>Игнорировать проверку активных античитов (для отладки).</summary>
        public bool IgnoreAntiCheat { get; init; }
        public string Reason { get; init; } = L10n.T("Применение оптимизаций");
    }

    public sealed record ApplySession(
        List<ApplyResult> Results,
        string? SnapshotId,
        bool RebootRequired,
        string? AbortReason = null);

    public ApplySession Apply(
        IEnumerable<TweakDefinition> tweaks,
        ApplyOptions? options = null,
        IProgress<ApplyResult>? progress = null)
    {
        options ??= new ApplyOptions();
        var list = tweaks.ToList();
        var results = new List<ApplyResult>();

        // Проверка античитов идёт ДО снятия снимка: если игра запущена,
        // мы вообще ничего не начинаем.
        if (!options.IgnoreAntiCheat && !options.DryRun)
        {
            var active = DetectActiveAntiCheats();
            if (active.Count > 0)
            {
                return new ApplySession([], null, false,
                    L10n.F("Запущен античит ({0}). ", string.Join(", ", active)) +
                    L10n.T("Закрой игру и её лаунчер — менять настройки при активном античите небезопасно."));
            }
        }

        if (options.DryRun)
        {
            foreach (var tweak in list)
            {
                var message = string.Join("; ", tweak.Apply.Select(a => a.Describe()));
                var result = new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Skipped, message);
                results.Add(result);
                progress?.Report(result);
            }
            return new ApplySession(results, null, false);
        }

        var session = _store.BeginSession(options.Reason);
        bool reboot = false;

        foreach (var tweak in list)
        {
            // Один сломанный твик не должен обрывать весь сеанс: иначе уже применённые
            // до него изменения остались бы без отката до ручного восстановления точки.
            ApplyResult result;
            try { result = ApplyOne(tweak, session); }
            catch (Exception ex)
            {
                result = new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Failed,
                    L10n.F("непредвиденная ошибка: {0}. Изменение сохранено в точке отката", ex.Message));
            }
            results.Add(result);
            progress?.Report(result);

            if (result.Outcome == ApplyOutcome.Applied && tweak.RequiresReboot) reboot = true;
        }

        session.DiscardIfEmpty();
        return new ApplySession(results, session.Snapshot.Changes.Count > 0 ? session.Snapshot.Id : null, reboot);
    }

    private ApplyResult ApplyOne(TweakDefinition tweak, SnapshotSession session)
    {
        if (!Facts.MatchesAll(tweak.Requires))
            return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Skipped, L10n.T("не подходит этой конфигурации"));

        if (tweak.IsManual)
            return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Skipped,
                L10n.T("настройка выполняется вручную — программа её изменить не может"));

        if (tweak.Apply.OfType<CommandAction>().Any() && tweak.Revert.Count == 0)
            return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Skipped,
                L10n.T("в каталоге не описан откат для команды — применять необратимое запрещено"));

        if (tweak.RequiresElevation && !IsElevated())
            return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Skipped, L10n.T("нужны права администратора"));

        // Снимок ДО любых изменений. Если снять не удалось — не применяем вовсе:
        // необратимое изменение хуже неприменённого твика.
        List<StateRecord> previous;
        try
        {
            previous = [.. tweak.Apply.SelectMany(Capture)];

            // Состояние команд снять нельзя — у них нет «прежнего значения», которое
            // можно прочитать. Поэтому такие твики обязаны принести свой откат явно,
            // и мы сохраняем его в снимок вместе с остальным.
            previous.AddRange(tweak.Revert.OfType<CommandAction>().Select(c => new StateRecord
            {
                Kind = "command",
                Target = c.Executable,
                Value = c.Arguments
            }));
        }
        catch (Exception ex)
        {
            return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Failed,
                L10n.F("не удалось сохранить прежнее состояние: {0}", ex.Message));
        }

        var change = new TweakChange
        {
            TweakId = tweak.Id,
            TweakTitle = tweak.Title,
            AppliedAt = DateTimeOffset.Now,
            Previous = previous
        };
        session.Record(change);

        try
        {
            foreach (var action in tweak.Apply) Execute(action);
        }
        catch (Exception ex)
        {
            return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Failed,
                TryRollback(change, session, tweak.Id) is { } rollbackError
                    ? L10n.F("{0}. Вернуть прежнее значение не удалось ({1}) — оно сохранено в точке отката", ex.Message, rollbackError)
                    : ex.Message);
        }

        // Проверяем, что изменение действительно вступило в силу.
        if (DetectActions(tweak).Count > 0 && !tweak.RequiresReboot)
        {
            var status = GetStatus(tweak);
            if (status.State == TweakState.NotApplied)
            {
                return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.VerifyFailed,
                    TryRollback(change, session, tweak.Id) is { } rollbackError
                        ? L10n.F("изменение не подтвердилось, а откатить не удалось ({0}) — значение сохранено в точке отката", rollbackError)
                        : L10n.T("изменение не подтвердилось, откатили"));
            }
        }

        return new ApplyResult(tweak.Id, tweak.Title, ApplyOutcome.Applied,
            NeedsReboot: tweak.RequiresReboot);
    }

    private static IEnumerable<StateRecord> Capture(TweakAction action) => action switch
    {
        RegistryAction reg => [RegistryExecutor.Capture(reg)],
        PowerCfgAction pc => [PowerExecutor.Capture(pc)],
        DisplayModeAction dm => DisplayExecutor.Capture(dm),
        ServiceAction svc => [ServiceExecutor.Capture(svc)],
        NvidiaProfileAction nv => [NvidiaExecutor.Capture(nv)],
        JsonFileAction js => [JsonFileExecutor.Capture(js)],
        ScheduledTaskAction task => [TaskExecutor.Capture(task)],
        RegistryEachAction each => RegistryEachExecutor.Capture(each),
        DnsAction dns => DnsExecutor.Capture(dns),
        WindowsFeatureAction feature => [FeatureExecutor.Capture(feature)],
        // Произвольные команды снимку не поддаются — для них твик обязан
        // задавать собственный блок revert.
        _ => []
    };

    private static void Execute(TweakAction action)
    {
        switch (action)
        {
            case RegistryAction reg: RegistryExecutor.Apply(reg); break;
            case PowerCfgAction pc: PowerExecutor.Apply(pc); break;
            case DisplayModeAction dm: DisplayExecutor.Apply(dm); break;
            case ServiceAction svc: ServiceExecutor.Apply(svc); break;
            case CommandAction cmd: CommandExecutor.Apply(cmd); break;
            case NvidiaProfileAction nv: NvidiaExecutor.Apply(nv); break;
            case JsonFileAction js: JsonFileExecutor.Apply(js); break;
            case ScheduledTaskAction task: TaskExecutor.Apply(task); break;
            case RegistryEachAction each: RegistryEachExecutor.Apply(each); break;
            case DnsAction dns: DnsExecutor.Apply(dns); break;
            case WindowsFeatureAction feature: FeatureExecutor.Apply(feature); break;
            default: throw new NotSupportedException(L10n.F("Действие {0} не поддерживается", action.GetType().Name));
        }
    }

    // ─────────────────────────── Откат ───────────────────────────

    public List<ApplyResult> Revert(string snapshotId, IProgress<ApplyResult>? progress = null)
    {
        var snapshot = _store.Load(snapshotId);
        if (snapshot is null) return [];

        var results = new List<ApplyResult>();

        // Откатываем в обратном порядке: последнее изменение снимается первым.
        foreach (var change in Enumerable.Reverse(snapshot.Changes))
        {
            if (change.Reverted) continue;

            try
            {
                RollbackChange(change);
                change.Reverted = true;
                results.Add(new ApplyResult(change.TweakId, change.TweakTitle, ApplyOutcome.Applied, L10n.T("откачено")));
            }
            catch (Exception ex)
            {
                results.Add(new ApplyResult(change.TweakId, change.TweakTitle, ApplyOutcome.Failed, ex.Message));
            }

            progress?.Report(results[^1]);
        }

        snapshot.Reverted = snapshot.Changes.All(c => c.Reverted);
        SaveSnapshot(snapshot);
        return results;
    }

    /// <summary>
    /// Откат одного твика внутри сеанса. Возвращает текст ошибки или null. Если откат не удался,
    /// изменение НЕ помечается откатанным — оно остаётся в точке отката, и вернуть его можно позже.
    /// </summary>
    private static string? TryRollback(TweakChange change, SnapshotSession session, string tweakId)
    {
        try
        {
            RollbackChange(change);
            session.MarkReverted(tweakId);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void RollbackChange(TweakChange change)
    {
        // Значения тоже возвращаем в обратном порядке. Ошибка на одном значении не мешает
        // вернуть остальные; о ней сообщаем после того, как попробовали всё.
        var errors = new List<string>();
        foreach (var record in Enumerable.Reverse(change.Previous))
        {
            try { RestoreRecord(record); }
            catch (Exception ex) { errors.Add($"{record.Target}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    private static void RestoreRecord(StateRecord record)
    {
        {
            switch (record.Kind)
            {
                case "registry": RegistryExecutor.Restore(record); break;
                case "powercfg": PowerExecutor.Restore(record); break;
                case "display": DisplayExecutor.Restore(record); break;
                case "service": ServiceExecutor.Restore(record); break;
                case "nvidia": NvidiaExecutor.Restore(record); break;
                case "json": JsonFileExecutor.Restore(record); break;
                case "task": TaskExecutor.Restore(record); break;
                case "dns": DnsExecutor.Restore(record); break;
                case "feature": FeatureExecutor.Restore(record); break;
                case "command":
                    if (record.Value is { } arguments)
                        CommandExecutor.Apply(new CommandAction { Executable = record.Target, Arguments = arguments });
                    break;
            }
        }
    }

    private void SaveSnapshot(Snapshot snapshot)
    {
        if (snapshot.FilePath is null) return;
        try
        {
            File.WriteAllText(snapshot.FilePath, System.Text.Json.JsonSerializer.Serialize(snapshot,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    // Иначе кириллица уезжает в escape-последовательности, а снимок
                    // должен оставаться читаемым человеком: это файл восстановления.
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
        }
        catch { }
    }

    public List<Snapshot> Snapshots() => _store.List();
}
