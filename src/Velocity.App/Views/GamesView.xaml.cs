using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Velocity.Core;
using Velocity.Tweaks.Games;

namespace Velocity.App.Views;

public sealed class GameRow
{
    public required MatchedGame Match { get; init; }
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required Brush StateBrush { get; set; }
}

public sealed class ChecklistItem : INotifyPropertyChanged
{
    private bool _isComplete;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Value { get; init; }
    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (_isComplete == value) return;
            _isComplete = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

[SupportedOSPlatform("windows")]
public partial class GamesView : UserControl
{
    private readonly DispatcherTimer _processTimer;
    private readonly GameChecklistStore _checklists = new();
    private List<GameRow> _games = [];
    private GameRow? _selected;

    public GamesView()
    {
        InitializeComponent();
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _processTimer.Tick += (_, _) => RefreshProcessState();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppState.Current.ReportChanged += Reload;
        _processTimer.Start();
        if (AppState.Current.HasReport) Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppState.Current.ReportChanged -= Reload;
        _processTimer.Stop();
    }

    private void Reload()
    {
        Dispatcher.Invoke(() =>
        {
            var state = AppState.Current;
            if (state.Report is null || state.Facts is null) return;
            _games = state.GameProfiles.Match(state.Report.Games)
                .Select(match => new GameRow
                {
                    Match = match,
                    Name = match.Profile.Name,
                    Location = Describe(match.Installed),
                    StateBrush = (Brush)FindResource("TextDisabled")
                }).ToList();

            GameList.ItemsSource = _games;
            EmptyState.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailHost.Visibility = _games.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SubtitleText.Text = _games.Count > 0
                ? Loc.F("Готовых профилей: {0} · отметки подготовки сохраняются автоматически", _games.Count)
                : Loc.F("Установленных игр найдено: {0}", state.Report.Games.Count);

            var target = _selected is null ? _games.FirstOrDefault() : _games.FirstOrDefault(g => g.Match.Profile.Id == _selected.Match.Profile.Id);
            GameList.SelectedItem = target;
            if (target is not null) ShowGame(target);
            RefreshProcessState();
        });
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is GameRow row) ShowGame(row);
    }

    private void ShowGame(GameRow row)
    {
        _selected = row;
        var state = AppState.Current;
        if (state.Facts is null) return;
        var profile = row.Match.Profile;
        SelectedGameName.Text = profile.Name;
        SelectedGameMeta.Text = Describe(row.Match.Installed);

        var settings = profile.Settings
            .Where(setting => setting.When.Count == 0 || state.Facts.MatchesAll(setting.When))
            .Select((setting, index) => new ChecklistItem
            {
                Key = $"{profile.Id}:{index}", Name = setting.Name, Value = setting.Value,
                IsComplete = _checklists.IsCompleteAny($"{profile.Id}:{index}")
            }).ToList();
        Checklist.ItemsSource = settings;
        NoChecklist.Visibility = settings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateChecklistSummary(settings);

        var launch = GameProfileEngine.ResolveTokens(profile.LaunchOptions, state.Facts);
        LaunchBlock.Visibility = string.IsNullOrWhiteSpace(launch) ? Visibility.Collapsed : Visibility.Visible;
        LaunchOptionsText.Text = launch ?? string.Empty;
        CopyLaunchButton.Tag = launch;

        var hasFiles = profile.Config.Count > 0;
        ApplyProfileButton.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ProfileActionHint.Text = hasFiles
            ? Loc.T("Правки в конфигурационных файлах. Оригиналы сохраняются рядом с файлом до изменения.")
            : Loc.T("Этот профиль меняется через настройки игры и параметры запуска — автоматических правок файла нет.");
        ProfileActionNote.Text = hasFiles
            ? Loc.T("Перед применением закрой игру: она может перезаписать конфигурацию при выходе.")
            : profile.LaunchOptionsWhy ?? Loc.T("Отмечай пункты слева после изменения настроек в игре.");

        NotesList.ItemsSource = profile.Notes;
        NotesBlock.Visibility = profile.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRunState();
    }

