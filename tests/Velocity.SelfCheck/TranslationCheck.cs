using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velocity.Core;
using Velocity.Tweaks;
using Velocity.Tweaks.Games;
using Velocity.Tweaks.Guides;

/// <summary>
/// Полнота английского перевода. Ключ перевода — русский текст, поэтому любая правка
/// русской строки без правки перевода тихо возвращает её по-русски. Проверка ловит это:
/// собирает все переводимые строки из исходников и разметки, затем загружает каталог,
/// руководства и профили игр на английском и ищет в них кириллицу.
/// </summary>
static partial class TranslationCheck
{
    public static List<(string What, bool Ok, string? Detail)> Run()
    {
        var results = new List<(string, bool, string?)>();
        var src = FindSources();
        if (src is null)
        {
            results.Add(("исходники не найдены — проверка перевода пропущена", true, null));
            return results;
        }

        var map = Load(Path.Combine(src, "Velocity.App", "Strings", "en.json"));
        foreach (var (key, value) in Load(Path.Combine(src, "Velocity.App", "Strings", "en.content.json")))
            map[key] = value;
        results.Add(($"словарь загружен: {map.Count} строк", map.Count > 1500, null));

        var empty = map.Where(p => string.IsNullOrWhiteSpace(p.Value) || Cyr().IsMatch(p.Value)).Select(p => p.Key).ToList();
        results.Add(("в переводах нет пустых и русских значений", empty.Count == 0, Sample(empty)));

        // 1. Строки кода и разметки, которые проходят через перевод.
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var untranslatedXaml = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || file.Contains("Velocity.Scan.Cli")) continue;
            if (file.EndsWith(".cs"))
            {
                // Строки без кириллицы («Resizable BAR», «{0} · {1}») переводить не нужно.
                foreach (var key in CodeKeys(File.ReadAllText(file), file))
                    if (Cyr().IsMatch(key) && !map.ContainsKey(key)) missing.Add(key);
            }
            else if (file.EndsWith(".xaml"))
            {
                var xaml = File.ReadAllText(file);
                foreach (Match m in XamlKey().Matches(xaml))
                {
                    var key = WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\\'", "'"));
                    if (Cyr().IsMatch(key) && !map.ContainsKey(key)) missing.Add(key);
                }
                // Русский текст в разметке мимо {app:T} — забытая строка.
                var stripped = XamlKey().Replace(XmlComment().Replace(xaml, ""), "");
                if (Cyr().IsMatch(stripped)) untranslatedXaml.Add(Path.GetFileName(file));
            }
        }
        results.Add(("все строки интерфейса и кода есть в переводе", missing.Count == 0, Sample(missing)));
        results.Add(("в разметке нет русского текста мимо перевода", untranslatedXaml.Count == 0,
            string.Join(", ", untranslatedXaml)));

        // 2. Каталог, руководства и профили игр, загруженные на английском.
        L10n.Set("en", map);
        try
        {
            var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var catalog = TweakCatalog.Load();
            var guides = GuideCatalog.Load(catalog);
            var games = GameProfileEngine.Load();

            List<string> Russian<T>(IEnumerable<T> items, Func<T, string> name) => items
                .Where(item => Cyr().IsMatch(JsonSerializer.Serialize(item, options)))
                .Select(name).ToList();

            var tweaks = Russian(catalog.All, t => t.Id);
            results.Add(($"каталог настроек на английском ({catalog.All.Count})", tweaks.Count == 0 && catalog.LoadErrors.Count == 0,
                tweaks.Count > 0 ? Sample(tweaks) : string.Join("; ", catalog.LoadErrors)));
            var guideIds = Russian(guides.All, g => g.Id);
            results.Add(($"руководства на английском ({guides.All.Count})", guideIds.Count == 0, Sample(guideIds)));
            var profiles = Russian(games.Profiles, p => p.Id);
            results.Add(($"профили игр на английском ({games.Profiles.Count})", profiles.Count == 0 && games.LoadErrors.Count == 0,
                profiles.Count > 0 ? Sample(profiles) : string.Join("; ", games.LoadErrors)));
        }
        finally
        {
            L10n.Set("ru", null);
        }
        return results;
    }

    private static IEnumerable<string> CodeKeys(string code, string file)
    {
        foreach (Match m in CodeKey().Matches(code))
            yield return Unescape(m.Groups[1].Value);
        // Числа со словами: в переводе нужны формы «одна» и «много».
        foreach (Match m in PluralCall().Matches(code))
        {
            yield return Unescape(m.Groups[1].Value);
            yield return Unescape(m.Groups[3].Value);
        }
        // Тексты замеров переводятся при выводе (BenchmarkView, NetworkView).
        if (file.Contains(@"\Bench\"))
        {
            foreach (Match m in BenchField().Matches(code))
                if (Cyr().IsMatch(m.Groups[1].Value)) yield return Unescape(m.Groups[1].Value);
        }
    }

    private static string Unescape(string literal) => Regex.Unescape(literal);

    private static Dictionary<string, string> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];

    private static string? FindSources()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "Velocity.App", "Strings"))) return candidate;
        }
        return null;
    }

    private static string? Sample(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? null : $"{list.Count}: " + string.Join(" | ", list.Take(5).Select(s => s.Length > 60 ? s[..60] + "…" : s));
    }

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex Cyr();

    [GeneratedRegex(@"(?:L10n|Loc)\.[TF]\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"(?:Plural|With)\([^,()]+(?:\([^()]*\))?[^,()]*,\s*""((?:[^""\\]|\\.)*)""\s*,\s*""((?:[^""\\]|\\.)*)""\s*,\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex PluralCall();

    [GeneratedRegex(@"(?:(?:Title|Group|Unit|Hint)\s*=|const string Group\w*\s*=|new\()\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex BenchField();

    [GeneratedRegex(@"\{app:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
}
