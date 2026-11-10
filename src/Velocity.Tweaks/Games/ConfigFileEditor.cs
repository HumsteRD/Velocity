using Velocity.Core;
using System.Runtime.Versioning;
using System.Text;

namespace Velocity.Tweaks.Games;

/// <summary>
/// Правка игровых конфигов. Три правила, от которых нельзя отступать:
///  1. Оригинал файла сохраняется рядом ДО первой правки.
///  2. Неизвестные строки не трогаем — в конфиге игрока может быть его личная настройка.
///  3. Кодировка и переносы строк сохраняются: часть движков ломается от смены BOM.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConfigFileEditor
{
    private const string BackupSuffix = ".velocity-backup";

    public sealed record EditResult(string File, int Changed, bool Created, string? Error = null);

    public static EditResult Apply(ConfigTweak tweak, string resolvedPath)
    {
        try
        {
            bool exists = File.Exists(resolvedPath);
            if (!exists && !tweak.CreateIfMissing)
                return new EditResult(resolvedPath, 0, false, L10n.T("файл не найден"));

            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Бэкап делаем один раз: повторное применение не должно затирать
            // исходный файл уже изменённой версией.
            var backup = resolvedPath + BackupSuffix;
            if (exists && !File.Exists(backup)) File.Copy(resolvedPath, backup);

            var original = exists ? File.ReadAllText(resolvedPath) : "";
            var updated = tweak.Format switch
            {
                ConfigFormat.Ini => EditIni(original, tweak.Section, tweak.Entries),
                ConfigFormat.SourceCfg => EditSourceCfg(original, tweak.Entries),
                ConfigFormat.KeyValue => EditKeyValue(original, tweak.Entries),
                _ => original
            };

            if (updated == original) return new EditResult(resolvedPath, 0, !exists);

            File.WriteAllText(resolvedPath, updated, new UTF8Encoding(false));
            return new EditResult(resolvedPath, tweak.Entries.Count, !exists);
        }
        catch (Exception ex)
        {
            return new EditResult(resolvedPath, 0, false, ex.Message);
        }
    }

    /// <summary>Возвращает файл к сохранённому оригиналу.</summary>
    public static bool Restore(string path)
    {
        var backup = path + BackupSuffix;
        if (!File.Exists(backup)) return false;

        File.Copy(backup, path, overwrite: true);
        File.Delete(backup);
        return true;
    }

    public static bool HasBackup(string path) => File.Exists(path + BackupSuffix);

    // ─────────────────────────── INI ───────────────────────────

    private static string EditIni(string content, string? section, Dictionary<string, string> entries)
    {
        var newline = DetectNewline(content);
        var lines = content.Length == 0 ? [] : content.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var pending = new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase);

        int sectionStart = -1, sectionEnd = lines.Count;
        if (section is not null)
        {
            var header = $"[{section}]";
            sectionStart = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));

            if (sectionStart < 0)
            {
                // Секции нет — дописываем её целиком в конец.
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add(header);
                foreach (var (k, v) in pending) lines.Add($"{k}={v}");
                return string.Join(newline, lines);
            }

            sectionEnd = lines.FindIndex(sectionStart + 1, l => l.TrimStart().StartsWith('['));
            if (sectionEnd < 0) sectionEnd = lines.Count;
        }
        else
        {
            sectionStart = -1;
        }

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;

            var eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;

            var key = lines[i][..eq].Trim();
            if (!pending.TryGetValue(key, out var value)) continue;

            lines[i] = $"{key}={value}";
            pending.Remove(key);
        }

        // Недостающие ключи вставляем в конец нужной секции, а не в конец файла.
        if (pending.Count > 0)
        {
            var insertAt = sectionEnd;
            foreach (var (k, v) in pending) lines.Insert(insertAt++, $"{k}={v}");
        }

        return string.Join(newline, lines);
    }

    // ─────────────────────── Конфиг Source ───────────────────────

    private static string EditSourceCfg(string content, Dictionary<string, string> entries)
    {
        var newline = DetectNewline(content);
        var lines = content.Length == 0 ? [] : content.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var pending = new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//")) continue;

            var parts = trimmed.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (!pending.TryGetValue(parts[0], out var value)) continue;

            lines[i] = $"{parts[0]} {value}";
            pending.Remove(parts[0]);
        }

        if (pending.Count > 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("// Добавлено VELOCITY");
            foreach (var (k, v) in pending) lines.Add($"{k} {v}");
        }

        return string.Join(newline, lines);
    }

    private static string EditKeyValue(string content, Dictionary<string, string> entries)
        => EditIni(content, null, entries);

    /// <summary>Сохраняем тот перенос строки, который уже в файле.</summary>
    private static string DetectNewline(string content)
        => content.Contains("\r\n") ? "\r\n" : content.Contains('\n') ? "\n" : Environment.NewLine;
}
