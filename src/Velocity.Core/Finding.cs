namespace Velocity.Core;

/// <summary>
/// Находка диагностики — то, что показывается пользователю в бесплатном режиме.
/// Это главный продающий артефакт продукта (см. docs/01-PROJECT-PLAN.md, раздел «воронка»).
/// </summary>
public sealed class Finding
{
    public required string Id { get; init; }
    public required FindingCategory Category { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Title { get; init; }
    /// <summary>Человеческим языком: что не так и почему это важно. Без маркетинга.</summary>
    public required string Detail { get; init; }
    /// <summary>Что конкретно делать. null, если исправление не автоматизируемо (например, BIOS).</summary>
    public string? Recommendation { get; init; }
    /// <summary>Id твика, который это чинит. null — только информирование (BIOS, замена кабеля).</summary>
    public string? FixTweakId { get; init; }
    /// <summary>Можно ли починить из приложения, или нужны руки пользователя.</summary>
    public bool AutoFixable => FixTweakId is not null;
    /// <summary>Ожидаемый эффект. Честная формулировка, без «+300% FPS».</summary>
    public string? ExpectedGain { get; init; }
    /// <summary>Вес для расчёта Score.</summary>
    public int ScoreWeight { get; init; }
}

public enum FindingCategory
{
    Display,
    HardwareBios,
    Gpu,
    Windows,
    Power,
    Input,
    Storage,
    Memory,
    Network,
    Background,
    Security
}

public enum FindingSeverity
{
    /// <summary>Просто к сведению, на производительность не влияет.</summary>
    Info,
    /// <summary>Стоит поправить, эффект заметный.</summary>
    Warning,
    /// <summary>Явно ломает производительность прямо сейчас.</summary>
    Critical
}

public static class Scoring
{
    /// <summary>
    /// Score = 100 − Σ (вес × множитель серьёзности), с полом в 0.
    /// Формула намеренно простая и прозрачная — в UI пользователь может её раскрыть.
    /// </summary>
    public static int Compute(IEnumerable<Finding> findings)
    {
        double penalty = 0;
        foreach (var f in findings)
        {
            double multiplier = f.Severity switch
            {
                FindingSeverity.Critical => 1.0,
                FindingSeverity.Warning => 0.5,
                _ => 0.0
            };
            penalty += f.ScoreWeight * multiplier;
        }
        return Math.Clamp((int)Math.Round(100 - penalty), 0, 100);
    }

    public static string Label(int score) => score switch
    {
        >= 90 => L10n.T("Отличный"),
        >= 75 => L10n.T("Хороший"),
        >= 55 => L10n.T("Средний"),
        >= 35 => L10n.T("Слабый"),
        _ => L10n.T("Требует внимания")
    };
}
