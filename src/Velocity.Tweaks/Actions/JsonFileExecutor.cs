using Velocity.Core;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Velocity.Tweaks.Actions;

/// <summary>
/// Правка настроек программ, которые хранят их в JSON — Discord, часть лаунчеров.
///
/// Остальные ключи файла не трогаем: там лежат личные настройки пользователя,
/// и переписывать файл целиком «своей» версией значило бы их потерять.
/// </summary>
[SupportedOSPlatform("windows")]
public static class JsonFileExecutor
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Раскрывает подстановки в пути: {appdata}, {localappdata}, {documents}, {userprofile}.</summary>
    public static string ResolvePath(string template) => template
        .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        .Replace("{localappdata}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        .Replace("{documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        .Replace("{userprofile}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static StateRecord Capture(JsonFileAction action)
    {
        var path = ResolvePath(action.File);
        string? current = null;

        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
            {
                // Отсутствующий ключ и ключ со значением null — разные вещи:
                // при откате первый надо удалить, второй вернуть как null.
                current = root.TryGetPropertyValue(action.Property, out var node)
                    ? node?.ToJsonString() ?? "null"
                    : null;
            }
        }
        catch { /* файл битый или занят — считаем, что значения не было */ }

        return new StateRecord
        {
            Kind = "json",
            Target = $"{action.File}|{action.Property}",
            Value = current
        };
    }

    public static void Apply(JsonFileAction action)
    {
        var path = ResolvePath(action.File);

        if (!File.Exists(path))
            throw new FileNotFoundException(L10n.F("Файл настроек не найден: {0}", path));

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException(L10n.F("Файл {0} не является объектом JSON", path));

        root[action.Property] = Parse(action.Value, action.Kind);
        Save(path, root);
    }

    public static void Restore(StateRecord record)
    {
        var parts = record.Target.Split('|');
        if (parts.Length != 2) return;

        var path = ResolvePath(parts[0]);
        if (!File.Exists(path)) return;

        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return;

        if (record.Value is null) root.Remove(parts[1]);
        else root[parts[1]] = JsonNode.Parse(record.Value);

        Save(path, root);
    }

    public static bool Check(JsonFileAction action)
    {
        var expected = Parse(action.Value, action.Kind)?.ToJsonString();
        return Capture(action).Value == expected;
    }

    private static JsonNode? Parse(string value, string kind) => kind.ToLowerInvariant() switch
    {
        "bool" => JsonValue.Create(bool.Parse(value)),
        "number" => JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value)
    };

    private static void Save(string path, JsonObject root)
        => File.WriteAllText(path, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
}
