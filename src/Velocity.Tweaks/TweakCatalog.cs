using Velocity.Core;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velocity.Tweaks;

/// <summary>
/// Каталог твиков. Встроенные определения лежат в сборке, но каталог с диска
/// имеет приоритет — так обновление правил не требует переустановки программы.
/// </summary>
public sealed class TweakCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<TweakDefinition> _tweaks = [];

    public IReadOnlyList<TweakDefinition> All => _tweaks;
    public List<string> LoadErrors { get; } = [];

    public static TweakCatalog Load(string? overrideDirectory = null)
    {
        var catalog = new TweakCatalog();

        if (overrideDirectory is not null && Directory.Exists(overrideDirectory))
            catalog.LoadFromDirectory(overrideDirectory);
        else
            catalog.LoadEmbedded();

        return catalog;
    }

    private void LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Фильтр по папке обязателен: в сборке лежат ещё и профили игр,
        // и без него они попадали бы сюда и валились с ошибкой разбора.
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Catalog.") && n.EndsWith(".json")).Order())
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                AddRange(reader.ReadToEnd(), name);
            }
            catch (Exception ex) { LoadErrors.Add($"{name}: {ex.Message}"); }
        }
    }

    private void LoadFromDirectory(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            try { AddRange(File.ReadAllText(file), Path.GetFileName(file)); }
            catch (Exception ex) { LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
    }

    private void AddRange(string json, string source)
    {
        // Тексты для человека переводятся сразу при загрузке: дальше по всей программе
        // (поиск, диалоги, точки отката) идут уже переведённые названия.
        var parsed = JsonSerializer.Deserialize<List<TweakDefinition>>(L10n.TranslateJson(json), JsonOptions);
        if (parsed is null) return;

        foreach (var tweak in parsed)
        {
            // Дубликат идентификатора означает ошибку в каталоге, а не «последний побеждает»:
            // иначе твик молча подменится, и никто этого не заметит.
            if (_tweaks.Any(t => t.Id == tweak.Id))
            {
                LoadErrors.Add(L10n.F("{0}: повторяющийся идентификатор «{1}»", source, tweak.Id));
                continue;
            }
            _tweaks.Add(tweak);
        }
    }

    public TweakDefinition? ById(string id)
        => _tweaks.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public List<TweakDefinition> ByPreset(TweakPreset preset)
        => [.. _tweaks.Where(t => t.Presets.Contains(preset))];

    public List<TweakDefinition> ByCategory(TweakCategory category)
        => [.. _tweaks.Where(t => t.Category == category)];

    /// <summary>Твики, подходящие этой системе.</summary>
    public List<TweakDefinition> Applicable(SystemFacts facts)
        => [.. _tweaks.Where(t => facts.MatchesAll(t.Requires))];
}
