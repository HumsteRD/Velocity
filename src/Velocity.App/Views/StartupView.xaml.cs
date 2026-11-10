using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Velocity.Core;
using Velocity.Tweaks;
using Velocity.Tweaks.Startup;

namespace Velocity.App.Views;

/// <summary>Запись автозагрузки с желаемым состоянием переключателя.</summary>
public sealed class StartupItem : INotifyPropertyChanged
{
    public required StartupEntry Entry { get; init; }

    public string Name => Entry.Name;
    public string? Command => Entry.Command;
    public string? AdviceText => Entry.AdviceText;

    public string KindName => Entry.Kind switch
    {
        StartupKind.Logon => Loc.T("Программы при входе в Windows"),
        StartupKind.Task => Loc.T("Задачи по расписанию"),
        StartupKind.Service => Loc.T("Службы сторонних программ"),
        _ => ""
    };

    public string Meta => string.Join(" · ", new[] { Entry.Publisher, Entry.Location }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string? BadgeText => Entry.Advice switch
    {
        StartupAdvice.Heavy => Loc.T("Тяжёлая"),
        StartupAdvice.Updater => Loc.T("Обновлятор"),
        StartupAdvice.Keep => Loc.T("Не трогать"),
        _ => null
    };

    public Brush BadgeForeground => Res(Entry.Advice switch
    {
        StartupAdvice.Heavy => "Warning",
        StartupAdvice.Updater => "AccentInk",
        StartupAdvice.Keep => "Success",
        _ => "TextTertiary"
    });

    public Brush BadgeBackground => Res(Entry.Advice switch
    {
        StartupAdvice.Heavy => "WarningDim",
        StartupAdvice.Updater => "AccentTint",
        StartupAdvice.Keep => "NeutralTint",
        _ => "NeutralTint"
    });

    /// <summary>Стоит отключить: включено и помечено как тяжёлое или обновлятор.</summary>
    public bool IsSuggested => Entry.Enabled && Entry.Advice is StartupAdvice.Heavy or StartupAdvice.Updater;

    private bool _desired;
    public bool DesiredEnabled
    {
        get => _desired;
        set
        {
            if (_desired == value) return;
            _desired = value;
            Notify();
            Notify(nameof(IsChanged));
            Notify(nameof(StateText));
            Changed?.Invoke();
        }
    }

    public bool IsChanged => DesiredEnabled != Entry.Enabled;

    public string StateText => (Entry.Kind, DesiredEnabled) switch
    {
        (StartupKind.Service, true) => Loc.T("автозапуск"),
        (StartupKind.Service, false) => Loc.T("вручную"),
        (_, true) => Loc.T("включено"),
        _ => Loc.T("выключено")
    };

    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Res(string key) => Application.Current.Resources[key] as Brush ?? Brushes.Gray;
}

[SupportedOSPlatform("windows")]
public partial class StartupView : UserControl
{
    private readonly ObservableCollection<StartupItem> _items = [];
    private readonly ListCollectionView _view;
    private string _filter = "All";
    private bool _ready;

