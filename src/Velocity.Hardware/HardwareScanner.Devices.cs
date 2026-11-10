using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Velocity.Core;
using Velocity.Hardware.Internal;
using static Velocity.Hardware.Internal.NativeMethods;

namespace Velocity.Hardware;

[SupportedOSPlatform("windows")]
public sealed partial class HardwareScanner
{
    // ─────────────────────────── GPU ───────────────────────────

    private static List<GpuInfo> ScanGpus()
    {
        var gpus = new List<GpuInfo>();

        foreach (var v in Wmi.Query(
            "SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterCompatibility FROM Win32_VideoController"))
        {
            var name = v.Str("Name");
            if (name is null || IsVirtualDisplayAdapter(name)) continue;

            var vendor = DetectGpuVendor(name, v.Str("AdapterCompatibility"));
            // Microsoft Basic Display Adapter — не видеокарта, а признак отсутствия драйвера.
            var gpu = new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                DriverVersion = v.Str("DriverVersion"),
                DriverDate = v.CimDate("DriverDate"),
                PnpDeviceId = v.Str("PNPDeviceID"),
                IsIntegrated = IsIntegratedGpu(name, vendor)
            };

            gpu.VendorDriverVersion = ToVendorDriverVersion(vendor, gpu.DriverVersion);
            gpu.VramGb = ReadVramGb(name, gpu.PnpDeviceId);
            gpus.Add(gpu);
        }

