using Velocity.Core;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Velocity.Hardware.Cleanup;

public sealed record CleanupResult(string TargetId, long FreedBytes, int DeletedFiles, List<string> Errors)
{
    public double FreedGb => Math.Round(FreedBytes / 1024d / 1024d / 1024d, 2);
}

/// <summary>
/// Считает и удаляет мусор. Два правила, от которых нельзя отступать:
///  1. Удаляем только то, что перечислено в каталоге, и ничего сверх.
///  2. Сама папка категории остаётся на месте — удаляется только содержимое.
///     Снос папки целиком ломает программы, которые ждут её существования.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CleanupScanner
{
    /// <param name="driveRoot">
    /// Только этот диск («D:\»). Удаляются ровно те папки, что попали в находку,
    /// поэтому фильтр по диску защищает и от удаления на другом диске.
    /// </param>
    public List<CleanupFinding> Scan(IProgress<string>? progress = null, string? driveRoot = null)
    {
        var findings = new List<CleanupFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in CleanupCatalog.All)
        {
            progress?.Report(target.Title);
            var finding = new CleanupFinding { Target = target };

            foreach (var template in target.Paths)
            {
                var path = CleanupCatalog.Resolve(template);

                // Один и тот же путь не должен попасть в две категории и посчитаться дважды.
                if (!seen.Add(path)) continue;
                if (driveRoot is not null && !string.Equals(Path.GetPathRoot(path), driveRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Directory.Exists(path)) continue;

                try
                {
                    var (size, count) = Measure(path, target.MinAgeDays);
                    if (size == 0) continue;

                    finding.SizeBytes += size;
                    finding.FileCount += count;
                    finding.Locations.Add((path, size));
                }
                catch (Exception ex)
                {
                    finding.Errors.Add($"{path}: {ex.Message}");
                }
            }

            if (finding.SizeBytes > 0) findings.Add(finding);
        }

        return [.. findings.OrderByDescending(f => f.SizeBytes)];
    }

    private static (long Size, int Count) Measure(string root, int minAgeDays)
    {
        // Когда ограничения по возрасту нет, порог должен быть в БУДУЩЕМ.
        // С DateTime.MinValue условие «файл новее порога» верно для всех файлов,
        // и фильтр молча отбрасывал всё содержимое категории.
        var cutoff = minAgeDays > 0 ? DateTime.Now.AddDays(-minAgeDays) : DateTime.MaxValue;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        long size = 0;
        int count = 0;

        try
        {
            // Размер и дату берём из записи каталога — без отдельного обращения к файлу.
            var files = new FileSystemEnumerable<(long Length, DateTime Written)>(
                root,
                (ref FileSystemEntry entry) => (entry.Length, entry.LastWriteTimeUtc.LocalDateTime),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
            };

            foreach (var (length, written) in files)
            {
                // Свежие файлы могут быть нужны работающим сейчас программам.
                if (written > cutoff) continue;
                size += length;
                count++;
            }
        }
        catch { /* нет доступа к корню — считаем, что здесь ничего нет */ }

        return (size, count);
    }

    /// <summary>
    /// Удаляет содержимое категории. Возвращает, сколько реально освободилось:
    /// заявленный при сканировании размер и фактический результат часто расходятся,
    /// потому что часть файлов занята работающими программами.
    /// </summary>
    public CleanupResult Clean(CleanupFinding finding)
    {
        long freed = 0;
        int deleted = 0;
        var errors = new List<string>();
        var cutoff = finding.Target.MinAgeDays > 0
            ? DateTime.Now.AddDays(-finding.Target.MinAgeDays)
            : DateTime.MaxValue;

        foreach (var (path, _) in finding.Locations)
        {
            // Подстраховка: удаляем только по путям из каталога, заново их проверяя.
            if (!IsAllowed(path))
            {
                errors.Add(L10n.F("{0}: путь не входит в список разрешённых", path));
                continue;
            }

            var (f, d, e) = DeleteContents(path, cutoff);
            freed += f;
            deleted += d;
            errors.AddRange(e);
        }

        return new CleanupResult(finding.Target.Id, freed, deleted, errors);
    }

    /// <summary>
    /// Путь обязан в точности совпадать с одним из записанных в каталоге.
    /// Это последний барьер между ошибкой в данных и удалёнными файлами пользователя.
    /// </summary>
    private static bool IsAllowed(string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd('\\');

        foreach (var target in CleanupCatalog.All)
        foreach (var template in target.Paths)
        {
            var allowed = Path.GetFullPath(CleanupCatalog.Resolve(template)).TrimEnd('\\');
            if (normalized.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static (long Freed, int Deleted, List<string> Errors) DeleteContents(string root, DateTime cutoff)
    {
        long freed = 0;
        int deleted = 0;
        var errors = new List<string>();

        void DeleteIn(string dir, bool isRoot)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) { errors.Add($"{dir}: {ex.Message}"); return; }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime > cutoff) continue;

                    long length = info.Length;
                    // Атрибут «только чтение» снимаем, иначе удаление упадёт.
                    if (info.IsReadOnly) info.IsReadOnly = false;
                    info.Delete();

                    freed += length;
                    deleted++;
                }
                catch (IOException) { /* файл занят — это нормально, пропускаем */ }
                catch (UnauthorizedAccessException) { /* нет прав на конкретный файл */ }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (var sub in subdirs)
            {
                DeleteIn(sub, isRoot: false);

                // Пустые подпапки убираем, но корневую папку категории оставляем:
                // программы рассчитывают, что она существует.
                try
                {
                    if (Directory.GetFileSystemEntries(sub).Length == 0) Directory.Delete(sub);
                }
                catch { }
            }
        }

        DeleteIn(root, isRoot: true);
        return (freed, deleted, errors);
    }

    // ─────────────────────────── Корзина ───────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>Сколько занято в корзине, в байтах, и сколько там объектов.</summary>
    public static (long Size, long Items) QueryRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch { return (0, 0); }
    }

    /// <summary>Очищает корзину без звука, анимации и подтверждения Windows.</summary>
    public static bool EmptyRecycleBin()
    {
        try
        {
            const uint noConfirmation = 0x01, noProgressUi = 0x02, noSound = 0x04;
            return SHEmptyRecycleBin(IntPtr.Zero, null, noConfirmation | noProgressUi | noSound) == 0;
        }
        catch { return false; }
    }
}
