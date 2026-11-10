using Velocity.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using Velocity.Tweaks.Actions;

namespace Velocity.Tweaks.Startup;

public enum StartupKind { Logon, Task, Service }

public enum StartupAdvice
{
    /// <summary>Ничего особенного: решает пользователь.</summary>
    Neutral,
    /// <summary>Тяжёлая программа в фоне — кандидат на отключение.</summary>
    Heavy,
    /// <summary>Обновлятор: отключение ничего не ломает, программа обновится при запуске.</summary>
    Updater,
    /// <summary>Нужна для античита, драйвера, защиты или VPN — отключать не стоит.</summary>
    Keep
}

/// <summary>Одна вещь, которая запускается сама: программа при входе, задача или служба.</summary>
public sealed class StartupEntry
{
    public required string Key { get; init; }
    public required StartupKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Command { get; init; }
    public string? Publisher { get; init; }
    public required string Location { get; init; }
    public bool Enabled { get; init; }
    public StartupAdvice Advice { get; init; }
    public string? AdviceText { get; init; }
    public bool RequiresElevation { get; init; }

    // Данные для переключения — у каждого вида свои.
    internal string? ApprovedHive { get; init; }
    internal string? ApprovedPath { get; init; }
    internal string? ApprovedValue { get; init; }
    internal string? TaskPath { get; init; }
    internal string? ServiceName { get; init; }
}

