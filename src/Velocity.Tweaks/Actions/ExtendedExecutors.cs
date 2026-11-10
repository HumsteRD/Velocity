using Velocity.Core;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Velocity.Tweaks.Actions;

/// <summary>
/// Задачи планировщика через COM-интерфейс Schedule.Service.
///
/// Почему не schtasks.exe: его вывод локализован («Готово» / «Отключена»),
/// и разбирать его на русской, английской и немецкой Windows одинаково нельзя.
/// COM отдаёт состояние числом и не зависит от языка системы.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskExecutor
{
    public static StateRecord Capture(ScheduledTaskAction action) => new()
    {
        Kind = "task",
        Target = action.Task,
        // null — задачи на этой сборке нет; откатывать нечего.
        Value = ReadEnabled(action.Task) is { } enabled ? (enabled ? "1" : "0") : null
    };

    public static void Apply(ScheduledTaskAction action) => SetEnabled(action.Task, action.Enabled);

    public static void Restore(StateRecord record)
    {
        if (record.Value is null) return;
        SetEnabled(record.Target, record.Value == "1");
    }

    /// <summary>Отсутствующая задача считается приведённой в нужное состояние: менять нечего.</summary>
    public static bool Check(ScheduledTaskAction action)
        => ReadEnabled(action.Task) is not { } enabled || enabled == action.Enabled;

    /// <summary>null — задачи нет.</summary>
    public static bool? ReadEnabled(string path)
    {
        return WithTask(path, task => (bool?)(bool)task.Enabled, missing: null);
    }

    private static void SetEnabled(string path, bool enabled)
    {
        WithTask<object?>(path, task =>
        {
            task.Enabled = enabled;
            return null;
        }, missing: null);
    }

    private static T WithTask<T>(string path, Func<dynamic, T> body, T missing)
    {
        var split = path.LastIndexOf('\\');
        var folderPath = split <= 0 ? "\\" : path[..split];
        var name = path[(split + 1)..];

        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException(L10n.T("Планировщик заданий недоступен"));
        dynamic service = Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException(L10n.T("Планировщик заданий недоступен"));
        try
        {
            service.Connect();
            dynamic folder;
            try { folder = service.GetFolder(folderPath); }
            catch (Exception ex) when (IsNotFound(ex)) { return missing; }

            dynamic task;
            try { task = folder.GetTask(name); }
            catch (Exception ex) when (IsNotFound(ex)) { return missing; }

            return body(task);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L10n.F("Нет прав на задачу {0}: нужны права администратора", path));
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(service); } catch { }
        }
    }

    private static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException
        || ex is COMException { HResult: unchecked((int)0x80070002) or unchecked((int)0x80070003) };

    public sealed record TaskInfo(string Path, string Name, bool Enabled, string? Author, string? ActionPath, string? Arguments);

    /// <summary>
    /// Задачи сторонних программ: всё, что лежит вне папки \Microsoft. Туда обновляторы
    /// и утилиты железа складывают свои задачи «при входе» и «каждый час».
    /// Задачи, которые не видны без прав администратора, сюда просто не попадут.
    /// </summary>
    public static List<TaskInfo> ListThirdParty()
    {
        var result = new List<TaskInfo>();
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return result;
        dynamic? service = Activator.CreateInstance(type);
        if (service is null) return result;

        try
        {
            service.Connect();
            Walk(service.GetFolder("\\"), result, depth: 0);
        }
        catch { }
        finally
        {
            try { Marshal.FinalReleaseComObject(service); } catch { }
        }
        return result;
    }

    private static void Walk(dynamic folder, List<TaskInfo> result, int depth)
    {
        string path = folder.Path;
        if (depth > 6 || path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            dynamic tasks = folder.GetTasks(1); // 1 = вместе со скрытыми
            int count = tasks.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic task = tasks[i];
                    string? actionPath = null, arguments = null, author = null;
                    try
                    {
                        dynamic definition = task.Definition;
                        author = definition.RegistrationInfo.Author as string;
                        dynamic actions = definition.Actions;
                        for (int a = 1; a <= (int)actions.Count; a++)
                        {
                            dynamic action = actions[a];
                            if ((int)action.Type != 0) continue; // 0 = запуск программы
                            actionPath = Environment.ExpandEnvironmentVariables(((string)action.Path).Trim('"'));
                            arguments = action.Arguments as string;
                            break;
                        }
                    }
                    catch { /* определение задачи бывает недоступно без прав — покажем без команды */ }

                    result.Add(new TaskInfo((string)task.Path, (string)task.Name, (bool)task.Enabled,
                        author, actionPath, arguments));
                }
                catch { }
            }
        }
        catch { }

        try
        {
            dynamic folders = folder.GetFolders(0);
            int count = folders.Count;
            for (int i = 1; i <= count; i++)
            {
                try { Walk(folders[i], result, depth + 1); } catch { }
            }
        }
        catch { }
    }
}