        // Дискретная карта первой — на неё вешаются GPU-твики.
        return [.. gpus.OrderBy(g => g.IsIntegrated).ThenByDescending(g => g.VramGb)];
    }

    /// <summary>
    /// Программные «видеокарты» стриминга и удалённого доступа (Parsec, Sunshine, RDP, Moonlight).
    /// Физически их не существует, VRAM у них нет — в отчёте они только сбивают с толку.
    /// </summary>
    private static bool IsVirtualDisplayAdapter(string name) =>
        Regex.IsMatch(name,
            @"parsec|sunshine|virtual\s*display|idd\s|indirect\s*display|remote\s*display|" +
            @"rdp\s|meta\s*virtual|citrix|teradici|usb\s*display|displaylink|spacedesk",
            RegexOptions.IgnoreCase);

    private static GpuVendor DetectGpuVendor(string name, string? compatibility)
    {
        var s = $"{name} {compatibility}";
        if (s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (s.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("ATI ", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        if (s.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Microsoft;
        return GpuVendor.Unknown;
    }

    private static bool IsIntegratedGpu(string name, GpuVendor vendor) => vendor switch
    {
        GpuVendor.Intel => !name.Contains("Arc", StringComparison.OrdinalIgnoreCase),
        GpuVendor.Amd => name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
                         && !name.Contains("RX", StringComparison.OrdinalIgnoreCase),
        GpuVendor.Microsoft => true,
        _ => false
    };

    /// <summary>
    /// NVIDIA показывает пользователю 566.36, а Windows хранит 32.0.15.6636.
    /// Правило: убрать точки, взять последние 5 цифр, поставить точку перед двумя последними.
    /// </summary>
    private static string? ToVendorDriverVersion(GpuVendor vendor, string? windowsVersion)
    {
        if (vendor != GpuVendor.Nvidia || string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var digits = windowsVersion.Replace(".", "");
        if (digits.Length < 5) return null;
        var tail = digits[^5..];
        return $"{tail[..3]}.{tail[3..]}";
    }

    /// <summary>
    /// Win32_VideoController.AdapterRAM — 32-битное поле, оно переполняется на картах >4 ГБ
    /// и выдаёт мусор. Настоящий объём лежит в ключе класса дисплея.
    /// </summary>
    private static double ReadVramGb(string gpuName, string? pnpDeviceId)
    {
        // VEN_10DE&DEV_2216 — этим сопоставляем адаптер с его ключом в реестре.
        // Сравнение по имени драйвера ломается на двух одинаковых картах и на гибридной графике,
        // где DriverDesc у встроенной и дискретной может совпадать.
        var venDev = pnpDeviceId is not null
            ? Regex.Match(pnpDeviceId, @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)
            : Match.Empty;

        foreach (var sub in Reg.SubKeys(RegistryHive.LocalMachine, Reg.GpuClassKey))
        {
            if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
            var path = $@"{Reg.GpuClassKey}\{sub}";

            bool matched = false;
            if (venDev.Success &&
                Reg.Str(RegistryHive.LocalMachine, path, "MatchingDeviceId") is { } matchingId)
            {
                matched = matchingId.Contains($"VEN_{venDev.Groups[1].Value}", StringComparison.OrdinalIgnoreCase)
                       && matchingId.Contains($"DEV_{venDev.Groups[2].Value}", StringComparison.OrdinalIgnoreCase);
            }

            if (!matched)
            {
                var desc = Reg.Str(RegistryHive.LocalMachine, path, "DriverDesc");
                matched = string.Equals(desc, gpuName, StringComparison.OrdinalIgnoreCase);
            }

            if (!matched) continue;

            if (Reg.Raw(RegistryHive.LocalMachine, path, "HardwareInformation.qwMemorySize") is { } qw
                && long.TryParse(qw.ToString(), out var bytes) && bytes > 0)
                return Math.Round(bytes / 1024d / 1024d / 1024d, 1);

            if (Reg.Raw(RegistryHive.LocalMachine, path, "HardwareInformation.MemorySize") is byte[] { Length: >= 4 } raw)
            {
                long size = BitConverter.ToUInt32(raw, 0);
                if (size > 0) return Math.Round(size / 1024d / 1024d / 1024d, 1);
            }
        }
        return 0;
    }

    // ─────────────────────────── Дисплеи ───────────────────────────

    private static List<DisplayInfo> ScanDisplays()
    {
        var displays = new List<DisplayInfo>();
        var monitorNames = ReadMonitorFriendlyNames();

        var device = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            device.cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>();

            bool attached = (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            bool mirroring = (device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
            if (!attached || mirroring) continue;

            var current = new DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref current)) continue;

            var info = new DisplayInfo
            {
                DeviceName = device.DeviceName,
                IsPrimary = (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                Width = (int)current.dmPelsWidth,
                Height = (int)current.dmPelsHeight,
                RefreshHz = (int)current.dmDisplayFrequency,
                BitsPerPixel = (int)current.dmBitsPerPel
            };

            // Перебираем все режимы: нам нужен максимум Гц на текущем разрешении
            // (именно это сравнение и ловит «165-герцовый монитор на 60 Гц»).
            var mode = new DEVMODE { dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            for (int m = 0; EnumDisplaySettings(device.DeviceName, m, ref mode); m++)
            {
                mode.dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>();
                int hz = (int)mode.dmDisplayFrequency;
                if (hz <= 1) continue; // 0 и 1 = «частота по умолчанию адаптера»

                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight)
                    info.MaxRefreshAtCurrentResolution = Math.Max(info.MaxRefreshAtCurrentResolution, hz);

                if (hz > info.MaxRefreshAnyResolution) info.MaxRefreshAnyResolution = hz;

                if ((int)mode.dmPelsWidth * (int)mode.dmPelsHeight > info.MaxWidth * info.MaxHeight)
                {
                    info.MaxWidth = (int)mode.dmPelsWidth;
                    info.MaxHeight = (int)mode.dmPelsHeight;
                }
            }

            info.FriendlyName = ResolveMonitorName(device.DeviceName, monitorNames);
            displays.Add(info);
        }

        return displays;
    }

    /// <summary>
    /// Имя монитора берём из EDID и сопоставляем по PnP-идентификатору устройства,
    /// а НЕ по порядковому номеру: на нескольких мониторах порядок в WMI и в
    /// EnumDisplayDevices не совпадает, и имена перепутались бы местами.
    /// </summary>
    private static string? ResolveMonitorName(string adapterName, Dictionary<string, string> monitorNames)
    {
        var monitor = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevices(adapterName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
            return null;

        // DeviceID: \\?\DISPLAY#GSM5B09#5&1a2b&0&UID4353#{guid}
        // InstanceName в WMI: DISPLAY\GSM5B09\5&1a2b&0&UID4353_0
        var key = NormalizeMonitorId(monitor.DeviceID);
        if (key is not null && monitorNames.TryGetValue(key, out var name)) return name;

        // Фолбэк — «Универсальный монитор PnP». Не идеально, но честнее, чем имя видеокарты.
        return string.IsNullOrWhiteSpace(monitor.DeviceString) ? null : monitor.DeviceString;
    }

    /// <summary>Приводит идентификатор монитора к общему виду: HARDWAREID|INSTANCEID.</summary>
    private static string? NormalizeMonitorId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Replace(@"\\?\", "").Replace('#', '\\');
        var parts = cleaned.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // Нужны сегменты после «DISPLAY»: модель и instance id.
        var displayIndex = Array.FindIndex(parts, p => p.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase));
        if (displayIndex < 0 || parts.Length < displayIndex + 3) return null;

        var hardwareId = parts[displayIndex + 1];
        // У WMI instance id заканчивается на «_0», у DeviceID этого суффикса нет.
        var instanceId = parts[displayIndex + 2].TrimEnd('0').TrimEnd('_');

        return $"{hardwareId}|{instanceId}".ToUpperInvariant();
    }

    /// <summary>Имена мониторов из EDID (root\wmi), ключ — нормализованный PnP-идентификатор.</summary>
    private static Dictionary<string, string> ReadMonitorFriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in Wmi.Query("SELECT UserFriendlyName, InstanceName FROM WmiMonitorID", @"root\wmi"))
        {
            if (m.UInt16Array("UserFriendlyName") is not { } chars) continue;
            var text = new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
            if (text.Length == 0) continue;

            if (NormalizeMonitorId(m.Str("InstanceName")) is { } key) names[key] = text;
        }

        return names;
    }

    // ─────────────────────────── Накопители ───────────────────────────

    private static List<StorageInfo> ScanStorage()
    {
        var result = new List<StorageInfo>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');

        // MSFT_PhysicalDisk знает MediaType и BusType, Win32_DiskDrive — буквы разделов.
        var physical = Wmi.Query(
            "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk",
            @"root\Microsoft\Windows\Storage").ToList();

        var letterMap = BuildDriveLetterMap();

        foreach (var d in Wmi.Query("SELECT DeviceID, Index, Model, Size FROM Win32_DiskDrive"))
        {
            var index = d.Int("Index");
            var match = physical.FirstOrDefault(p => p.Str("DeviceId") == index?.ToString());

            var info = new StorageInfo
            {
                Model = match?.Str("FriendlyName") ?? d.Str("Model"),
                SizeGb = Math.Round((d.ULong("Size") ?? match?.ULong("Size") ?? 0) / 1024d / 1024d / 1024d, 1),
                HealthStatus = MapHealth(match?.UInt("HealthStatus")),
                BusType = MapBusType(match?.UInt("BusType")),
                Kind = MapMediaKind(match?.UInt("MediaType"), match?.UInt("BusType")),
                // USB / SD / MMC — флешки и внешние диски. Судить о них как о системных нельзя:
                // «мало свободного места на флешке» это не проблема производительности.
                IsRemovable = match?.UInt("BusType") is 7 or 12 or 13
            };

            if (d.Str("DeviceID") is { } deviceId && letterMap.TryGetValue(deviceId, out var mapped))
            {
                foreach (var letter in mapped)
                {
                    info.DriveLetters.Add(letter);
                    if (string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase))
                        info.IsSystemDrive = true;

                    try
                    {
                        var drive = new DriveInfo(letter);
                        if (drive.IsReady) info.FreeGb += Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 1);
                    }
                    catch { /* съёмный или недоступный том */ }
                }
            }

            info.FreeGb = Math.Round(info.FreeGb, 1);
            result.Add(info);
        }

        return [.. result.OrderByDescending(x => x.IsSystemDrive)];
    }

    /// <summary>
    /// Строит карту «физический диск → буквы томов» за два запроса.
    ///
    /// Раньше здесь был ASSOCIATORS OF на каждый диск и на каждый раздел — на системе
    /// с тремя дисками это десяток отдельных запросов, а каждый стоит десятки миллисекунд.
    /// Читать классы-ассоциации целиком и собирать связи в памяти — на порядок быстрее.
    /// </summary>
    private static Dictionary<string, List<string>> BuildDriveLetterMap()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Раздел → буквы томов
        var partitionToLetters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in Wmi.Query("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
        {
            var partition = ExtractDeviceId(link.Str("Antecedent"));
            var letter = ExtractDeviceId(link.Str("Dependent"));
            if (partition is null || letter is null) continue;

            if (!partitionToLetters.TryGetValue(partition, out var letters))
                partitionToLetters[partition] = letters = [];
            letters.Add(letter);
        }

        // Физический диск → разделы → буквы
        foreach (var link in Wmi.Query("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
        {
            var disk = ExtractDeviceId(link.Str("Antecedent"));
            var partition = ExtractDeviceId(link.Str("Dependent"));
            if (disk is null || partition is null) continue;
            if (!partitionToLetters.TryGetValue(partition, out var letters)) continue;

            if (!map.TryGetValue(disk, out var existing))
                map[disk] = existing = [];
            existing.AddRange(letters);
        }

        return map;
    }

    [GeneratedRegex("DeviceID=\"(.+?)\"", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdInPath();

    /// <summary>
    /// Из пути-ссылки WMI достаёт сам идентификатор устройства.
    /// В пути слэши экранированы удвоением — снимаем экранирование.
    /// </summary>
    private static string? ExtractDeviceId(string? objectPath)
    {
        if (objectPath is null) return null;
        var m = DeviceIdInPath().Match(objectPath);
        return m.Success ? m.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    private static StorageKind MapMediaKind(uint? mediaType, uint? busType) => (mediaType, busType) switch
    {
        (_, 17) => StorageKind.Nvme,   // BusType NVMe перевешивает: MediaType там часто 0
        (3, _) => StorageKind.Hdd,
        (4, _) => StorageKind.Ssd,
        (5, _) => StorageKind.Ssd,     // SCM (Storage Class Memory)
        _ => StorageKind.Unknown
    };

    private static string? MapBusType(uint? busType) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
        12 => "SD", 13 => "MMC", 16 => "Storage Spaces", 17 => "NVMe",
        _ => null
    };

    private static string? MapHealth(uint? health) => health switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null
    };

    // ─────────────────────────── Сеть ───────────────────────────

    private static List<NetworkInfo> ScanNetwork()
    {
        var result = new List<NetworkInfo>();

        foreach (var a in Wmi.Query(
            "SELECT Name, NetConnectionID, Speed, NetConnectionStatus, GUID, PNPDeviceID " +
            "FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE"))
        {
            var name = a.Str("Name");
            if (name is null) continue;

            var info = new NetworkInfo
            {
                Name = name,
                ConnectionId = a.Str("NetConnectionID"),
                LinkSpeedMbps = (long)((a.ULong("Speed") ?? 0) / 1_000_000),
                Connected = a.UInt("NetConnectionStatus") == 2,
                InterfaceGuid = a.Str("GUID"),
                Kind = DetectNetworkKind(name, a.Str("NetConnectionID"))
            };

            if (info.InterfaceGuid is { } guid)
            {
                var path = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}";
                var ack = Reg.Int(RegistryHive.LocalMachine, path, "TcpAckFrequency");
                var noDelay = Reg.Int(RegistryHive.LocalMachine, path, "TCPNoDelay");
                info.NagleDisabled = ack == 1 && noDelay == 1;
            }

            result.Add(info);
        }

        return [.. result.OrderByDescending(x => x.Connected)];
    }

    private static NetworkKind DetectNetworkKind(string name, string? connectionId)
    {
        var s = $"{name} {connectionId}";
        if (Regex.IsMatch(s, @"wi[- ]?fi|wireless|802\.11|wlan", RegexOptions.IgnoreCase)) return NetworkKind.WiFi;
        if (s.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return NetworkKind.Bluetooth;
        // «tap»/«tun» только как отдельные слова: иначе под них попадёт любой «Fortune»/«Realtek».
        if (Regex.IsMatch(s, @"virtual|vmware|virtualbox|hyper-v|loopback|\bvpn\b|wireguard|openvpn|\btap\b|\btun\b",
                RegexOptions.IgnoreCase)) return NetworkKind.Virtual;
        return NetworkKind.Ethernet;
    }

    // ─────────────────────────── Ввод ───────────────────────────

    private static InputInfo ScanInput()
    {
        var input = new InputInfo();

        foreach (var m in Wmi.Query("SELECT Name, PNPDeviceID FROM Win32_PointingDevice"))
            if (BuildHid(m) is { } hid) input.Mice.Add(hid);

        foreach (var k in Wmi.Query("SELECT Name, PNPDeviceID FROM Win32_Keyboard"))
            if (BuildHid(k) is { } hid) input.Keyboards.Add(hid);

        // MouseSpeed=1 — это и есть «Повышенная точность установки указателя» (акселерация).
        input.PointerPrecisionEnabled = Reg.Str(RegistryHive.CurrentUser, Reg.HkcuMouse, "MouseSpeed") is "1";
        input.MouseSensitivity = int.TryParse(
            Reg.Str(RegistryHive.CurrentUser, Reg.HkcuMouse, "MouseSensitivity"), out var s) ? s : 0;
        input.MouseDataQueueSize = Reg.Int(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize") ?? 0;
        input.KeyboardDataQueueSize = Reg.Int(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize") ?? 0;

        return input;
    }

    [GeneratedRegex(@"VID[_&]([0-9A-F]{4}).*?PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidPattern();

    private static HidDevice? BuildHid(System.Management.ManagementObject mo)
    {
        var name = mo.Str("Name");
        if (name is null) return null;

        var device = new HidDevice { Name = name };
        var pnp = mo.Str("PNPDeviceID");
        if (pnp is not null)
        {
            var match = VidPidPattern().Match(pnp);
            if (match.Success)
            {
                device.VendorId = match.Groups[1].Value.ToUpperInvariant();
                device.ProductId = match.Groups[2].Value.ToUpperInvariant();
                device.VendorName = KnownVendors.GetValueOrDefault(device.VendorId);
            }
        }
        return device;
    }

    /// <summary>USB VID популярных игровых периферийщиков — нужен для подсказок по их софту.</summary>
    private static readonly Dictionary<string, string> KnownVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = "Logitech",
        ["1532"] = "Razer",
        ["1038"] = "SteelSeries",
        ["1B1C"] = "Corsair",
        ["04D9"] = L10n.T("Holtek (Glorious / китайские OEM)"),
        ["3151"] = L10n.T("Compx (Ajazz / VGN / Attack Shark и др.)"),
        ["3554"] = "Pulsar",
        ["258A"] = "SINOWEALTH (Glorious / VGN / Ajazz)",
        ["0B05"] = "ASUS",
        ["09DA"] = "A4Tech",
        ["2F68"] = "Endgame Gear",
        ["195D"] = "Zowie / BenQ",
        ["30FA"] = "Lamzu / VXE",
        ["413C"] = "Dell",
        ["045E"] = "Microsoft",
        ["0951"] = "HyperX / Kingston",
        ["2EA8"] = "Cooler Master"
    };

    // ─────────────────────────── Автозагрузка ───────────────────────────

    /// <summary>Известные пожиратели ресурсов в автозагрузке — по ним даём отдельный совет.</summary>
    private static readonly Dictionary<string, string> HeavyStartupItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["icue"] = "Corsair iCUE",
        ["armourycrate"] = "ASUS Armoury Crate",
        ["razer synapse"] = "Razer Synapse",
        ["lghub"] = "Logitech G HUB",
        ["msi center"] = "MSI Center",
        ["aura"] = "ASUS Aura",
        ["wallpaper engine"] = "Wallpaper Engine",
        ["epicgameslauncher"] = "Epic Games Launcher",
        ["eadesktop"] = "EA Desktop",
        ["ubisoft"] = "Ubisoft Connect",
        ["battle.net"] = "Battle.net",
        ["adobe"] = L10n.T("Adobe (обновлятор)"),
        ["onedrive"] = "OneDrive",
        ["teams"] = "Microsoft Teams",
        ["cortana"] = "Cortana"
    };

    private static StartupInfo ScanStartup()
    {
        var info = new StartupInfo();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        (RegistryHive Hive, string Path)[] runKeys =
        [
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
        ];

        foreach (var (hive, path) in runKeys)
            foreach (var name in Reg.ValueNames(hive, path))
                if (seen.Add(name)) info.Items.Add(name);

        // Плюс папки автозагрузки — там живёт то, чего нет в реестре.
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            try
            {
                var dir = Environment.GetFolderPath(folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(name)) info.Items.Add(name);
                }
            }
            catch { /* нет доступа к папке */ }
        }

        info.ItemCount = info.Items.Count;
        info.HeavyItems = [.. info.Items
            .Select(item => HeavyStartupItems.FirstOrDefault(h =>
                item.Contains(h.Key, StringComparison.OrdinalIgnoreCase)).Value)
            .Where(label => label is not null)
            .Distinct()
            .Order()];

        return info;
    }

    // ─────────────────── Настройки Windows ───────────────────

    private static WindowsTuningState ScanTuning()
    {
        var t = new WindowsTuningState();

        ReadPowerPlan(t);

        t.GameDvrEnabled = Reg.Flag(RegistryHive.CurrentUser, Reg.HkcuGameConfigStore, "GameDVR_Enabled");
        t.GameBarEnabled = Reg.Flag(RegistryHive.CurrentUser, Reg.HkcuGameBar, "UseNexusForGameBarEnabled");
        t.GameModeEnabled = Reg.Flag(RegistryHive.CurrentUser, Reg.HkcuGameBar, "AutoGameModeEnabled");

        // HwSchMode: 1 = выключено, 2 = включено. Отсутствие ключа = не поддерживается.
        t.HardwareAcceleratedGpuScheduling =
            Reg.Int(RegistryHive.LocalMachine, Reg.HklmGraphicsDrivers, "HwSchMode") is { } hw ? hw == 2 : null;

        ReadDeviceGuard(t);

        t.SystemResponsiveness = Reg.Int(RegistryHive.LocalMachine, Reg.HklmMultimediaProfile, "SystemResponsiveness");
        t.NetworkThrottlingIndex = Reg.UInt(RegistryHive.LocalMachine, Reg.HklmMultimediaProfile, "NetworkThrottlingIndex");
        t.GamesGpuPriority = Reg.Int(RegistryHive.LocalMachine, Reg.HklmGamesTask, "GPU Priority");
        t.GamesPriority = Reg.Int(RegistryHive.LocalMachine, Reg.HklmGamesTask, "Priority");

        t.TransparencyEnabled = Reg.Flag(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency");
        t.GlobalTimerResolutionRequests = Reg.Flag(RegistryHive.LocalMachine,
            Reg.HklmSessionKernel, "GlobalTimerResolutionRequests");
        t.CurrentTimerResolutionMs = GetTimerResolutionMs() is { } ms ? Math.Round(ms, 4) : null;

        t.ActiveOverlays = DetectProcesses(OverlayProcesses);
        t.ActiveAntiCheats = DetectProcesses(AntiCheatProcesses);
        t.HarmfulTweaksDetected = DetectHarmfulTweaks(t);

        return t;
    }

    private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    /// <summary>
    /// Читаем схему питания из реестра, а не из powercfg: вывод powercfg приходит
    /// в OEM-кодировке консоли и на локализованной Windows превращается в кракозябры.
    /// </summary>
    private static void ReadPowerPlan(WindowsTuningState t)
    {
        var activeGuid = Reg.Str(RegistryHive.LocalMachine, PowerSchemesKey, "ActivePowerScheme");
        if (activeGuid is null) return;

        t.PowerPlanGuid = activeGuid;

        // FriendlyName у встроенных схем — ссылка на ресурс powrprof.dll, её нужно резолвить.
        if (Reg.Str(RegistryHive.LocalMachine, $@"{PowerSchemesKey}\{activeGuid}", "FriendlyName") is { } friendly)
            t.PowerPlanName = ResolveIndirectString(friendly);

        // Схема Ultimate Performance скрыта, пока её не продублируют — проверяем, есть ли она вообще.
        t.UltimatePerformanceAvailable = Reg
            .SubKeys(RegistryHive.LocalMachine, PowerSchemesKey)
            .Any(k => k.Contains(UltimatePerformanceGuid, StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadDeviceGuard(WindowsTuningState t)
    {
        if (Wmi.QueryFirst(
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard",
                @"root\Microsoft\Windows\DeviceGuard") is { } dg)
        {
            // VirtualizationBasedSecurityStatus: 0 выкл, 1 включён но не запущен, 2 включён и работает
            t.VbsEnabled = dg.UInt("VirtualizationBasedSecurityStatus") == 2;

            // SecurityServicesRunning: 1 = Credential Guard, 2 = HVCI (Memory Integrity)
            if (dg["SecurityServicesRunning"] is uint[] services)
                t.HvciEnabled = services.Contains(2u);
        }

        // Реестровое значение — то, что реально переключает Memory Integrity в UI.
        t.HvciEnabled ??= Reg.Flag(RegistryHive.LocalMachine, Reg.HklmHvci, "Enabled");
    }

    private static readonly Dictionary<string, string> OverlayProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GameOverlayUI"] = "Steam Overlay",
        ["Discord"] = "Discord",
        ["NVIDIA Share"] = "NVIDIA Overlay (ShadowPlay)",
        ["NVIDIA Overlay"] = "NVIDIA Overlay",
        ["RTSS"] = "RivaTuner Statistics Server",
        ["RTSSHooksLoader64"] = "RTSS Hooks",
        ["MSIAfterburner"] = "MSI Afterburner",
        ["EpicGamesLauncher"] = "Epic Games Overlay",
        ["Medal"] = "Medal.tv",
        ["obs64"] = "OBS Studio",
        ["wallpaper64"] = "Wallpaper Engine",
        ["iCUE"] = "Corsair iCUE",
        ["Razer Synapse 3"] = "Razer Synapse",
        ["LGHUB"] = "Logitech G HUB",
        ["ArmouryCrate.UserSessionHelper"] = "ASUS Armoury Crate"
    };

    private static readonly Dictionary<string, string> AntiCheatProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vgc"] = "Riot Vanguard",
        ["vgtray"] = "Riot Vanguard",
        ["EasyAntiCheat"] = "Easy Anti-Cheat",
        ["EasyAntiCheat_EOS"] = "Easy Anti-Cheat (EOS)",
        ["BEService"] = "BattlEye",
        ["FACEITService"] = "FACEIT AC",
        ["ESEAClient"] = "ESEA AC"
    };

    private static List<string> DetectProcesses(Dictionary<string, string> known)
    {
        var found = new HashSet<string>();

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return []; }

        foreach (var p in processes)
        {
            // ProcessName у защищённых процессов (в т.ч. у самих античитов) бросает Win32Exception.
            // Ловим поштучно, иначе один защищённый процесс обрывает весь перебор.
            try
            {
                if (known.TryGetValue(p.ProcessName, out var label)) found.Add(label);
            }
            catch { /* недоступен — пропускаем */ }
            finally { p.Dispose(); }
        }

        return [.. found.Order()];
    }

    /// <summary>
    /// Фича «Детокс»: ищем следы вредных твиков из чужих .bat/.reg-паков.
    /// Всё, что здесь перечислено, разобрано в docs/02-OPTIMIZATION-SPEC.md, раздел «Мифы».
    /// </summary>
    private static List<string> DetectHarmfulTweaks(WindowsTuningState t)
    {
        var found = new List<string>();

        if (t.SystemResponsiveness == 0)
            found.Add(L10n.T("SystemResponsiveness = 0 — душит аудиопоток, возможен треск звука. Норма: 10"));

        if (Reg.Flag(RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware") == true)
            found.Add(L10n.T("Defender отключён через политику — вместо этого нужны исключения папок игр"));

        if (Reg.Int(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation") is { } prio
            && prio is not (2 or 26 or 38))
            found.Add(L10n.F("Win32PrioritySeparation = {0} — нестандартное значение, обычно из .bat-пака. Норма: 2 или 38", prio));

        if (Reg.Int(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive") == 1)
            found.Add(L10n.T("DisablePagingExecutive = 1 — карго-культ, на FPS не влияет, память расходуется хуже"));

        if (Reg.Str(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services\Ndu", "Start") == "4")
            found.Add(L10n.T("Служба Ndu отключена — ломает мониторинг сети в диспетчере задач, эффекта на FPS нет"));

        return found;
    }
}
