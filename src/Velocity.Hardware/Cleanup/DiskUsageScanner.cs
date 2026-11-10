using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.Versioning;
using Velocity.Core;

namespace Velocity.Hardware.Cleanup;

/// <summary>
/// Считает, чем занято место на диске.
///
/// Обход идёт по верхнему уровню: пользователю нужна картина «папка X весит 35 ГБ»,
/// а не дерево из миллиона файлов. Каждая папка верхнего уровня считается в своём
/// потоке — обход упирается в диск, а не в процессор, и параллельность даёт много.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskUsageScanner
{
    /// <summary>Мелочь в диаграмме не нужна — она превращает её в кашу.</summary>
    private const long MinimumInterestingBytes = 200L * 1024 * 1024;

    public DiskUsageReport Scan(
        string driveLetter,
        IEnumerable<GameInfo>? games = null,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var root = driveLetter.TrimEnd('\\', ':') + ":\\";
        var drive = new DriveInfo(root);

        var report = new DiskUsageReport
        {
            Drive = root,
            TotalBytes = drive.IsReady ? drive.TotalSize : 0,
            FreeBytes = drive.IsReady ? drive.AvailableFreeSpace : 0
        };

        string[] topLevel;
        try { topLevel = Directory.GetDirectories(root); }
        catch { return report; }

        var gamePaths = games?
            .Where(g => g.InstallPath is not null)
            .Select(g => g.InstallPath!)
            .ToList() ?? [];

        var results = new ConcurrentBag<DiskUsageEntry>();

        Parallel.ForEach(topLevel, new ParallelOptions
        {
            CancellationToken = cancel,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, dir =>
        {
            if (cancel.IsCancellationRequested) return;

            var name = Path.GetFileName(dir);
            progress?.Report(name);

            long size = MeasureDirectory(dir, cancel);
            if (size < MinimumInterestingBytes) return;

            results.Add(new DiskUsageEntry
            {
                Name = name,
                Path = dir,
                SizeBytes = size,
                IsGames = gamePaths.Any(p => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            });
        });

        // Файлы в корне диска тоже занимают место — например, файл подкачки
        // и файл гибернации, а это десятки гигабайт.
        long rootFiles = 0;
        try
        {
            foreach (var file in Directory.GetFiles(root))
            {
                try { rootFiles += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }

        if (rootFiles >= MinimumInterestingBytes)
        {
            results.Add(new DiskUsageEntry
            {
                Name = L10n.T("Файлы в корне диска"),
                Path = root,
                SizeBytes = rootFiles
            });
        }

        var entries = results.OrderByDescending(e => e.SizeBytes).ToList();
        long measured = entries.Sum(e => e.SizeBytes);

        // Разница между занятым местом и суммой того, что удалось измерить.
        // Это защищённые системные папки, теневые копии и мелочь ниже порога.
        report.UnaccountedBytes = Math.Max(0, report.UsedBytes - measured);

        foreach (var entry in entries)
            entry.Percent = report.UsedBytes > 0 ? entry.SizeBytes * 100.0 / report.UsedBytes : 0;

        report.Entries.AddRange(entries);
        return report;
    }

    /// <summary>
    /// Считает размер каталога со всеми вложенными.
    ///
    /// Раньше здесь был обход с «new FileInfo(path).Length» на каждый файл — а это
    /// отдельное обращение к файловой системе на КАЖДЫЙ из сотен тысяч файлов.
    /// FileSystemEnumerable берёт размер прямо из записи каталога, которую система
    /// и так вернула при перечислении: обращений в разы меньше.
    /// </summary>
    private static long MeasureDirectory(string root, CancellationToken cancel)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Точки соединения ведут в другое место файловой системы: пройдя по ним,
            // обход посчитает одни и те же данные дважды и может зациклиться.
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            var files = new FileSystemEnumerable<long>(
                root,
                (ref FileSystemEntry entry) => entry.Length,
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
            };

            long total = 0;
            foreach (var length in files)
            {
                if (cancel.IsCancellationRequested) break;
                total += length;
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Диски, по которым есть смысл строить картину занятости.</summary>
    public static List<string> AvailableDrives()
    {
        try
        {
            return [.. DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.Name)];
        }
        catch { return []; }
    }
}
