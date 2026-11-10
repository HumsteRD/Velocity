using System.IO;
using System.Text.Json;

namespace Velocity.App;

/// <summary>
/// Локальные отметки пользователя «сделано» — пункты подготовки игр и шаги руководств.
/// Это не системные настройки и не часть снимка отката.
/// </summary>
public sealed class GameChecklistStore
{
    private readonly string _path;
    private readonly HashSet<string> _completed;

    public GameChecklistStore(string fileName = "game-checklist.json")
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity");
        _path = Path.Combine(folder, fileName);
        _completed = Load(_path);
    }

    public bool IsComplete(string key) => _completed.Contains(key);
    public void SetComplete(string key, bool complete)
    {
        if (complete) _completed.Add(key); else _completed.Remove(key);
        Save();
    }

    /// <summary>
    /// Отметка без учёта хвоста «:название». Раньше в ключ пункта входило его название,
    /// а оно меняется вместе с языком интерфейса — по такому ключу отметки терялись бы.
    /// </summary>
    public bool IsCompleteAny(string key) =>
        _completed.Contains(key) || _completed.Any(k => k.StartsWith(key + ":", StringComparison.Ordinal));

    public void SetCompleteAny(string key, bool complete)
    {
        _completed.RemoveWhere(k => k.StartsWith(key + ":", StringComparison.Ordinal));
        SetComplete(key, complete);
    }

    private static HashSet<string> Load(string path)
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []; }
        catch { return []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_completed.OrderBy(x => x)));
        }
        catch { }
    }
}
