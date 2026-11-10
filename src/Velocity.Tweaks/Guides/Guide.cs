using Velocity.Core;
using System.Reflection;
using System.Text.Json;

namespace Velocity.Tweaks.Guides;

/// <summary>
/// Пошаговое руководство для того, что программа сделать не может физически:
/// настройки BIOS, установка Windows, роутер, проверка сторонними утилитами.
///
/// Это данные, как и твики: тексты правятся без пересборки логики.
/// </summary>
public sealed class Guide
{
    public required string Id { get; init; }
    public required GuideSection Section { get; init; }
    public required string Title { get; init; }
    /// <summary>Одна строка под заголовком в списке.</summary>
    public required string Summary { get; init; }

    /// <summary>Зачем это делать — честно, с ценой.</summary>
    public string? Why { get; init; }
    /// <summary>Чего ждать в цифрах или ощущениях.</summary>
    public string? Gain { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    /// <summary>Сколько времени займёт: «15 минут», «вечер».</summary>
    public string? Duration { get; init; }

    /// <summary>Условия показа — те же, что у твиков: «cpu.vendor=amd».</summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>Что программа знает о текущем состоянии из скана.</summary>
    public GuideStatus? Status { get; init; }

    public List<string> Warnings { get; init; } = [];
    public List<string> Steps { get; init; } = [];
    /// <summary>Как убедиться, что получилось.</summary>
    public List<string> Verify { get; init; } = [];
    /// <summary>Как вернуть как было.</summary>
    public List<string> Undo { get; init; } = [];
    public List<GuideLink> Tools { get; init; } = [];

    /// <summary>Руководство собрано из ручного твика каталога, а не из файла руководств.</summary>
    public string? SourceTweakId { get; init; }
}

public enum GuideSection { Windows, Bios, Network, Diagnostics }

/// <summary>
/// Строка «Сейчас: …» в карточке руководства. Значение берётся из фактов скана,
/// а условие <see cref="Good"/> решает, подсвечивать ли его как «в порядке».
/// </summary>
public sealed class GuideStatus
{
    public required string Label { get; init; }
    public required string Fact { get; init; }
    /// <summary>Условие «уже хорошо». null — однозначно правильного значения нет.</summary>
    public string? Good { get; init; }
    /// <summary>Перевод сырых значений: true → «включён».</summary>
    public Dictionary<string, string> Values { get; init; } = [];
    public string? Suffix { get; init; }
}

public sealed class GuideLink
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Note { get; init; }
}

public sealed class GuideCatalog
{
    private readonly List<Guide> _guides = [];

    public IReadOnlyList<Guide> All => _guides;
    public List<string> LoadErrors { get; } = [];

    /// <summary>
    /// Загружает руководства и добавляет к ним ручные твики каталога
    /// (XMP, Resizable BAR, слоты памяти): у них уже есть шаги, дублировать тексты незачем.
    /// </summary>
    public static GuideCatalog Load(TweakCatalog? tweaks = null)
    {
        var catalog = new GuideCatalog();
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Guides.") && n.EndsWith(".json")).Order())
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var parsed = JsonSerializer.Deserialize<List<Guide>>(L10n.TranslateJson(reader.ReadToEnd()), TweakCatalog.JsonOptions);
                foreach (var guide in parsed ?? [])
                {
                    if (catalog._guides.Any(g => g.Id == guide.Id))
                    {
                        catalog.LoadErrors.Add(L10n.F("{0}: повторяющийся идентификатор «{1}»", name, guide.Id));
                        continue;
                    }
                    catalog._guides.Add(guide);
                }
            }
            catch (Exception ex) { catalog.LoadErrors.Add($"{name}: {ex.Message}"); }
        }

        if (tweaks is not null)
        {
            foreach (var tweak in tweaks.All.Where(t => t.IsManual))
                catalog._guides.Add(FromTweak(tweak));
        }

        return catalog;
    }

    private static Guide FromTweak(TweakDefinition tweak) => new()
    {
        Id = "tweak:" + tweak.Id,
        SourceTweakId = tweak.Id,
        Section = tweak.Category switch
        {
            TweakCategory.Memory or TweakCategory.Gpu or TweakCategory.Cpu or TweakCategory.Display => GuideSection.Bios,
            TweakCategory.Network => GuideSection.Network,
            _ => GuideSection.Windows
        },
        Title = tweak.Title,
        Summary = tweak.ExpectedGain ?? tweak.Effect ?? "",
        Why = tweak.Description,
        Gain = tweak.ExpectedGain,
        Risk = tweak.Risk,
        Requires = tweak.Requires,
        Steps = tweak.ManualSteps,
        Status = tweak.Id switch
        {
            "manual.xmp_expo" => new GuideStatus
            {
                Label = L10n.T("Частота памяти"),
                Fact = "ram.speed",
                Good = "ram.xmp_off_likely=false",
                Suffix = L10n.T(" МГц")
            },
            "manual.memory_channels" => new GuideStatus
            {
                Label = L10n.T("Каналов памяти"),
                Fact = "ram.channels",
                Good = "ram.channels>=2"
            },
            _ => null
        }
    };

    public List<Guide> Applicable(SystemFacts? facts)
        => [.. _guides.Where(g => facts is null || facts.MatchesAll(g.Requires))];
}
