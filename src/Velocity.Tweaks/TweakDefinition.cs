using Velocity.Core;
using System.Text.Json.Serialization;

namespace Velocity.Tweaks;

/// <summary>
/// Твик — это ДАННЫЕ, а не код. Благодаря этому каталог обновляется с сервера
/// без переустановки программы (docs/02-OPTIMIZATION-SPEC.md, раздел 1).
/// </summary>
public sealed class TweakDefinition
{
    public required string Id { get; init; }
    public int Version { get; init; } = 1;
    public required TweakCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Что именно меняется — показываем пользователю до применения.</summary>
    public string? Effect { get; init; }
    /// <summary>Честная оценка выигрыша. Без «+300% FPS».</summary>
    public string? ExpectedGain { get; init; }

    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    public TweakConfidence Confidence { get; init; } = TweakConfidence.Documented;

    public bool RequiresReboot { get; init; }
    /// <summary>Применяем только при закрытой игре — иначе можно словить бан античита.</summary>
    public bool RequiresGameClosed { get; init; } = true;
    public bool RequiresElevation { get; init; }
    public bool AntiCheatSafe { get; init; } = true;

    /// <summary>Условия применимости: «gpu.vendor=nvidia», «os.build&gt;=19041».</summary>
    public List<string> Requires { get; init; } = [];
    /// <summary>Твики, которые нельзя включать одновременно с этим.</summary>
    public List<string> Conflicts { get; init; } = [];

    /// <summary>
    /// Как определить, применён ли твик. Если не задан, берётся первое действие
    /// из Apply — для реестра, питания и профиля драйвера этого достаточно,
    /// и не приходится дублировать одно и то же в каталоге.
    /// </summary>
    public TweakAction? Detect
    {
        get => _detect ?? Apply.FirstOrDefault(IsDetectable);
        init => _detect = value;
    }
    private readonly TweakAction? _detect;

    /// <summary>
    /// Проверять состояние по ВСЕМ действиям Apply, а не по первому. Нужно твикам,
    /// которые меняют несколько независимых вещей (пять задач планировщика, политики
    /// для пяти игр): по первому пункту нельзя судить об остальных.
    /// </summary>
    public bool DetectAll { get; init; }

    /// <summary>Действия, состояние которых программа умеет прочитать.</summary>
    public static bool IsDetectable(TweakAction action) =>
        action is RegistryAction or PowerCfgAction or NvidiaProfileAction or JsonFileAction
            or ServiceAction or ScheduledTaskAction or RegistryEachAction or DnsAction or WindowsFeatureAction;
    public List<TweakAction> Apply { get; init; } = [];
    /// <summary>Пусто = откат автоматический (восстановление снятого снимка).</summary>
    public List<TweakAction> Revert { get; init; } = [];

    /// <summary>В какие пресеты входит твик.</summary>
    public List<TweakPreset> Presets { get; init; } = [];

    /// <summary>
    /// Пошаговая инструкция для настроек, которые программа применить не может
    /// физически — например, включение XMP в BIOS. Без неё пункт превращается
    /// в упрёк «у тебя плохо настроено» без объяснения, что с этим делать.
    /// </summary>
    public List<string> ManualSteps { get; init; } = [];

    /// <summary>
    /// Настройка выполняется руками пользователя: программа только объясняет,
    /// куда идти и что нажимать.
    /// </summary>
    public bool IsManual => Apply.Count == 0 && ManualSteps.Count > 0;

    public List<string> Sources { get; init; } = [];
}

public enum TweakCategory
{
    Display, Power, Cpu, Gpu, Windows, Privacy, Security, Input, Memory, Storage, Network, Background, Game
}

public enum TweakRisk
{
    /// <summary>Обратимо, ничего не ломает, безопасность не трогает.</summary>
    Safe,
    /// <summary>Обратимо, но меняет поведение системы (сон, звук, экономия батареи).</summary>
    Moderate,
    /// <summary>Требует перезагрузки, меняет системные подсистемы.</summary>
    Advanced,
    /// <summary>Компромисс безопасности или стабильности. Только с явным согласием.</summary>
    Expert
}