/// <summary>
/// Физические сетевые адаптеры. Виртуальные (VPN, Hyper-V, VirtualBox, Radmin)
/// отсекаются по идентификатору устройства: у настоящей карты он начинается с PCI\ или USB\.
/// Это надёжнее, чем угадывать по названию.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetAdapters
{
    public const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public sealed record Adapter(string Guid, string Description, string ClassSubKey, string DeviceInstanceId);

    public static List<Adapter> Physical()
    {
        var result = new List<Adapter>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = hklm.OpenSubKey(ClassKey);
            if (root is null) return result;

            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                try
                {
                    using var key = root.OpenSubKey(sub);
                    if (key?.GetValue("NetCfgInstanceId") is not string guid) continue;
                    var instance = key.GetValue("DeviceInstanceID") as string ?? "";
                    if (!instance.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
                        && !instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;

                    result.Add(new Adapter(guid, key.GetValue("DriverDesc") as string ?? guid,
                        $@"{ClassKey}\{sub}", instance));
                }
                catch { /* ключ без прав на чтение — просто пропускаем */ }
            }
        }
        catch { }
        return result;
    }
}

/// <summary>
/// Одно значение в наборе ключей, вычисляемом на месте. Вся запись идёт через
/// <see cref="RegistryExecutor"/>: снимок и откат у них общие.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RegistryEachExecutor
{
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string UsbClass = "{36fc9e60-c465-11cf-8056-444553540000}";
    private const string MsiSubKey = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    /// <summary>Конкретные действия реестра, в которые разворачивается правило.</summary>
    public static List<RegistryAction> Expand(RegistryEachAction action)
    {
        var keys = action.Scope.ToLowerInvariant() switch
        {
            "tcp_interfaces" => NetAdapters.Physical()
                .Select(a => $@"{NetAdapters.InterfacesKey}\{a.Guid}")
                .Where(KeyExists),
            "net_adapters" => NetAdapters.Physical().Select(a => a.ClassSubKey),
            "msi_display" => MsiKeys(DisplayClass),
            "msi_net" => MsiKeys(NetClass),
            "msi_usb" => MsiKeys(UsbClass),
            "usb_devices" => UsbDeviceKeys(),
            _ => throw new NotSupportedException(L10n.F("Неизвестная область: {0}", action.Scope))
        };

        var result = new List<RegistryAction>();
        foreach (var path in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (action.OnlyIfExists && !ValueExists(path, action.Name)) continue;
            result.Add(new RegistryAction
            {
                Hive = "HKLM",
                Path = path,
                Name = action.Name,
                Kind = action.Kind,
                Value = action.Value
            });
        }
        return result;
    }

    public static List<StateRecord> Capture(RegistryEachAction action)
        => [.. Expand(action).Select(RegistryExecutor.Capture)];

    public static void Apply(RegistryEachAction action)
    {
        foreach (var concrete in Expand(action)) RegistryExecutor.Apply(concrete);
    }

    /// <summary>Применено, если во всех найденных ключах стоит нужное значение. Нет ключей — менять нечего.</summary>
    public static bool Check(RegistryEachAction action) => Expand(action).All(RegistryExecutor.Check);

    /// <summary>
    /// «Device Parameters» каждого подключённого USB-устройства: там драйвер хранит
    /// разрешение Windows усыплять устройство (то же, что галочка «Разрешить отключение
    /// этого устройства для экономии энергии» в диспетчере устройств).
    /// </summary>
    private static IEnumerable<string> UsbDeviceKeys()
    {
        var result = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            const string usb = @"SYSTEM\CurrentControlSet\Enum\USB";
            using var root = hklm.OpenSubKey(usb);
            if (root is null) return result;

            foreach (var device in root.GetSubKeyNames())
            {
                try
                {
                    using var deviceKey = root.OpenSubKey(device);
                    if (deviceKey is null) continue;
                    foreach (var instance in deviceKey.GetSubKeyNames())
                    {
                        var path = $@"{usb}\{device}\{instance}\Device Parameters";
                        if (KeyExists(path)) result.Add(path);
                    }
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static IEnumerable<string> MsiKeys(string classGuid)
    {
        var result = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            const string pci = @"SYSTEM\CurrentControlSet\Enum\PCI";
            using var root = hklm.OpenSubKey(pci);
            if (root is null) return result;

            foreach (var device in root.GetSubKeyNames())
            {
                try
                {
                    using var deviceKey = root.OpenSubKey(device);
                    if (deviceKey is null) continue;
                    foreach (var instance in deviceKey.GetSubKeyNames())
                    {
                        using var instanceKey = deviceKey.OpenSubKey(instance);
                        if (instanceKey?.GetValue("ClassGUID") is not string cls
                            || !cls.Equals(classGuid, StringComparison.OrdinalIgnoreCase)) continue;

                        // Ключ прерываний есть только у устройств, драйвер которых
                        // вообще объявил поддержку MSI. Создавать его самим — гадание.
                        var path = $@"{pci}\{device}\{instance}\{MsiSubKey}";
                        if (KeyExists(path)) result.Add(path);
                    }
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(path);
            return key is not null;
        }
        catch { return false; }
    }

    private static bool ValueExists(string path, string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(path);
            return key?.GetValue(name) is not null;
        }
        catch { return false; }
    }
}

/// <summary>
/// DNS-серверы адаптеров через WMI (Win32_NetworkAdapterConfiguration).
///
/// Прежнее значение берётся из реестра, а не из WMI: WMI показывает действующие
/// серверы и не отличает «прописаны вручную» от «выданы роутером». Реестр хранит
/// именно ручную настройку, а пустая строка там означает «автоматически».
/// </summary>
[SupportedOSPlatform("windows")]
public static class DnsExecutor
{
    public static List<StateRecord> Capture(DnsAction action)
        => [.. Targets().Select(guid => new StateRecord
        {
            Kind = "dns",
            Target = guid,
            Value = ReadStatic(guid) ?? ""
        })];

    public static void Apply(DnsAction action)
    {
        var targets = Targets();
        if (targets.Count == 0)
            throw new InvalidOperationException(L10n.T("Не найдено подключённых физических сетевых адаптеров"));

        foreach (var guid in targets) Set(guid, action.Servers);
    }

    public static void Restore(StateRecord record)
    {
        var servers = (record.Value ?? "")
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).ToList();
        Set(record.Target, servers);
    }

    public static bool Check(DnsAction action)
    {
        var targets = Targets();
        if (targets.Count == 0) return false;

        return targets.All(guid =>
        {
            var current = (ReadStatic(guid) ?? "")
                .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            return current.SequenceEqual(action.Servers, StringComparer.OrdinalIgnoreCase);
        });
    }

    /// <summary>Вручную прописанные серверы адаптера; пустая строка — получает от роутера.</summary>
    public static string? ReadStatic(string guid)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey($@"{NetAdapters.InterfacesKey}\{guid}");
            return key?.GetValue("NameServer") as string;
        }
        catch { return null; }
    }

    /// <summary>GUID физических адаптеров, на которых сейчас включён IPv4.</summary>
    public static List<string> Targets()
    {
        var physical = NetAdapters.Physical().Select(a => a.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SettingID FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["SettingID"] is string id && physical.Contains(id)) result.Add(id);
                }
            }
        }
        catch { }
        return result;
    }

    private static void Set(string guid, List<string> servers)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{guid.Replace("'", "")}'");

        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var input = mo.GetMethodParameters("SetDNSServerSearchOrder");
                // null возвращает получение DNS от DHCP — именно так это делает и окно свойств адаптера.
                input["DNSServerSearchOrder"] = servers.Count == 0 ? null : servers.ToArray();
                var output = mo.InvokeMethod("SetDNSServerSearchOrder", input, null);
                var code = Convert.ToUInt32(output?["ReturnValue"] ?? 0u);

                // 0 — готово, 1 — готово, но нужна перезагрузка. Остальное — ошибка.
                if (code > 1)
                    throw new InvalidOperationException(code == 91
                        ? L10n.T("Нужны права администратора")
                        : L10n.F("Windows отказалась менять DNS (код {0})", code));
            }
        }
    }
}
