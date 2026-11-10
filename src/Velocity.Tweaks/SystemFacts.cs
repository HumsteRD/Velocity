using System.Globalization;
using Velocity.Core;

namespace Velocity.Tweaks;

/// <summary>
/// Плоский словарь фактов о системе, против которого проверяются условия твиков.
///
/// Зачем отдельный слой: условия в каталоге пишутся строками («gpu.vendor=nvidia»),
/// каталог приходит с сервера, и в нём не должно быть ссылок на классы C#.
/// </summary>
public sealed class SystemFacts
{
    private readonly Dictionary<string, string> _facts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> All => _facts;

    public static SystemFacts From(HardwareReport r)
    {
        var f = new SystemFacts();

        f.Set("os.build", r.Os.Build);
        f.Set("os.version", r.Os.IsWindows11 ? "11" : "10");
        f.Set("os.arch", r.Os.Architecture);

        f.Set("machine.chassis", r.Machine.Chassis.ToString());
        f.Set("machine.is_laptop", r.Machine.Chassis == ChassisKind.Laptop);
        f.Set("machine.board", r.Machine.BoardManufacturer);
        f.Set("machine.secure_boot", r.Machine.SecureBoot);

        f.Set("cpu.vendor", r.Cpu.IsAmd ? "amd" : r.Cpu.IsIntel ? "intel" : "unknown");
        f.Set("cpu.cores", r.Cpu.PhysicalCores);
        f.Set("cpu.threads", r.Cpu.LogicalCores);
        f.Set("cpu.is_hybrid", r.Cpu.IsHybrid);
        f.Set("cpu.p_cores", r.Cpu.PerformanceCores);
        f.Set("cpu.e_cores", r.Cpu.EfficiencyCores);
        f.Set("cpu.l3_domains", r.Cpu.L3Domains);
        f.Set("cpu.is_x3d", r.Cpu.IsAsymmetricCache || (r.Cpu.Name?.Contains("X3D", StringComparison.OrdinalIgnoreCase) ?? false));
        f.Set("cpu.name", r.Cpu.Name);
        // Ryzen X3D с двумя чиплетами (7900X3D, 7950X3D, 9900X3D, 9950X3D): игру на кэш-чиплет
        // переносит драйвер чипсета через парковку ядер, и ему нужна «Сбалансированная» схема.
        bool x3d = r.Cpu.IsAsymmetricCache || (r.Cpu.Name?.Contains("X3D", StringComparison.OrdinalIgnoreCase) ?? false);
        f.Set("cpu.x3d_dual_ccd", x3d && r.Cpu.L3Domains >= 2);

        var gpu = r.Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? r.Gpus.FirstOrDefault();
        f.Set("gpu.vendor", gpu?.Vendor.ToString().ToLowerInvariant() ?? "unknown");
        f.Set("gpu.name", gpu?.Name);
        f.Set("gpu.vram_gb", gpu?.VramGb ?? 0);
        f.Set("gpu.driver", gpu?.VendorDriverVersion ?? gpu?.DriverVersion);
        f.Set("gpu.has_integrated", r.Gpus.Any(g => g.IsIntegrated));
        f.Set("gpu.count", r.Gpus.Count);

        f.Set("ram.total_gb", r.Memory.TotalGb);
        f.Set("ram.speed", r.Memory.ConfiguredSpeedMhz);
        f.Set("ram.channels", r.Memory.Channels);
        f.Set("ram.kind", r.Memory.Kind.ToString().ToLowerInvariant());

        f.Set("display.count", r.Displays.Count);
        f.Set("display.max_hz", r.Displays.Count > 0 ? r.Displays.Max(d => d.MaxRefreshAtCurrentResolution) : 0);
        f.Set("display.current_hz", r.Displays.FirstOrDefault(d => d.IsPrimary)?.RefreshHz ?? 0);

        f.Set("storage.has_ssd", r.Storage.Any(s => s.Kind is StorageKind.Ssd or StorageKind.Nvme));
        f.Set("storage.has_hdd", r.Storage.Any(s => s.Kind == StorageKind.Hdd && !s.IsRemovable));

        f.Set("tuning.hags", r.Tuning.HardwareAcceleratedGpuScheduling);
        f.Set("tuning.vbs", r.Tuning.VbsEnabled);
        f.Set("tuning.hvci", r.Tuning.HvciEnabled);
        f.Set("tuning.gamedvr", r.Tuning.GameDvrEnabled);
        f.Set("tuning.anticheat_active", r.Tuning.ActiveAntiCheats.Count > 0);

        f.Set("games.count", r.Games.Count);

        // ── Для руководств и сетевых правил ──
        var physicalNet = r.Network.Where(n => n.Kind is NetworkKind.Ethernet or NetworkKind.WiFi).ToList();
        f.Set("network.has_wifi", physicalNet.Any(n => n.Kind == NetworkKind.WiFi));
        f.Set("network.has_ethernet", physicalNet.Any(n => n.Kind == NetworkKind.Ethernet));
        f.Set("network.wifi_active", physicalNet.Any(n => n.Kind == NetworkKind.WiFi && n.Connected));
        f.Set("network.ethernet_active", physicalNet.Any(n => n.Kind == NetworkKind.Ethernet && n.Connected));
        f.Set("network.nagle_off", physicalNet.Any(n => n.NagleDisabled == true));

        var systemDrive = r.Storage.FirstOrDefault(s => s.IsSystemDrive);
        if (systemDrive is not null)
            f.Set("storage.system_ssd", systemDrive.Kind is StorageKind.Ssd or StorageKind.Nvme);

        // Логических ядер вдвое больше физических — значит, Hyper-Threading / SMT включён.
        if (r.Cpu.PhysicalCores > 0 && r.Cpu.LogicalCores > 0)
            f.Set("cpu.smt_enabled", r.Cpu.LogicalCores > r.Cpu.PhysicalCores);
        f.Set("cpu.virtualization", r.Cpu.VirtualizationEnabled);
        f.Set("print.physical_printers", Actions.Printers.PhysicalCount());

        f.Set("bios.vendor", r.Machine.BiosVendor);
        f.Set("bios.version", r.Machine.BiosVersion);
        if (r.Machine.BiosDate is { } biosDate)
        {
            f.Set("bios.age_days", (int)(DateTime.Now - biosDate).TotalDays);
            f.Set("bios.date", biosDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        }

        f.Set("ram.rated_speed", r.Memory.RatedSpeedMhz);
        // Нижняя («почти наверняка») граница находки «XMP выключен» из FindingsEngine:
        // частота не выше базовой JEDEC. Верхнюю ступень JEDEC сюда не берём —
        // на ней бывают честные комплекты без разгонного профиля.
        int jedecTop = r.Memory.Kind switch
        {
            MemoryKind.Ddr4 => 2400,
            MemoryKind.Ddr5 => 4800,
            MemoryKind.Ddr3 => 1333,
            _ => 0
        };
        if (jedecTop > 0 && r.Memory.ConfiguredSpeedMhz > 0)
            f.Set("ram.xmp_off_likely", r.Memory.ConfiguredSpeedMhz <= jedecTop);

        f.Set("timer.resolution_ms", r.Tuning.CurrentTimerResolutionMs ?? 0);
        f.Set("tuning.global_timer", r.Tuning.GlobalTimerResolutionRequests);

        return f;
    }

    private void Set(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _facts[key] = value;
    }

    private void Set(string key, int value) => _facts[key] = value.ToString(CultureInfo.InvariantCulture);
    private void Set(string key, double value) => _facts[key] = value.ToString("0.##", CultureInfo.InvariantCulture);
    private void Set(string key, bool value) => _facts[key] = value ? "true" : "false";
    private void Set(string key, bool? value) { if (value.HasValue) Set(key, value.Value); }

    public string? Get(string key) => _facts.GetValueOrDefault(key);

    /// <summary>
    /// Проверяет условие вида «gpu.vendor=nvidia», «os.build&gt;=19041», «cpu.is_x3d=true»,
    /// «gpu.vendor!=intel», «ram.total_gb&gt;16».
    ///
    /// Неизвестный факт означает «условие не выполнено»: лучше не применить нужный твик,
    /// чем применить твик на неподходящем железе.
    /// </summary>
    public bool Matches(string requirement)
    {
        foreach (var op in new[] { ">=", "<=", "!=", "=", ">", "<" })
        {
            var idx = requirement.IndexOf(op, StringComparison.Ordinal);
            if (idx <= 0) continue;

            var key = requirement[..idx].Trim();
            var expected = requirement[(idx + op.Length)..].Trim();
            var actual = Get(key);

            if (actual is null) return false;

            // Числовые сравнения — только если обе стороны разбираются как числа.
            bool actualIsNumber = double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a);
            bool expectedIsNumber = double.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var b);
            bool numeric = actualIsNumber && expectedIsNumber;

            return op switch
            {
                "=" => numeric ? Math.Abs(a - b) < 0.001 : actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
                "!=" => numeric ? Math.Abs(a - b) >= 0.001 : !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
                ">=" => numeric && a >= b,
                "<=" => numeric && a <= b,
                ">" => numeric && a > b,
                "<" => numeric && a < b,
                _ => false
            };
        }

        // Условие без оператора — проверка «факт существует и не false».
        var value = Get(requirement.Trim());
        return value is not null && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesAll(IEnumerable<string> requirements) => requirements.All(Matches);
}