public enum TweakConfidence
{
    /// <summary>Гипотеза. Только в расширенном режиме, помечена в интерфейсе.</summary>
    Experimental,
    /// <summary>Воспроизводимо по отзывам, но без строгих замеров.</summary>
    Community,
    /// <summary>Задокументировано вендором (Microsoft / NVIDIA / AMD / Intel).</summary>
    Documented,
    /// <summary>Замерено нами минимум на трёх конфигурациях.</summary>
    Measured
}

public enum TweakPreset { Safe, Gaming, Competitive, Maximum, Extreme, Laptop }

// ──────────────────────────── Действия ────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegistryAction), "registry")]
[JsonDerivedType(typeof(PowerCfgAction), "powercfg")]
[JsonDerivedType(typeof(DisplayModeAction), "display_mode")]
[JsonDerivedType(typeof(ServiceAction), "service")]
[JsonDerivedType(typeof(NvidiaProfileAction), "nvidia_profile")]
[JsonDerivedType(typeof(CommandAction), "command")]
[JsonDerivedType(typeof(JsonFileAction), "json_file")]
[JsonDerivedType(typeof(ScheduledTaskAction), "scheduled_task")]
[JsonDerivedType(typeof(RegistryEachAction), "registry_each")]
[JsonDerivedType(typeof(DnsAction), "dns")]
[JsonDerivedType(typeof(WindowsFeatureAction), "windows_feature")]
public abstract class TweakAction
{
    /// <summary>Человекочитаемое описание действия для журнала.</summary>
    public abstract string Describe();
}

public sealed class RegistryAction : TweakAction
{
    /// <summary>HKLM, HKCU, HKCR, HKU.</summary>
    public required string Hive { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    /// <summary>dword, qword, string, expand_string, multi_string, binary.</summary>
    public string Kind { get; init; } = "dword";
    /// <summary>Значение для записи. null означает удаление значения.</summary>
    public string? Value { get; init; }
    /// <summary>Ожидаемое значение при проверке состояния (detect).</summary>
    public string? Expected { get; init; }

    /// <summary>
    /// Настройка считается применённой, когда значения в реестре НЕТ.
    /// Отдельный флаг нужен потому, что «значение отсутствует» и «значение пустое» —
    /// разные состояния, и пустой строкой первое не выразить.
    /// </summary>
    public bool ExpectAbsent { get; init; }

    public override string Describe() =>
        Value is null ? L10n.F("{0}\\{1} → удалить {2}", Hive, Path, Name) : $"{Hive}\\{Path}\\{Name} = {Value}";
}

public sealed class PowerCfgAction : TweakAction
{
    /// <summary>duplicate | activate | setvalue.</summary>
    public required string Operation { get; init; }
    public string? SchemeGuid { get; init; }
    /// <summary>
    /// Для duplicate: GUID создаваемой копии. Без него powercfg выдаёт копии случайный
    /// GUID, и следующую команду «активировать» адресовать становится нечем.
    /// </summary>
    public string? TargetGuid { get; init; }
    public string? SubGroupGuid { get; init; }
    public string? SettingGuid { get; init; }
    /// <summary>Значение для сети (AC).</summary>
    public int? AcValue { get; init; }
    /// <summary>Значение для батареи (DC). null — не трогаем.</summary>
    public int? DcValue { get; init; }

    public override string Describe() => Operation switch
    {
        "duplicate" => L10n.F("создать схему питания {0}", TargetGuid ?? SchemeGuid),
        "activate" => L10n.F("активировать схему {0}", SchemeGuid),
        _ => L10n.F("питание: {0}/{1} = {2}", SubGroupGuid, SettingGuid, AcValue)
    };
}

public sealed class DisplayModeAction : TweakAction
{
    /// <summary>max_refresh — выставить максимальную доступную частоту. bit_depth — глубина цвета.</summary>
    public required string Mode { get; init; }
    /// <summary>Имя устройства (\\\\.\\DISPLAY1). null — все подключённые.</summary>
    public string? Device { get; init; }
    public int? Value { get; init; }

    public override string Describe() => Mode switch
    {
        "max_refresh" => L10n.T("выставить максимальную частоту обновления"),
        "bit_depth" => L10n.F("глубина цвета {0} бит", Value),
        _ => Mode
    };
}

public sealed class ServiceAction : TweakAction
{
    public required string Name { get; init; }
    /// <summary>auto | manual | disabled. Службы никогда не удаляем.</summary>
    public required string StartMode { get; init; }

