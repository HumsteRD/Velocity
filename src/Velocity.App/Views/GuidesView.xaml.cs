using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Velocity.App.ViewModels;
using Velocity.Tweaks.Guides;

namespace Velocity.App.Views;

[SupportedOSPlatform("windows")]
public partial class GuidesView : UserControl
{
    private readonly ObservableCollection<GuideItem> _items = [];
    private readonly GameChecklistStore _progress = new("guide-checklist.json");
    private readonly ListCollectionView _view;
    private GuideSection? _section;
    private bool _ready;

    public GuidesView()
    {
        InitializeComponent();

        _view = new ListCollectionView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GuideItem.SectionName)));
        _view.Filter = o => _section is null || ((GuideItem)o).Guide.Section == _section;
        GuideList.ItemsSource = _view;

        Loaded += (_, _) => AppState.Current.ReportChanged += Reload;
        Unloaded += (_, _) => AppState.Current.ReportChanged -= Reload;

        _ready = true;
        Reload();
    }

    /// <summary>Открыть конкретное руководство — переход из других разделов.</summary>
    public void Select(string guideId)
    {
        var item = _items.FirstOrDefault(i => i.Guide.Id == guideId);
        if (item is null) return;
        if (_section is not null && item.Guide.Section != _section)
        {
            _section = null;
            _view.Refresh();
        }
        GuideList.SelectedItem = item;
        GuideList.ScrollIntoView(item);
    }

    private void Reload()
    {
        if (!_ready) return;
        Dispatcher.Invoke(() =>
        {
            var state = AppState.Current;
            var selectedId = (GuideList.SelectedItem as GuideItem)?.Guide.Id;

            _items.Clear();
            var order = new[] { GuideSection.Windows, GuideSection.Bios, GuideSection.Network, GuideSection.Diagnostics };
            foreach (var guide in state.Guides.Applicable(state.Facts)
                         .OrderBy(g => Array.IndexOf(order, g.Section))
                         .ThenBy(g => g.SourceTweakId is null ? 0 : 1))
            {
                var item = new GuideItem { Guide = guide };
                for (int i = 0; i < guide.Steps.Count; i++)
                {
                    var key = $"{guide.Id}#{i}";
                    item.Steps.Add(new GuideStepItem
                    {
                        Key = key,
                        Number = i + 1,
                        Text = guide.Steps[i],
                        IsDone = _progress.IsComplete(key)
                    });
                }
                item.EvaluateStatus(state.Facts);
                _items.Add(item);
            }

            var attention = _items.Count(i => i.NeedsAttention && !i.IsComplete);
            SubtitleText.Text =
                Loc.T("То, что программа не может сделать сама: BIOS, установка Windows, роутер, проверка сторонними утилитами. ") +
                Loc.T("Отметки шагов сохраняются.") +
                (attention > 0 ? Loc.F(" По данным скана стоит заняться: {0}.", attention) : "");

            var restore = _items.FirstOrDefault(i => i.Guide.Id == selectedId)
                          ?? _items.FirstOrDefault(i => i.NeedsAttention)
                          ?? _items.FirstOrDefault();
            GuideList.SelectedItem = restore;
            DetailHost.DataContext = restore;
        });
    }

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        _section = tag == "All" ? null : Enum.Parse<GuideSection>(tag);
        _view.Refresh();
        if (GuideList.SelectedItem is null && !_view.IsEmpty)
        {
            _view.MoveCurrentToFirst();
            GuideList.SelectedItem = _view.CurrentItem;
        }
    }

    private void GuideList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GuideList.SelectedItem is GuideItem item)
        {
            DetailHost.DataContext = item;
            FigureHost.Content = GuideFigures.For(item.Guide.Id, this);
            DetailScroll.ScrollToTop();
        }
    }

    private void Step_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GuideStepItem step }) return;
        _progress.SetComplete(step.Key, step.IsDone);
        (DetailHost.DataContext as GuideItem)?.RefreshProgress();
    }

    private void ResetSteps_Click(object sender, RoutedEventArgs e)
    {
        if (DetailHost.DataContext is not GuideItem item) return;
        foreach (var step in item.Steps)
        {
            step.IsDone = false;
            _progress.SetComplete(step.Key, false);
        }
        item.RefreshProgress();
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось открыть ссылку: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
