using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Velocity.Core;
using Velocity.Hardware.Internal;

namespace Velocity.Hardware;

/// <summary>
/// Собирает полный снимок системы. Устройство модуля подчинено одному правилу:
/// падение любого отдельного сканера не должно ронять весь отчёт — вместо этого
/// ошибка попадает в <see cref="HardwareReport.ScanErrors"/>, а поля остаются пустыми.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class HardwareScanner
{
    private const string HwidSalt = "velocity.v1";

    /// <summary>
    /// Полный скан. Самостоятельные сканеры идут параллельно: почти всё время скана
    /// — это ожидание ответа WMI, а не работа процессора, так что выигрыш почти линейный.
    /// </summary>
    public HardwareReport Scan(IProgress<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var report = new HardwareReport();
        var errors = new ConcurrentBag<string>();

        var steps = new (string Name, string Label, Action Action)[]
        {
            ("machine",  L10n.T("плата и BIOS"),       () => report.Machine = ScanMachine()),
            ("os",       "Windows",            () => report.Os = ScanOs()),
            ("cpu",      L10n.T("процессор"),           () => report.Cpu = ScanCpu()),
            ("memory",   L10n.T("память"),             () => report.Memory = ScanMemory()),
            ("gpu",      L10n.T("видеокарта"),         () => report.Gpus = ScanGpus()),
            ("display",  L10n.T("мониторы"),           () => report.Displays = ScanDisplays()),
            ("storage",  L10n.T("накопители"),        () => report.Storage = ScanStorage()),
            ("network",  L10n.T("сеть"),                () => report.Network = ScanNetwork()),
            ("input",    L10n.T("мышь и клавиатура"), () => report.Input = ScanInput()),
            ("tuning",   L10n.T("настройки Windows"),  () => report.Tuning = ScanTuning()),
            ("startup",  L10n.T("автозагрузка"),        () => report.Startup = ScanStartup()),
            ("games",    L10n.T("библиотеки игр"),    () => report.Games = GameLibraryScanner.Scan())
        };

        Parallel.ForEach(steps, step =>
        {
            progress?.Report(step.Label);
            try { step.Action(); }
            catch (Exception ex) { errors.Add($"{step.Name}: {ex.GetType().Name}: {ex.Message}"); }
        });

        report.ScanErrors = [.. errors.Order()];

        // Связываем игры с накопителями уже после того, как оба скана отработали.
        try { GameLibraryScanner.AttachDriveKinds(report.Games, report.Storage); }
        catch (Exception ex) { report.ScanErrors.Add($"games.drives: {ex.Message}"); }

        sw.Stop();
        report.ScanDurationMs = sw.ElapsedMilliseconds;
        return report;
    }

    // ─────────────────────────── Машина ───────────────────────────

    private static MachineInfo ScanMachine()
    {
        var info = new MachineInfo();

        if (Wmi.QueryFirst("SELECT Manufacturer, Model FROM Win32_ComputerSystem") is { } cs)
        {
            info.Manufacturer = cs.Str("Manufacturer");
            info.Model = cs.Str("Model");
        }

        if (Wmi.QueryFirst("SELECT Manufacturer, Product FROM Win32_BaseBoard") is { } bb)
        {
            info.BoardManufacturer = bb.Str("Manufacturer");
            info.BoardProduct = bb.Str("Product");
        }

        if (Wmi.QueryFirst("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS") is { } bios)
        {
            info.BiosVendor = bios.Str("Manufacturer");
            info.BiosVersion = bios.Str("SMBIOSBIOSVersion");
            info.BiosDate = bios.CimDate("ReleaseDate");
        }

        info.Chassis = DetectChassis();
        info.SecureBoot = Reg.Flag(RegistryHive.LocalMachine, Reg.HklmSecureBoot, "UEFISecureBootEnabled");

        // UUID материнской платы — самый стабильный компонент HWID.
        // В отчёт кладём только хэш: сырой UUID это идентификатор устройства.
        if (Wmi.QueryFirst("SELECT UUID FROM Win32_ComputerSystemProduct")?.Str("UUID") is { } uuid
            && !uuid.StartsWith("FFFFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            info.SystemUuidHash = Hash(uuid);
        }

        return info;
    }

    private static ChassisKind DetectChassis()
    {
        // Батарея — самый надёжный признак ноутбука: SMBIOS ChassisTypes
        // OEM'ы заполняют как попало (часто 3 «Desktop» на игровом ноуте).
        if (Wmi.Query("SELECT BatteryStatus FROM Win32_Battery").Any())
            return ChassisKind.Laptop;

        var types = Wmi.QueryFirst("SELECT ChassisTypes FROM Win32_SystemEnclosure")?.UInt16Array("ChassisTypes");
        if (types is null || types.Length == 0) return ChassisKind.Unknown;

        return types[0] switch
        {
            8 or 9 or 10 or 14 or 30 or 31 or 32 => ChassisKind.Laptop,
            11 => ChassisKind.Handheld,
            13 => ChassisKind.AllInOne,
            17 or 23 or 28 => ChassisKind.Server,
            3 or 4 or 5 or 6 or 7 or 15 or 16 => ChassisKind.Desktop,
            _ => ChassisKind.Unknown
        };
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(HwidSalt + "|" + value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    // ─────────────────────────── ОС ───────────────────────────

    private static OsInfo ScanOs()
    {
        var os = new OsInfo
        {
            Build = Reg.Int(RegistryHive.LocalMachine, Reg.HklmCurrentVersion, "CurrentBuildNumber") ?? 0,
            Ubr = Reg.Int(RegistryHive.LocalMachine, Reg.HklmCurrentVersion, "UBR") ?? 0,
            DisplayVersion = Reg.Str(RegistryHive.LocalMachine, Reg.HklmCurrentVersion, "DisplayVersion"),
            Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Locale = System.Globalization.CultureInfo.CurrentUICulture.Name
        };

        // ProductName в реестре на Windows 11 до сих пор пишет «Windows 10» — правим по билду.
        var productName = Reg.Str(RegistryHive.LocalMachine, Reg.HklmCurrentVersion, "ProductName");
        if (os.Build >= 22000 && productName?.Contains("Windows 10") == true)
            productName = productName.Replace("Windows 10", "Windows 11");
        os.ProductName = productName;

        if (Wmi.QueryFirst("SELECT InstallDate FROM Win32_OperatingSystem") is { } wos)
            os.InstallDate = wos.CimDate("InstallDate");

        return os;
    }

    // ─────────────────────────── CPU ───────────────────────────

    private static CpuInfo ScanCpu()
    {
        var cpu = new CpuInfo { LogicalCores = Environment.ProcessorCount };

        if (Wmi.QueryFirst(
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, " +
            "L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor") is { } p)
        {
            cpu.Name = p.Str("Name");
            cpu.Vendor = p.Str("Manufacturer");
            cpu.PhysicalCores = p.Int("NumberOfCores") ?? 0;
            cpu.LogicalCores = p.Int("NumberOfLogicalProcessors") ?? Environment.ProcessorCount;
            cpu.MaxClockMhz = p.Int("MaxClockSpeed") ?? 0;
            cpu.L2CacheKb = p.Int("L2CacheSize") ?? 0;
            cpu.L3CacheKb = p.Int("L3CacheSize") ?? 0;
            cpu.Socket = p.Str("SocketDesignation");
            cpu.VirtualizationEnabled = p.Bool("VirtualizationFirmwareEnabled");
        }

        // Гибридная топология (P/E-ядра) и L3-домены (CCD) — только через WinAPI,
        // WMI об этом ничего не знает.
        if (NativeMethods.ReadCoreTopology() is { } cores)
        {
            if (cpu.PhysicalCores == 0) cpu.PhysicalCores = cores.PhysicalCores;
            cpu.IsHybrid = cores.IsHybrid;
            cpu.PerformanceCores = cores.PerformanceCores;
            cpu.EfficiencyCores = cores.EfficiencyCores;
        }

        if (NativeMethods.ReadCacheTopology() is { } cache)
        {
            cpu.L3Domains = cache.L3Domains;
            cpu.IsAsymmetricCache = cache.Asymmetric;
            if (cpu.L3CacheKb == 0) cpu.L3CacheKb = cache.LargestL3Kb;
        }

        return cpu;
    }

    // ─────────────────────────── Память ───────────────────────────

    private static MemoryInfo ScanMemory()
    {
        var mem = new MemoryInfo();

        if (Wmi.QueryFirst("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")?.ULong("TotalPhysicalMemory") is { } total)
            mem.TotalGb = Math.Round(total / 1024d / 1024d / 1024d, 1);

        if (Wmi.QueryFirst("SELECT FreePhysicalMemory FROM Win32_OperatingSystem")?.ULong("FreePhysicalMemory") is { } free)
            mem.AvailableGb = Math.Round(free / 1024d / 1024d, 1);

        if (Wmi.QueryFirst("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray")?.Int("MemoryDevices") is { } slots)
            mem.SlotsTotal = slots;

        foreach (var m in Wmi.Query(
            "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, " +
            "DeviceLocator, BankLabel, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            int configured = (int)(m.UInt("ConfiguredClockSpeed") ?? 0);
            int declared = (int)(m.UInt("Speed") ?? 0);
            if (configured == 0) configured = declared;

            mem.Modules.Add(new MemoryModule
            {
                Slot = m.Str("DeviceLocator"),
                BankLabel = m.Str("BankLabel"),
                CapacityGb = Math.Round((m.ULong("Capacity") ?? 0) / 1024d / 1024d / 1024d, 1),
                ConfiguredSpeedMhz = configured,
                RatedSpeedMhz = Math.Max(declared, configured),
                Manufacturer = m.Str("Manufacturer"),
                PartNumber = m.Str("PartNumber")
            });

            if (mem.Kind == MemoryKind.Unknown && m.Int("SMBIOSMemoryType") is { } smbios)
                mem.Kind = MapMemoryKind(smbios);
        }

        mem.SlotsUsed = mem.Modules.Count;
        if (mem.SlotsTotal == 0) mem.SlotsTotal = mem.SlotsUsed;
        mem.ConfiguredSpeedMhz = mem.Modules.Count > 0 ? mem.Modules.Min(x => x.ConfiguredSpeedMhz) : 0;
        mem.RatedSpeedMhz = mem.Modules.Count > 0 ? mem.Modules.Max(x => x.RatedSpeedMhz) : 0;
        mem.Channels = CountChannels(mem.Modules);
        mem.Pagefile = ScanPagefile();

        return mem;
    }

    private static MemoryKind MapMemoryKind(int smbiosType) => smbiosType switch
    {
        24 => MemoryKind.Ddr3,
        26 => MemoryKind.Ddr4,
        30 => MemoryKind.Lpddr4,
        34 => MemoryKind.Ddr5,
        35 => MemoryKind.Lpddr5,
        _ => MemoryKind.Unknown
    };

    [GeneratedRegex(@"(?:CHANNEL\s*|CH)([A-D0-9])|DIMM[_\- ]?([A-D])\d", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    /// <summary>
    /// Считает занятые каналы по именам слотов. OEM'ы пишут их по-разному
    /// («ChannelA-DIMM1», «DIMM_A2», «Controller0-ChannelB-DIMM0»), поэтому это эвристика:
    /// если распарсить не удалось — не выдумываем, возвращаем 0 («неизвестно»).
    /// </summary>
    private static int CountChannels(List<MemoryModule> modules)
    {
        if (modules.Count == 0) return 0;

        var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            foreach (var source in new[] { m.BankLabel, m.Slot })
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                var match = ChannelPattern().Match(source);
                if (!match.Success) continue;
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                channels.Add(value);
                break;
            }
        }

        return channels.Count;
    }

    private static PagefileInfo ScanPagefile()
    {
        var pf = new PagefileInfo();

        var configured = Wmi.Query("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting").ToList();
        foreach (var s in configured)
            if (s.Str("Name") is { } n) pf.Locations.Add(n);

        foreach (var u in Wmi.Query("SELECT Name, AllocatedBaseSize FROM Win32_PageFileUsage"))
        {
            pf.TotalSizeGb += (u.UInt("AllocatedBaseSize") ?? 0) / 1024d;
            if (u.Str("Name") is { } n && !pf.Locations.Contains(n)) pf.Locations.Add(n);
        }

        pf.TotalSizeGb = Math.Round(pf.TotalSizeGb, 1);
        // Пустой Win32_PageFileSetting при существующем файле = «управляется системой».
        pf.SystemManaged = configured.Count == 0 && pf.Locations.Count > 0;
        pf.Disabled = pf.Locations.Count == 0;

        return pf;
    }
}
