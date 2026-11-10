using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;
using Velocity.Core;

namespace Velocity.App;

/// <summary>
/// Перевод интерфейса по схеме gettext: ключ — сам русский текст.
///
/// Так разметка остаётся читаемой (видно, что написано на кнопке), а строка без перевода
/// просто показывается по-русски, а не ключом вида «settings.title». Переводы лежат
/// в Strings/{язык}.json (оболочка) и Strings/{язык}.content.json (каталог, руководства,
/// находки, профили игр) как пары «русский текст → перевод». Сам словарь живёт в ядре
/// (<see cref="L10n"/>), чтобы переводить могли и сканер, и каталог настроек.
/// </summary>
public static class Loc
{
    public static readonly (string Id, string Name)[] Languages = [("ru", "Русский"), ("en", "English")];

    public static string Language => L10n.Language;
    public static bool IsRussian => L10n.IsRussian;
    public static CultureInfo Culture => L10n.Culture;

    /// <summary>Встроенные файлы перевода для языка.</summary>
    public static string[] ResourceNames(string id) =>
        [$"Velocity.App.Strings.{id}.json", $"Velocity.App.Strings.{id}.content.json"];

    public static void Load(string? language)
    {
        var id = Languages.Any(l => l.Id == language) ? language! : "ru";
        if (id == "ru")
        {
            L10n.Set("ru", null);
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in ResourceNames(id))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
                if (parsed is null) continue;
                foreach (var (key, value) in parsed) map[key] = value;
            }
            catch { /* битый файл перевода — его строки останутся по-русски */ }
        }

        L10n.Set(id, map);
        // Числа и даты — в формате языка интерфейса, иначе в одной строке выйдет
        // «155.5 GB» из перевода и «33,2%» из системной культуры.
        CultureInfo.DefaultThreadCurrentUICulture = L10n.Culture;
        CultureInfo.CurrentUICulture = L10n.Culture;
        CultureInfo.DefaultThreadCurrentCulture = L10n.Culture;
        CultureInfo.CurrentCulture = L10n.Culture;
    }

    /// <summary>Перевод строки; без перевода — исходный русский текст.</summary>
    public static string T(string ru) => L10n.T(ru);

    /// <summary>Перевод шаблона с подстановками {0}, {1}…</summary>
    public static string F(string ruFormat, params object?[] args) => L10n.F(ruFormat, args);

    /// <summary>
    /// Число со словом: по-русски три формы (1 настройка, 2 настройки, 5 настроек),
    /// по-английски две (1 setting, 5 settings).
    /// </summary>
    public static string Plural(long n, string one, string few, string many) =>
        Velocity.Core.Plural.With((int)n, one, few, many);
}

/// <summary>Перевод в разметке: <c>Text="{app:T 'Применить'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