    private void RefreshProcessState()
    {
        foreach (var game in _games)
            game.StateBrush = IsRunning(game.Match.Profile) ? (Brush)FindResource("Success") : (Brush)FindResource("TextDisabled");
        var current = GameList.SelectedItem;
        GameList.Items.Refresh();
        GameList.SelectedItem = current;
        UpdateRunState();
    }

    private void UpdateRunState()
    {
        if (_selected is null) return;
        var running = IsRunning(_selected.Match.Profile);
        RunState.Text = running ? Loc.T("игра запущена") : Loc.T("игра закрыта");
        RunIndicator.Fill = (Brush)FindResource(running ? "Success" : "TextDisabled");
        ApplyProfileButton.IsEnabled = !running;
        if (running && _selected.Match.Profile.Config.Count > 0)
            ProfileActionNote.Text = Loc.T("Закрой игру перед применением: её процесс сейчас запущен.");
    }

    private static bool IsRunning(GameProfile profile)
    {
        foreach (var executable in profile.Executables)
        {
            try
            {
                if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Length > 0) return true;
            }
            catch { }
        }
        return false;
    }

    private void ChecklistChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ChecklistItem item }) return;
        _checklists.SetCompleteAny(item.Key, item.IsComplete);
        UpdateChecklistSummary(Checklist.Items.Cast<ChecklistItem>().ToList());
    }

    private void UpdateChecklistSummary(IReadOnlyCollection<ChecklistItem> items)
    {
        if (items.Count == 0) { ChecklistSummary.Text = Loc.T("Настройки внутри игры"); return; }
        var done = items.Count(item => item.IsComplete);
        ChecklistSummary.Text = done == items.Count ? Loc.T("Профиль подготовлен") : Loc.F("Готово {0} из {1}", done, items.Count);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            if (Window.GetWindow(this) is MainWindow main) main.SetStatus(Loc.T("Параметры запуска скопированы в буфер обмена"));
        }
        catch { MessageBox.Show(Loc.T("Не удалось получить доступ к буферу обмена. Попробуй ещё раз."), "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || AppState.Current.Facts is null) return;
        var match = _selected.Match;
        if (IsRunning(match.Profile))
        {
            MessageBox.Show(Loc.T("Сначала закрой игру. Иначе она может перезаписать свой конфиг при выходе."), match.Profile.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var engine = AppState.Current.GameProfiles;
        var preview = engine.Apply(match, AppState.Current.Facts, dryRun: true);
        var files = string.Join("\n", preview.Files.Select(file => file.Error is null ? $"• {file.File}" : $"• {file.File} — {file.Error}"));
        if (MessageBox.Show(Loc.F("Будут изменены файлы настроек:\n\n{0}\n\nОригиналы сохраняются рядом с файлами. Продолжить?", files), match.Profile.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var result = engine.Apply(match, AppState.Current.Facts);
        var changed = result.Files.Count(file => file.Error is null);
        var errors = result.Files.Where(file => file.Error is not null).ToList();
        MessageBox.Show(errors.Count == 0 ? Loc.F("Изменено файлов: {0}", changed) : Loc.F("Изменено файлов: {0}\n\nНе удалось:\n{1}", changed, string.Join("\n", errors.Select(file => $"• {file.File} — {file.Error}"))), match.Profile.Name, MessageBoxButton.OK, errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private static string Describe(GameInfo game)
    {
        var parts = new List<string>();
        if (game.SizeGb > 0) parts.Add(Loc.F("{0} ГБ", game.SizeGb));
        if (game.DriveLetter is { } drive) parts.Add(Loc.F("диск {0}", drive));
        parts.Add(game.Launcher.ToString());
        return string.Join("  ·  ", parts);
    }
}