    public override string Describe() => L10n.F("служба {0} → {1}", Name, StartMode);
}

public sealed class NvidiaProfileAction : TweakAction
{
    /// <summary>Имя настройки в драйверном профиле.</summary>
    public required string Setting { get; init; }
    public required uint Value { get; init; }
    /// <summary>null — глобальный профиль, иначе имя exe.</summary>
    public string? Application { get; init; }

    public override string Describe() =>
        $"NVIDIA: {Setting} = {Value}" + (Application is null ? L10n.T(" (глобально)") : $" ({Application})");
}

/// <summary>
/// Настройка программы, которая хранит конфигурацию в JSON — например Discord.
/// Меняется ровно одно свойство, остальной файл остаётся нетронутым.
/// </summary>
public sealed class JsonFileAction : TweakAction
{
    /// <summary>Путь с подстановками: {appdata}, {localappdata}, {documents}, {userprofile}.</summary>
    public required string File { get; init; }
    public required string Property { get; init; }
    public required string Value { get; init; }
    /// <summary>bool | number | string.</summary>
    public string Kind { get; init; } = "bool";

    public override string Describe() => $"{System.IO.Path.GetFileName(File)}: {Property} = {Value}";
}

public sealed class CommandAction : TweakAction
{
    /// <summary>Только из белого списка: powercfg, bcdedit, netsh, dism, sc.</summary>
    public required string Executable { get; init; }
    public required string Arguments { get; init; }

    public override string Describe() => $"{Executable} {Arguments}";
}

/// <summary>
/// Задача планировщика Windows. Задачи не удаляются — только выключаются,
/// а прежнее состояние записывается в снимок. Отсутствующая задача (в новых сборках
/// Microsoft часть задач убрала) просто пропускается.
/// </summary>
public sealed class ScheduledTaskAction : TweakAction
{
    /// <summary>Полный путь: \Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser.</summary>
    public required string Task { get; init; }
    public bool Enabled { get; init; }

    public override string Describe() => L10n.F("задача {0} → {1}", Task, (Enabled ? L10n.T("включить") : L10n.T("выключить")));
}

/// <summary>
/// Одно и то же значение реестра в нескольких ключах, которые заранее не известны:
/// параметры каждого сетевого интерфейса, ключ драйвера каждого адаптера,
/// настройки прерываний каждой видеокарты. Набор ключей вычисляется на месте,
/// а в снимок пишется по записи на каждый реальный ключ — откат идёт обычным
/// механизмом реестра.
/// </summary>
public sealed class RegistryEachAction : TweakAction
{
    /// <summary>
    /// tcp_interfaces — HKLM\...\Tcpip\Parameters\Interfaces\{GUID} активных физических адаптеров;
    /// net_adapters — ключ драйвера активных физических адаптеров (Control\Class\{4d36e972…}\00XX);
    /// msi_display, msi_net — Interrupt Management\MessageSignaledInterruptProperties видеокарт / сетевых карт.
    /// </summary>
    public required string Scope { get; init; }
    public required string Name { get; init; }
    public string Kind { get; init; } = "dword";
    public required string Value { get; init; }
    /// <summary>
    /// Писать только туда, где значение уже есть. Для дополнительных свойств драйвера
    /// сетевой карты это обязательно: ключевое слово, которого драйвер не знает,
    /// он просто проигнорирует, а мусор в реестре останется.
    /// </summary>
    public bool OnlyIfExists { get; init; }

    public override string Describe() => $"{Scope}: {Name} = {Value}";
}

/// <summary>
/// DNS-серверы активных физических адаптеров (IPv4). Пустой список возвращает
/// получение DNS автоматически от роутера (DHCP).
/// </summary>
public sealed class WindowsFeatureAction : TweakAction
{
    /// <summary>Имя компонента, как в «dism /online /get-features»: SMB1Protocol, MicrosoftWindowsPowerShellV2Root.</summary>
    public required string Name { get; init; }
    public bool Enabled { get; init; }

    public override string Describe() => L10n.F("компонент Windows {0} → {1}", Name,
        Enabled ? L10n.T("включить") : L10n.T("выключить"));
}

public sealed class DnsAction : TweakAction
{
    public List<string> Servers { get; init; } = [];

    public override string Describe() => Servers.Count == 0
        ? L10n.T("DNS: получать автоматически")
        : $"DNS: {string.Join(", ", Servers)}";
}
