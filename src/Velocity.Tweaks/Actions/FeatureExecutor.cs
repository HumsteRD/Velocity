using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Velocity.Core;

namespace Velocity.Tweaks.Actions;

/// <summary>
/// Компоненты Windows («Включение или отключение компонентов Windows»).
///
/// Состояние читается через WMI (Win32_OptionalFeature), меняется через dism.exe —
/// тот же путь, что и у панели управления. Отсутствующий в сборке компонент
/// (в LTSC и новых версиях часть убрана) считается уже выключенным и не трогается.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class FeatureExecutor
{
    private const int Enabled = 1, Disabled = 2, Absent = 3;

    public static StateRecord Capture(WindowsFeatureAction action) => new()
    {
        Kind = "feature",
        Target = action.Name,
        Value = State(action.Name)?.ToString() ?? ""
    };

    public static void Apply(WindowsFeatureAction action)
    {
        var state = State(action.Name);
        if (state is null or Absent) return;
        if (state == (action.Enabled ? Enabled : Disabled)) return;
        Run(action.Name, action.Enabled);
    }

    public static void Restore(StateRecord record)
    {
        if (record.Value == Enabled.ToString()) Run(record.Target, enable: true);
        else if (record.Value == Disabled.ToString()) Run(record.Target, enable: false);
    }

    public static bool Check(WindowsFeatureAction action)
    {
        var state = State(action.Name);
        return state is null or Absent || state == (action.Enabled ? Enabled : Disabled);
    }

    /// <summary>InstallState: 1 — включён, 2 — выключен, 3 — отсутствует; null — не удалось прочитать.</summary>
    public static int? State(string name)
    {
        if (!SafeName().IsMatch(name)) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT InstallState FROM Win32_OptionalFeature WHERE Name = '{name}'");
            foreach (ManagementObject mo in searcher.Get())
                return Convert.ToInt32(mo["InstallState"]);
            return Absent;
        }
        catch { return null; }
    }

    private static void Run(string name, bool enable)
    {
        if (!SafeName().IsMatch(name))
            throw new ArgumentException(L10n.F("Недопустимое имя компонента: {0}", name));

        var verb = enable ? "/Enable-Feature /All" : "/Disable-Feature";
        var psi = new ProcessStartInfo("dism.exe", $"/Online {verb} /FeatureName:{name} /NoRestart /Quiet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(L10n.T("dism не запустился"));
        process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException(L10n.F("dism не уложился в 5 минут ({0})", name));
        }
        // 3010 — «готово, нужна перезагрузка».
        if (process.ExitCode is not (0 or 3010))
            throw new InvalidOperationException(L10n.F("dism вернул код {0} для компонента {1}", process.ExitCode, name));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{1,80}$")]
    private static partial Regex SafeName();
}

/// <summary>Сведения о принтерах для условий каталога.</summary>
[SupportedOSPlatform("windows")]
public static class Printers
{
    /// <summary>Виртуальные принтеры Windows, ради которых служба печати не нужна.</summary>
    private static readonly string[] Virtual =
        ["Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote", "Fax", "Send To OneNote", "Microsoft Print"];

    /// <summary>Сколько установлено настоящих (не виртуальных) принтеров.</summary>
    public static int PhysicalCount()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
            if (key is null) return 0;
            return key.GetSubKeyNames().Count(name =>
                !Virtual.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return -1; }
    }
}