    public StartupView()
    {
        InitializeComponent();

        _view = new ListCollectionView(_items);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StartupItem.KindName)));
        _view.Filter = o => o is StartupItem item && _filter switch
        {
            "Suggest" => item.IsSuggested,
            "Logon" => item.Entry.Kind == StartupKind.Logon,
            "Task" => item.Entry.Kind == StartupKind.Task,
            "Service" => item.Entry.Kind == StartupKind.Service,
            _ => true
        };
        EntryList.ItemsSource = _view;
        ElevationHint.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;

        _ready = true;
        Loaded += async (_, _) => { if (_items.Count == 0) await LoadAsync(); };
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private async Task LoadAsync()
    {
        Main?.SetStatus(Loc.T("Читаю автозагрузку…"));
        var entries = await Task.Run(StartupManager.Scan);

        _items.Clear();
        var order = new Dictionary<StartupAdvice, int>
        {
            [StartupAdvice.Heavy] = 0, [StartupAdvice.Updater] = 1, [StartupAdvice.Neutral] = 2, [StartupAdvice.Keep] = 3
        };
        foreach (var entry in entries
                     .OrderBy(e => e.Kind)
                     .ThenByDescending(e => e.Enabled)
                     .ThenBy(e => order[e.Advice])
                     .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new StartupItem { Entry = entry, DesiredEnabled = entry.Enabled };
            item.Changed += UpdateSummary;
            _items.Add(item);
        }

        var enabled = entries.Count(e => e.Enabled);
        var suggested = _items.Count(i => i.IsSuggested);
        SubtitleText.Text =
            Loc.F("Запускается само: {0} из {1}. ", Loc.Plural(enabled, Loc.T("запись"), Loc.T("записи"), Loc.T("записей")), entries.Count) +
            (suggested > 0
                ? Loc.F("Стоит отключить: {0} — тяжёлые программы и обновляторы.", suggested)
                : Loc.T("Явных кандидатов на отключение нет."));
        SuggestChip.Content = suggested > 0 ? Loc.F("Стоит отключить · {0}", suggested) : Loc.T("Стоит отключить");

        _view.Refresh();
        UpdateSummary();
        Main?.SetStatus(Loc.F("Автозагрузка: {0} записей", entries.Count));
    }

    private void UpdateSummary()
    {
        var changed = _items.Count(i => i.IsChanged);
        ChangesText.Text = changed == 0
            ? Loc.T("Изменений нет")
            : Loc.F("Изменено: {0}", Loc.Plural(changed, Loc.T("запись"), Loc.T("записи"), Loc.T("записей")));
        ApplyButton.IsEnabled = ResetButton.IsEnabled = changed > 0;
        EmptyText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        _filter = tag;
        _view.Refresh();
        UpdateSummary();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.DesiredEnabled = item.Entry.Enabled;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Current;
        if (state.Engine is null)
        {
            MessageBox.Show(Loc.T("Дождись окончания скана системы."), "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var changed = _items.Where(i => i.IsChanged).ToList();
        if (changed.Count == 0) return;

        var keep = changed.Where(i => !i.DesiredEnabled && i.Entry.Advice == StartupAdvice.Keep).ToList();
        if (keep.Count > 0)
        {
            var names = string.Join("\n", keep.Select(i => $"  • {i.Name} — {i.AdviceText}"));
            if (MessageBox.Show(Loc.F("Ты отключаешь то, что лучше оставить:\n\n{0}\n\nВсё равно отключить?", names),
                    Loc.T("Подтверждение"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        if (changed.Any(i => i.Entry.RequiresElevation) && !Elevation.IsElevated)
        {
            if (Elevation.RequestRestart(Loc.T("Задачи, службы и общие записи автозагрузки меняются только с правами администратора.")))
                return;

            var own = changed.Where(i => !i.Entry.RequiresElevation).ToList();
            if (own.Count == 0) return;
            if (MessageBox.Show(Loc.F("Без прав администратора можно изменить только записи текущего пользователя ({0}). Применить их?", own.Count),
                    Loc.T("Часть изменений будет пропущена"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            changed = own;
        }

        var tweaks = changed.Select(i => (Item: i, Tweak: StartupManager.BuildToggle(i.Entry, i.DesiredEnabled))).ToList();

        ApplyButton.IsEnabled = false;
        ApplyButton.Content = Loc.T("Применяю…");

        // Автозагрузка меняет только то, что запустится при следующем входе или загрузке, —
        // работающую игру это не затрагивает, поэтому проверка античитов здесь не нужна.
        var session = await Task.Run(() => state.Engine.Apply(tweaks.Select(t => t.Tweak),
            new TweakEngine.ApplyOptions { Reason = Loc.T("Автозагрузка"), IgnoreAntiCheat = true }));

        ApplyButton.Content = Loc.T("Применить");

        foreach (var (item, tweak) in tweaks)
        {
            var result = session.Results.FirstOrDefault(r => r.TweakId == tweak.Id);
            if (result?.Outcome == ApplyOutcome.Applied && item.Entry.Kind == StartupKind.Service)
                StartupManager.RememberServiceChange(item.Entry.ServiceName(), item.DesiredEnabled);
        }

        var applied = session.Results.Count(r => r.Outcome == ApplyOutcome.Applied);
        var failed = session.Results.Where(r => r.Outcome is ApplyOutcome.Failed or ApplyOutcome.VerifyFailed).ToList();
        var text = Loc.F("Изменено: {0}", applied);
        if (failed.Count > 0)
            text += Loc.F("\n\nНе удалось ({0}):\n", failed.Count) + string.Join("\n", failed.Select(f => $"  • {f.Title} — {f.Message}"));
        if (changed.Any(i => i.Entry.Kind == StartupKind.Service))
            text += Loc.T("\n\nСлужбы, переведённые на ручной запуск, остановятся после перезагрузки.");
        if (session.SnapshotId is { } id)
            text += Loc.F("\n\nТочка отката: {0}. Вернуть всё можно в разделе «Точки отката».", id);
        MessageBox.Show(text, Loc.T("Автозагрузка"), MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        await LoadAsync();
    }
}

file static class StartupEntryExtensions
{
    /// <summary>Имя службы для записи вида «служба».</summary>
    public static string ServiceName(this StartupEntry entry) => entry.Key;
}
