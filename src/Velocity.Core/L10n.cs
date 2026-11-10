using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Velocity.Core;

/// <summary>
/// Перевод текстов по схеме gettext: ключ — сам русский текст, значение — перевод.
///
/// Живёт в ядре, чтобы переводить могли все слои: находки, очистка, каталог настроек,
/// руководства. Словарь загружает приложение (<c>Velocity.App.Loc</c>) до первого скана;
/// без словаря всё остаётся по-русски — так работают консольная утилита и самопроверка.
/// Строка без перевода показывается по-русски, а не пустой и не ключом.
/// </summary>
public static partial class L10n
{
    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public static string Language { get; private set; } = "ru";
    public static bool IsRussian => Language == "ru";

    /// <summary>
    /// Культура для чисел и дат в переведённых строках. По-русски — системная,
    /// в переводе — культура языка, иначе вышло бы «Last check: ноября 6».
    /// </summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    public static void Set(string language, IReadOnlyDictionary<string, string>? map)
    {
        Language = language;
        _map = map is null ? new(StringComparer.Ordinal) : new(map, StringComparer.Ordinal);
        Culture = IsRussian ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(language);
    }

    /// <summary>Перевод строки; без перевода — исходный русский текст.</summary>
    public static string T(string ru) =>
        !IsRussian && _map.TryGetValue(ru, out var text) && !string.IsNullOrEmpty(text) ? text : ru;

    /// <summary>Перевод шаблона с подстановками {0}, {1}…</summary>
    public static string F(string ruFormat, params object?[] args)
    {
        try { return string.Format(Culture, T(ruFormat), args); }
        catch (FormatException) { return string.Format(Culture, ruFormat, args); }
    }

    /// <summary>Есть ли перевод у строки (для проверки полноты словаря).</summary>
    public static bool Has(string ru) => _map.ContainsKey(ru);

    [GeneratedRegex("[А-Яа-яЁё]")]
    public static partial Regex Cyrillic();

    /// <summary>
    /// Переводит все русские строки в JSON-документе (каталог, руководства, профили игр).
    /// Идентификаторы, пути реестра и значения настроек латинские и не меняются;
    /// русские в этих файлах только тексты для человека.
    /// </summary>
    public static string TranslateJson(string json)
    {
        if (IsRussian) return json;
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (root is null) return json;
        TranslateInPlace(root);
        return root.ToJsonString();
    }

    /// <summary>Все русские строки JSON-документа — для проверки полноты перевода.</summary>
    public static IEnumerable<string> RussianStrings(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var found = new List<string>();
        Collect(root, found);
        return found;
    }

    private static void TranslateInPlace(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (Translated(obj[key]) is { } text) obj[key] = text;
                else TranslateInPlace(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (Translated(array[i]) is { } text) array[i] = text;
                else TranslateInPlace(array[i]);
            }
        }
    }

    private static string? Translated(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && Cyrillic().IsMatch(text) ? T(text) : null;

    private static void Collect(JsonNode? node, List<string> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj) Collect(pair.Value, found);
                break;
            case JsonArray array:
                foreach (var item in array) Collect(item, found);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && Cyrillic().IsMatch(text):
                found.Add(text);
                break;
        }
    }
}
