using Velocity.Core;
namespace Velocity.Hardware.Cleanup;

/// <summary>Насколько безопасно удалять эту категорию.</summary>
public enum CleanupSafety
{
    /// <summary>Заведомо мусор: распакованные установщики, временные файлы, кэши загрузок.</summary>
    Safe,
    /// <summary>Пересоздаётся при следующем запуске, но первый раз будет медленнее — кэши шейдеров.</summary>
    Regenerates,
    /// <summary>Удаление лишает возможности откатиться: старая копия Windows, установщики драйверов.</summary>
    PointOfNoReturn
}

public enum CleanupKind
{
    Temp,
    WindowsUpdate,
    Drivers,
    ShaderCache,
    Logs,
    RecycleBin,
    Launchers,
    CrashDumps,
    Browsers,
    Apps
}

/// <summary>
/// Категория очистки. Пути перечислены явным списком — никаких масок вида
/// «удалить всё в папке X»: ошибка в такой маске стоит пользователю его файлов.
/// </summary>
public sealed class CleanupTarget
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required CleanupKind Kind { get; init; }
    public required CleanupSafety Safety { get; init; }

    /// <summary>Что произойдёт после удаления. Пользователь должен это знать заранее.</summary>
    public string? Consequence { get; init; }

    /// <summary>Пути с подстановками. Удаляется содержимое, сама папка остаётся.</summary>
    public List<string> Paths { get; init; } = [];

    /// <summary>Нужны права администратора (системные папки).</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Файлы новее указанного возраста не трогаем — они могут быть ещё нужны.</summary>
    public int MinAgeDays { get; init; }
}

/// <summary>Результат сканирования одной категории.</summary>
public sealed class CleanupFinding
{
    public required CleanupTarget Target { get; init; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    /// <summary>Реально существующие пути с их размерами.</summary>
    public List<(string Path, long Size)> Locations { get; init; } = [];
    public List<string> Errors { get; init; } = [];

    public double SizeGb => Math.Round(SizeBytes / 1024d / 1024d / 1024d, 2);
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);

    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => L10n.F("{0} ГБ", SizeGb),
        >= 1024L * 1024 => L10n.F("{0} МБ", SizeMb),
        >= 1024 => L10n.F("{0} КБ", SizeBytes / 1024),
        _ => L10n.F("{0} Б", SizeBytes)
    };
}

/// <summary>Крупный каталог на диске — для картины «чем занято место».</summary>
public sealed class DiskUsageEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; set; }
    public double SizeGb => Math.Round(SizeBytes / 1024d / 1024d / 1024d, 2);
    /// <summary>Доля от занятого места на диске, в процентах.</summary>
    public double Percent { get; set; }
    public bool IsGames { get; set; }
}

public sealed class DiskUsageReport
{
    public required string Drive { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public List<DiskUsageEntry> Entries { get; init; } = [];
    /// <summary>Место, которое не удалось отнести ни к одной папке (нет доступа).</summary>
    public long UnaccountedBytes { get; set; }

    public double TotalGb => Math.Round(TotalBytes / 1024d / 1024d / 1024d, 1);
    public double FreeGb => Math.Round(FreeBytes / 1024d / 1024d / 1024d, 1);
    public double UsedGb => Math.Round(UsedBytes / 1024d / 1024d / 1024d, 1);
    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
}