/// <summary>
/// Автозагрузка целиком: программы при входе, задачи планировщика сторонних программ
/// и их службы с автозапуском.
///
/// Ничего не удаляется. Программы при входе выключаются тем же флагом, что и в диспетчере
/// задач (StartupApproved), задачи — переводятся в «Отключена», службы — в ручной запуск.
/// Каждое переключение превращается в обычный твик и проходит через движок со снимком и откатом.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupManager
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupEntry> Scan()
    {
        var list = new List<StartupEntry>();
        try { list.AddRange(ScanLogon()); } catch { }
        try { list.AddRange(ScanTasks()); } catch { }
        try { list.AddRange(ScanServices()); } catch { }
        return list;
    }

    /// <summary>Твик, который включает или выключает запись. Откат — общим механизмом снимков.</summary>
    public static TweakDefinition BuildToggle(StartupEntry entry, bool enable)
    {
        TweakAction action = entry.Kind switch
        {
            StartupKind.Logon => new RegistryAction
            {
                Hive = entry.ApprovedHive!,
                Path = entry.ApprovedPath!,
                Name = entry.ApprovedValue!,
                Kind = "binary",
                // Формат флага такой же, как пишет диспетчер задач: первый байт 02 — включено,
                // 03 — выключено, дальше время выключения (FILETIME).
                Value = enable
                    ? "020000000000000000000000"
                    : "03000000" + Convert.ToHexString(BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()))
            },
            StartupKind.Task => new ScheduledTaskAction { Task = entry.TaskPath!, Enabled = enable },
            StartupKind.Service => new ServiceAction { Name = entry.ServiceName!, StartMode = enable ? "auto" : "manual" },
            _ => throw new NotSupportedException()
        };

        return new TweakDefinition
        {
            Id = $"startup.{entry.Kind.ToString().ToLowerInvariant()}.{entry.Key}",
            Category = TweakCategory.Background,
            Title = L10n.F("{0} автозапуск: {1}", (enable ? L10n.T("Включить") : L10n.T("Отключить")), entry.Name),
            Description = entry.Location,
            RequiresElevation = entry.RequiresElevation,
            RequiresGameClosed = false,
            Apply = [action]
        };
    }

    // ───────────────────────────── Программы при входе ─────────────────────────────

    private static IEnumerable<StartupEntry> ScanLogon()
    {
        (RegistryHive Hive, string HiveName, string RunPath, string ApprovedKey, string Where, bool Elevated)[] runKeys =
        [
            (RegistryHive.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Run",
                L10n.T("Реестр, только этот пользователь"), false),
            (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run",
                L10n.T("Реестр, все пользователи"), true),
            (RegistryHive.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32",
                L10n.T("Реестр, все пользователи (32 бита)"), true),
        ];

        foreach (var (hive, hiveName, runPath, approvedKey, where, elevated) in runKeys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = baseKey.OpenSubKey(runPath);
            if (run is null) continue;

            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var command = run.GetValue(name)?.ToString() ?? "";
                var exe = ExecutableOf(command);
                var publisher = PublisherOf(exe);
                var approvedPath = $@"{Approved}\{approvedKey}";
                var (advice, text) = Classify(name, command, publisher);

                yield return new StartupEntry
                {
                    Key = $"{hiveName}.{approvedKey}.{name}",
                    Kind = StartupKind.Logon,
                    Name = name,
                    Command = command,
                    Publisher = publisher,
                    Location = where,
                    Enabled = IsApproved(baseKey, approvedPath, name),
                    Advice = advice,
                    AdviceText = text,
                    RequiresElevation = elevated,
                    ApprovedHive = hiveName,
                    ApprovedPath = approvedPath,
                    ApprovedValue = name
                };
            }
        }

        (Environment.SpecialFolder Folder, RegistryHive Hive, string HiveName, string Where, bool Elevated)[] folders =
        [
            (Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "HKCU", L10n.T("Папка автозагрузки пользователя"), false),
            (Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "HKLM", L10n.T("Общая папка автозагрузки"), true),
        ];

        foreach (var (folder, hive, hiveName, where, elevated) in folders)
        {
            string dir;
            try { dir = Environment.GetFolderPath(folder); } catch { continue; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var approvedPath = $@"{Approved}\StartupFolder";
                var name = Path.GetFileNameWithoutExtension(file);
                var (advice, text) = Classify(name, file, null);

                yield return new StartupEntry
                {
                    Key = $"{hiveName}.Folder.{fileName}",
                    Kind = StartupKind.Logon,
                    Name = name,
                    Command = file,
                    Location = where,
                    Enabled = IsApproved(baseKey, approvedPath, fileName),
                    Advice = advice,
                    AdviceText = text,
                    RequiresElevation = elevated,
                    ApprovedHive = hiveName,
                    ApprovedPath = approvedPath,
                    ApprovedValue = fileName
                };
            }
        }
    }

    /// <summary>Нечётный первый байт флага StartupApproved означает «выключено». Нет флага — включено.</summary>
    private static bool IsApproved(RegistryKey baseKey, string approvedPath, string name)
    {
        try
        {
            using var key = baseKey.OpenSubKey(approvedPath);
            return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch { return true; }
    }

    // ───────────────────────────── Задачи ─────────────────────────────

    private static IEnumerable<StartupEntry> ScanTasks()
    {
        foreach (var task in TaskExecutor.ListThirdParty())
        {
            var publisher = PublisherOf(task.ActionPath);
            var (advice, text) = Classify(task.Name, task.ActionPath ?? task.Path, publisher ?? task.Author);
            yield return new StartupEntry
            {
                Key = task.Path,
                Kind = StartupKind.Task,
                Name = task.Name,
                Command = task.ActionPath is null ? null : $"{task.ActionPath} {task.Arguments}".Trim(),
                Publisher = publisher ?? task.Author,
                Location = task.Path,
                Enabled = task.Enabled,
                Advice = advice,
                AdviceText = text,
                RequiresElevation = true,
                TaskPath = task.Path
            };
        }
    }

    // ───────────────────────────── Службы ─────────────────────────────

    /// <summary>Службы, которые программа переводила в ручной запуск, — чтобы их можно было вернуть.</summary>
    private static string DisabledServicesFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity", "startup-services.json");

    public static void RememberServiceChange(string service, bool enabled)
    {
        try
        {
            var set = LoadDisabledServices();
            if (enabled) set.Remove(service); else set.Add(service);
            Directory.CreateDirectory(Path.GetDirectoryName(DisabledServicesFile)!);
            File.WriteAllText(DisabledServicesFile, JsonSerializer.Serialize(set.Order()));
        }
        catch { }
    }

    private static HashSet<string> LoadDisabledServices()
    {
        try
        {
            return File.Exists(DisabledServicesFile)
                ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DisabledServicesFile)) ?? [],
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static IEnumerable<StartupEntry> ScanServices()
    {
        var ours = LoadDisabledServices();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null) yield break;

        foreach (var name in root.GetSubKeyNames())
        {
            StartupEntry? entry = null;
            try
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;

                int start = key.GetValue("Start") is int s ? s : -1;
                int type = key.GetValue("Type") is int t ? t : 0;
                // Только службы пользовательского режима (не драйверы) с автозапуском —
                // плюс те, что программа сама перевела в ручной режим.
                if ((type & 0x30) == 0) continue;
                if (start != 2 && !(start == 3 && ours.Contains(name))) continue;

                var imagePath = key.GetValue("ImagePath")?.ToString();
                if (string.IsNullOrWhiteSpace(imagePath)) continue;
                var exe = ExecutableOf(imagePath);
                if (exe is null || exe.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase)) continue;

                var publisher = PublisherOf(exe);
                if (publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true) continue;
                // Без издателя в свойствах файла: показываем, только если это не системный
                // файл Windows. Так в список попадают самодельные службы вроде VPN и zapret.
                var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (publisher is null && (!File.Exists(exe) || exe.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var display = key.GetValue("DisplayName")?.ToString() ?? name;
                if (display.StartsWith('@')) display = Indirect(display) ?? name;
                var (advice, text) = Classify(name + " " + display, exe, publisher);

                entry = new StartupEntry
                {
                    Key = name,
                    Kind = StartupKind.Service,
                    Name = display,
                    Command = imagePath,
                    Publisher = publisher,
                    Location = L10n.F("Служба {0}", name),
                    Enabled = start == 2,
                    Advice = advice,
                    AdviceText = text,
                    RequiresElevation = true,
                    ServiceName = name
                };
            }
            catch { }

            if (entry is not null) yield return entry;
        }
    }

    // ───────────────────────────── Классификация ─────────────────────────────

    private static readonly (string[] Marks, string Text)[] KeepRules =
    [
        (["vgc", "vanguard", "easyanticheat", "battleye", "beservice", "faceit", "esea", "anticheat", "anti-cheat"],
            L10n.T("Античит: без него игры не запустятся")),
        (["kaspersky", "eset", "avast", "avg ", "bitdefender", "dr.web", "drweb", "malwarebytes", "norton", "mcafee", "securityhealth", "windows defender"],
            L10n.T("Защита компьютера: не отключай")),
        (["nvdisplay.container", "nvidia display container", "amd external events", "rtkaud", "realtek audio", "igfx", "intel(r) graphics", "intel graphics", "synaptics", "etdctrl", "elan smart"],
            L10n.T("Нужна драйверу: без неё пропадут панель драйвера или функции устройства")),
        (["winws", "zapret", "xray", "sing-box", "hiddify", "goodbyedpi", "wireguard", "openvpn", "amnezia", "fi-service", "\\fi\\fi.exe", "nekoray", "clash"],
            L10n.T("VPN или обход блокировок: отключишь — перестанет работать при входе")),
    ];

    private static readonly (string[] Marks, string Text)[] UpdaterRules =
    [
        (["update", "updater", "googleupdate", "edgeupdate", "adobearm", "mozillamaintenance", "maintenance service", "autoupdate", "brave update"],
            L10n.T("Обновлятор: программа обновится при запуске или по кнопке")),
    ];

    private static readonly (string[] Marks, string Text)[] HeavyRules =
    [
        (["nahimic", "a-volute"], L10n.T("Nahimic известен статтерами и конфликтами со звуком в играх")),
        (["icue", "corsair", "armoury", "armourycrate", "lightingservice", "aura", "razer", "synapse", "lghub", "logitech g hub", "msi center", "dragon center", "steelseries", "nzxt cam", "signalrgb", "openrgb"],
            L10n.T("Утилита подсветки или железа: держит процессы в фоне. Настройки устройства обычно сохраняются в нём самом")),
        (["wallpaper engine", "wallpaper64", "lively"], L10n.T("Живые обои грузят видеокарту, пока работают")),
        (["epicgameslauncher", "eadesktop", "ea desktop", "ubisoft", "uplay", "battle.net", "goggalaxy", "rockstar"],
            L10n.T("Лаунчер запустится сам, когда откроешь игру")),
        (["discord", "spotify", "telegram", "skype", "teams", "slack", "zoom", "whatsapp", "viber"],
            L10n.T("Мессенджер или плеер: можно открывать, когда нужен")),
        (["onedrive", "dropbox", "googledrivefs", "yandex.disk", "yandexdisk", "icloud"],
            L10n.T("Облако синхронизирует файлы в фоне и нагружает диск")),
        (["steam"], L10n.T("Steam запустится сам, когда откроешь игру. Оставь, если нужен в трее для друзей и загрузок")),
        (["cortana", "copilot"], L10n.T("Ассистент Windows в фоне")),
    ];

    private static (StartupAdvice, string?) Classify(string name, string? command, string? publisher)
    {
        var text = $"{name} {command} {publisher}".ToLowerInvariant();
        foreach (var (marks, advice) in KeepRules)
            if (marks.Any(text.Contains)) return (StartupAdvice.Keep, advice);
        foreach (var (marks, advice) in UpdaterRules)
            if (marks.Any(text.Contains)) return (StartupAdvice.Updater, advice);
        foreach (var (marks, advice) in HeavyRules)
            if (marks.Any(text.Contains)) return (StartupAdvice.Heavy, advice);
        return (StartupAdvice.Neutral, null);
    }

    // ───────────────────────────── Служебное ─────────────────────────────

    /// <summary>Путь к исполняемому файлу из строки запуска с кавычками, аргументами и переменными.</summary>
    public static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());

        string candidate;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            candidate = end > 1 ? text[1..end] : text.Trim('"');
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        }

        // Пути служб вида \SystemRoot\System32\... и \??\C:\...
        if (candidate.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), candidate[12..]);
        if (candidate.StartsWith(@"\??\")) candidate = candidate[4..];
        if (candidate.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), candidate);
        return candidate;
    }

    public static string? PublisherOf(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return null;
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return string.IsNullOrWhiteSpace(company) ? null : company;
        }
        catch { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder buffer, int size, IntPtr reserved);

    private static string? Indirect(string resource)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(512);
            return SHLoadIndirectString(resource, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : null;
        }
        catch { return null; }
    }
}
