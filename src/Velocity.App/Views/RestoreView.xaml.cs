using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Velocity.Core;
using Velocity.Tweaks;

namespace Velocity.App.Views;

public sealed class SnapshotCard
{
    public required string Id { get; init; }
    public required string When { get; init; }
    public required string Reason { get; init; }
    public required string Summary { get; init; }
    public required bool IsReverted { get; init; }
    public bool CanRevert => !IsReverted;
}

[SupportedOSPlatform("windows")]
public partial class RestoreView : UserControl
{
    private readonly SnapshotStore _store = new();

    public RestoreView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var snapshots = _store.List();

        var cards = snapshots.Select(s => new SnapshotCard
        {
            Id = s.Id,
            When = s.CreatedAt.ToString("d MMMM yyyy, HH:mm"),
            Reason = s.Reason,
            IsReverted = s.Reverted,
            Summary = Summarize(s)
        }).ToList();

        SnapshotList.ItemsSource = cards;
        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var active = cards.Count(c => c.CanRevert);
        SubtitleText.Text = cards.Count == 0
            ? Loc.T("История изменений пуста")
            : Loc.F("{0} · доступно для отката: {1}", Loc.Plural(cards.Count, Loc.T("точка"), Loc.T("точки"), Loc.T("точек")), active);
    }

    private static string Summarize(Snapshot snapshot)
    {
        if (snapshot.Changes.Count == 0) return Loc.T("изменений не записано");

        var titles = snapshot.Changes.Select(c => c.TweakTitle).Take(3).ToList();
        var text = string.Join(", ", titles);

        if (snapshot.Changes.Count > titles.Count)
            text += Loc.F(" и ещё {0}", snapshot.Changes.Count - titles.Count);

        var values = snapshot.Changes.Sum(c => c.Previous.Count);
        return Loc.F("{0}  ·  сохранено значений: {1}", text, values);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var snapshot = _store.Load(id);
        if (snapshot is null) return;

        var answer = MessageBox.Show(
            Loc.F("Вернуть систему к состоянию до «{0}»?\n\n", snapshot.Reason) +
            Loc.F("Будет отменено изменений: {0}.\n", snapshot.Changes.Count) +
            Loc.T("Часть настроек потребует перезагрузки, чтобы откат вступил в силу."),
            Loc.T("Подтверждение отката"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var state = AppState.Current;
        // Откат должен работать даже до первого скана — создаём движок на пустых фактах.
        var engine = state.Engine ?? new TweakEngine(SystemFacts.From(new HardwareReport()), _store);

        var results = await Task.Run(() => engine.Revert(id));

        var failed = results.Where(r => r.Outcome == ApplyOutcome.Failed).ToList();
        var text = Loc.F("Откачено: {0}", results.Count(r => r.Outcome == ApplyOutcome.Applied));
        if (failed.Count > 0)
            text += Loc.F("\n\nНе удалось ({0}):\n", failed.Count) +
                    string.Join("\n", failed.Select(f => $"  • {f.Title} — {f.Message}"));

        MessageBox.Show(text, Loc.T("Откат завершён"), MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        Reload();
        state.RefreshFindings();
    }
}
