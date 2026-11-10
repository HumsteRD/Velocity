using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Velocity.Tweaks;
using Velocity.Tweaks.Guides;

namespace Velocity.App.ViewModels;

/// <summary>Шаг руководства с отметкой «сделано».</summary>
public sealed class GuideStepItem : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required int Number { get; init; }
    public required string Text { get; init; }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        set { if (_isDone == value) return; _isDone = value; Notify(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Руководство вместе с его прогрессом и состоянием из скана.</summary>
public sealed class GuideItem : INotifyPropertyChanged
{
    public required Guide Guide { get; init; }
    public ObservableCollection<GuideStepItem> Steps { get; } = [];

    public string Title => Guide.Title;
    public string Summary => Guide.Summary;
    public string? Why => Guide.Why;
    public string? Gain => Guide.Gain;
    public string? Duration => Guide.Duration;
    public TweakRisk Risk => Guide.Risk;
    public List<string> Warnings => Guide.Warnings;
    public List<string> Verify => Guide.Verify;
    public List<string> Undo => Guide.Undo;
    public List<GuideLink> Tools => Guide.Tools;

    public string SectionName => SectionTitle(Guide.Section);

    public static string SectionTitle(GuideSection section) => section switch
    {
        GuideSection.Windows => "Windows",
        GuideSection.Bios => Loc.T("BIOS и железо"),
        GuideSection.Network => Loc.T("Сеть и роутер"),
        GuideSection.Diagnostics => Loc.T("Диагностика"),
        _ => Loc.T("Прочее")
    };

    public string RiskText => Guide.Risk switch
    {
        TweakRisk.Safe => Loc.T("Безопасно"),
        TweakRisk.Moderate => Loc.T("Нужна аккуратность"),
        TweakRisk.Advanced => Loc.T("Для опытных"),
        TweakRisk.Expert => Loc.T("С риском — только с проверкой стабильности"),
        _ => ""
    };

    // ─────────────── Прогресс ───────────────

    public int DoneCount => Steps.Count(s => s.IsDone);
    public bool IsComplete => Steps.Count > 0 && DoneCount == Steps.Count;

    public string ProgressText => Steps.Count == 0 ? ""
        : IsComplete ? Loc.T("готово")
        : $"{DoneCount}/{Steps.Count}";

    public string ProgressLong => Steps.Count == 0 ? ""
        : IsComplete ? Loc.T("Все шаги отмечены как выполненные")
        : Loc.F("Выполнено {0} из {1}", DoneCount, Steps.Count);

    public void RefreshProgress()
    {
        Notify(nameof(DoneCount));
        Notify(nameof(IsComplete));
        Notify(nameof(ProgressText));
        Notify(nameof(ProgressLong));
        Notify(nameof(ListDotBrush));
    }

    // ─────────────── Состояние из скана ───────────────

    /// <summary>«Secure Boot: включён» — то, что программа знает сама.</summary>
    public string? StatusText { get; private set; }
    public string? StatusVerdict { get; private set; }
    public Brush? StatusBrush { get; private set; }
    public bool HasStatus => StatusText is not null;

    /// <summary>Требует внимания: из скана видно, что это стоит сделать.</summary>
    public bool NeedsAttention { get; private set; }

    public void EvaluateStatus(SystemFacts? facts)
    {
        var status = Guide.Status;
        if (status is null || facts is null)
        {
            StatusText = null;
            NeedsAttention = false;
        }
        else
        {
            var raw = facts.Get(status.Fact);
            string value = raw is null ? Loc.T("не удалось определить")
                : status.Values.TryGetValue(raw.ToLowerInvariant(), out var mapped) ? mapped
                : raw + (status.Suffix ?? "");
            StatusText = $"{status.Label}: {value}";

            if (status.Good is null || raw is null)
            {
                StatusVerdict = raw is null ? null : Loc.T("решение за тобой — смотри шаги");
                StatusBrush = Brush("Info");
                NeedsAttention = false;
            }
            else if (facts.Matches(status.Good))
            {
                StatusVerdict = Loc.T("в порядке");
                StatusBrush = Brush("Success");
                NeedsAttention = false;
            }
            else
            {
                StatusVerdict = Loc.T("стоит заняться");
                StatusBrush = Brush("Warning");
                NeedsAttention = true;
            }
        }

        Notify(nameof(StatusText));
        Notify(nameof(StatusVerdict));
        Notify(nameof(StatusBrush));
        Notify(nameof(HasStatus));
        Notify(nameof(NeedsAttention));
        Notify(nameof(ListDotBrush));
    }

    /// <summary>Точка в списке: зелёная — всё сделано или уже в порядке, жёлтая — стоит заняться.</summary>
    public Brush ListDotBrush =>
        IsComplete ? Brush("Success")
        : NeedsAttention ? Brush("Warning")
        : DoneCount > 0 ? Brush("Accent")
        : Brush("BorderStrong");

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? Brushes.Gray;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
