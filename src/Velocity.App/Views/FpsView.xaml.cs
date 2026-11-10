using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Velocity.Hardware.Bench;

namespace Velocity.App.Views;

/// <summary>Замер FPS в игре: захват, импорт CSV, сравнение «до» и «после».</summary>
public partial class FpsView : UserControl
{
    private sealed record RunRow(FrameRun Run, string Game, string Meta, string Fps);

    private readonly FrameStore _store = new();
    private string _stage = "before";
    private int _seconds = 60;
    private FrameCapture? _capture;
    private DispatcherTimer? _timer;
    private DateTime _phaseEnds;
    private bool _counting;
    private FrameRun? _selected;

    public FpsView()
    {
        InitializeComponent();
        var runs = _store.List();
        // После применения настроек следующий замер, скорее всего, — «после».
        if (runs.Any(r => r.Stage == "before")) _stage = "after";
        BuildSegments();
        AdminNote.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        Reload(runs.FirstOrDefault()?.Id);
        Unloaded += (_, _) => Cancel();
    }

    // ───────────────────────────── Выбор этапа и длительности ─────────────────────────────

    private void BuildSegments()
    {
        StagePanel.Children.Clear();
        foreach (var (id, name) in new[] { ("before", Loc.T("До оптимизации")), ("after", Loc.T("После")) })
            StagePanel.Children.Add(Segment(name, _stage == id, () => { _stage = id; BuildSegments(); }));
        DurationPanel.Children.Clear();
        foreach (var s in new[] { 30, 60, 120 })
            DurationPanel.Children.Add(Segment(Loc.F("{0} с", s), _seconds == s, () => { _seconds = s; BuildSegments(); }));
    }

    private ToggleButton Segment(string text, bool on, Action pick)
    {
        var button = new ToggleButton { Content = text, IsChecked = on, Style = (Style)FindResource("Segment") };
        button.Click += (_, _) => pick();
        return button;
    }

    // ───────────────────────────── Захват ─────────────────────────────

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_timer is not null) { Cancel(); return; }
        if (!Elevation.IsElevated)
        {
            Elevation.RequestRestart(Loc.T("Замер кадров читает события DirectX через трассировку Windows — нужны права администратора."));
            return;
        }

        _counting = true;
        _phaseEnds = DateTime.Now.AddSeconds(10);
        StartButton.Content = Loc.T("Отменить");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += Tick;
        _timer.Start();
        Tick(null, EventArgs.Empty);
    }

    private void Tick(object? sender, EventArgs e)
    {
        var left = _phaseEnds - DateTime.Now;
        if (_counting)
        {
            if (left > TimeSpan.Zero)
            {
                CaptureStatus.Text = Loc.F("Переключись в игру. Замер начнётся через {0:0} с…", Math.Ceiling(left.TotalSeconds));
                return;
            }
            try
            {
                _capture = FrameCapture.Start();
            }
            catch (UnauthorizedAccessException)
            {
                Finish(Loc.T("Windows не дала запустить трассировку: нужны права администратора."));
                return;
            }
            catch (Exception ex)
            {
                Finish(Loc.F("Не удалось начать замер: {0}", ex.Message));
                return;
            }
            SystemSounds.Asterisk.Play();
            _counting = false;
            _phaseEnds = DateTime.Now.AddSeconds(_seconds);
            return;
        }

        if (_capture is null) return;
        if (left > TimeSpan.Zero)
        {
            var leader = _capture.Leader();
            var elapsed = Math.Max(0.25, _capture.Elapsed.TotalSeconds);
            CaptureStatus.Text = leader is { } l
                ? Loc.F("Идёт замер: {0} · около {1:0} кадров/с · осталось {2:0} с", l.Name, l.Frames / elapsed, Math.Ceiling(left.TotalSeconds))
                : Loc.F("Идёт замер · игра пока не рисует кадры · осталось {0:0} с", Math.Ceiling(left.TotalSeconds));
            return;
        }

        var processes = _capture.Stop();
        _capture.Dispose();
        _capture = null;
        SystemSounds.Asterisk.Play();

        var game = processes.FirstOrDefault(p => p.FrameTimesMs.Count >= 100 && !IsDesktop(p.Name));
        if (game is null)
        {
            Finish(Loc.T("Игра не найдена: ни один процесс не рисовал кадры через DirectX 9–12. Запусти игру и повтори; для Vulkan и OpenGL используй импорт CSV из PresentMon."));
            return;
        }

        var run = FrameRun.Create(game.Name, _stage, "capture", game.FrameTimesMs);
        _store.Save(run);
        Finish(Loc.F("Готово: {0}, средний FPS {1:0}, 1% low {2:0}.", game.Name, run.Stats.AvgFps, run.Stats.Low1Fps));
        Reload(run.Id);
    }

    private static bool IsDesktop(string name) =>
        name.Equals("dwm", StringComparison.OrdinalIgnoreCase)
        || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Velocity", StringComparison.OrdinalIgnoreCase);

    private void Finish(string message)
    {
        _timer?.Stop();
        _timer = null;
        _counting = false;
        StartButton.Content = Loc.T("Начать замер");
        CaptureStatus.Text = message;
    }

    private void Cancel()
    {
        if (_capture is not null)
        {
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        if (_timer is not null) Finish(Loc.T("Замер отменён."));
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) =>
        Elevation.RequestRestart(Loc.T("Замер кадров читает события DirectX через трассировку Windows — нужны права администратора."));

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CSV|*.csv", Title = Loc.T("Файл PresentMon или CapFrameX") };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var run = FrameRun.FromCsv(dialog.FileName, _stage);
            _store.Save(run);
            Reload(run.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось прочитать файл: {0}\n\nНужен CSV со столбцом MsBetweenPresents (PresentMon, CapFrameX) или FrameTime (PresentMon 2).", ex.Message),
                "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────────────────────────── Список и подробности ─────────────────────────────

    private void Reload(string? selectId)
    {
        var rows = _store.List().Select(r => new RunRow(r, r.Game,
            Loc.F("{0} · {1:dd.MM HH:mm}", r.Stage == "after" ? Loc.T("после") : Loc.T("до"), r.Started),
            r.Stats.AvgFps.ToString("0", Loc.Culture))).ToList();
        RunList.ItemsSource = rows;
        EmptyRuns.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunList.SelectedItem = rows.FirstOrDefault(r => r.Run.Id == selectId) ?? rows.FirstOrDefault();
        if (rows.Count == 0) DetailPanel.Visibility = Visibility.Collapsed;
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunList.SelectedItem is not RunRow row) return;
        _selected = row.Run;
        ShowRun(row.Run);
    }

    private void ShowRun(FrameRun run)
    {
        DetailPanel.Visibility = Visibility.Visible;
        var s = run.Stats;
        DetailTitle.Text = run.Game;
        DetailMeta.Text = Loc.F("{0} · {1:dd.MM.yyyy HH:mm} · {2:0} с · {3} кадров · {4}",
            run.Stage == "after" ? Loc.T("После оптимизации") : Loc.T("До оптимизации"),
            run.Started, s.DurationS, s.Frames, run.Source == "csv" ? Loc.T("импорт CSV") : Loc.T("замер VELOCITY"));

        var all = _store.List();
        var (before, after) = FrameStore.Pair(all, run.Game);
        var other = run.Stage == "after" ? before : after;

        MetricTiles.Children.Clear();
        MetricTiles.Children.Add(Tile(Loc.T("Средний FPS"), s.AvgFps, other?.Stats.AvgFps, false, run.Stage));
        MetricTiles.Children.Add(Tile("1% low", s.Low1Fps, other?.Stats.Low1Fps, false, run.Stage));
        MetricTiles.Children.Add(Tile("0.1% low", s.Low01Fps, other?.Stats.Low01Fps, false, run.Stage));
        MetricTiles.Children.Add(Tile(Loc.T("Кадр, медиана, мс"), s.MedianMs, other?.Stats.MedianMs, true, run.Stage, "0.0"));
        MetricTiles.Children.Add(Tile(Loc.T("Рывки"), s.Stutters, other?.Stats.Stutters, true, run.Stage, "0"));

        ChartLegend.Text = Loc.F("медиана {0:0.0} мс · 99% кадров быстрее {1:0.0} мс · худший {2:0} мс", s.MedianMs, s.P99Ms, s.MaxMs);
        DrawChart(run);
        BuildCompare(run.Game, before, after);
    }

    private Border Tile(string label, double value, double? other, bool lowerIsBetter, string stage, string format = "0")
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("TextMuted") });
        panel.Children.Add(new TextBlock
        {
            Text = value.ToString(format, Loc.Culture),
            FontFamily = (FontFamily)FindResource("FontDisplay"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        if (other is { } o && o > 0 && stage == "after")
        {
            var change = (value - o) / o * 100;
            bool better = lowerIsBetter ? change < -2 : change > 2;
            bool worse = lowerIsBetter ? change > 2 : change < -2;
            panel.Children.Add(new TextBlock
            {
                Text = Math.Abs(change) < 2 ? Loc.T("без изменений") : Loc.F("{0:+0;-0}% к «до»", change),
                Style = (Style)FindResource("TextMuted"),
                Foreground = better ? (Brush)FindResource("Success") : worse ? (Brush)FindResource("Critical") : (Brush)FindResource("TextTertiary")
            });
        }
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 8, 0), Child = panel };
    }

    /// <summary>Пять плиток в ряд — только когда им хватает места; иначе три и две ниже.</summary>
    private void DetailPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        MetricTiles.Columns = e.NewSize.Width < 640 ? 3 : 5;

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selected is not null) DrawChart(_selected);
    }

    /// <summary>
    /// График времени кадра. Точек больше, чем пикселей, поэтому в каждом столбце берётся
    /// худший кадр — иначе рывки, ради которых график и нужен, сгладились бы.
    /// </summary>
    private void DrawChart(FrameRun run)
    {
        Chart.Children.Clear();
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        var times = run.FrameTimesMs;
        if (w < 20 || h < 20 || times.Count < 2) return;

        var s = run.Stats;
        var top = Math.Max(s.P99Ms * 1.6, s.MedianMs * 3);
        double Y(double ms) => h - Math.Min(ms, top) / top * (h - 4);

        foreach (var (ms, brush) in new[] { (s.MedianMs, "BorderStrong"), (s.MedianMs * 2, "WarningDim") })
        {
            var line = new Line { X1 = 0, X2 = w, Y1 = Y(ms), Y2 = Y(ms), Stroke = (Brush)FindResource(brush), StrokeThickness = 1, StrokeDashArray = [4, 4] };
            Chart.Children.Add(line);
        }

        int columns = (int)Math.Min(w, times.Count);
        var points = new PointCollection(columns);
        var stutters = new List<double>();
        for (int c = 0; c < columns; c++)
        {
            int from = (int)((long)c * times.Count / columns), to = (int)((long)(c + 1) * times.Count / columns);
            double worst = 0;
            for (int i = from; i < Math.Max(to, from + 1) && i < times.Count; i++) worst = Math.Max(worst, times[i]);
            double x = c * w / Math.Max(1, columns - 1);
            points.Add(new Point(x, Y(worst)));
            if (worst > s.MedianMs * 2 && worst - s.MedianMs > 5) stutters.Add(x);
        }
        Chart.Children.Add(new Polyline
        {
            Points = points,
            Stroke = (Brush)FindResource("Accent"),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round
        });
        foreach (var x in stutters)
        {
            var mark = new Rectangle { Width = 2, Height = 6, Fill = (Brush)FindResource("Critical") };
            Canvas.SetLeft(mark, x - 1);
            Canvas.SetTop(mark, h - 6);
            Chart.Children.Add(mark);
        }
    }

    private void BuildCompare(string game, FrameRun? before, FrameRun? after)
    {
        CompareGrid.Children.Clear();
        CompareGrid.RowDefinitions.Clear();
        CompareGrid.ColumnDefinitions.Clear();
        if (before is null || after is null)
        {
            CompareHint.Text = before is null
                ? Loc.F("Для «{0}» нет замера «до». Сравнение появится, когда будут оба.", game)
                : Loc.F("Для «{0}» нет замера «после». Примени настройки, перезагрузись и замерь ту же сцену.", game);
            return;
        }

        foreach (var width in new[] { 1.4, 1, 1, 1 })
            CompareGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });

        (string Name, double B, double A, bool Lower, string F)[] rows =
        [
            (Loc.T("Средний FPS"), before.Stats.AvgFps, after.Stats.AvgFps, false, "0"),
            ("1% low", before.Stats.Low1Fps, after.Stats.Low1Fps, false, "0"),
            ("0.1% low", before.Stats.Low01Fps, after.Stats.Low01Fps, false, "0"),
            (Loc.T("Рывки"), before.Stats.Stutters, after.Stats.Stutters, true, "0"),
        ];
        AddRow(0, [Loc.T("Показатель"), Loc.T("До"), Loc.T("После"), Loc.T("Изменение")], header: true);
        for (int i = 0; i < rows.Length; i++)
        {
            var (name, b, a, lower, f) = rows[i];
            var change = b > 0 ? (a - b) / b * 100 : 0;
            AddRow(i + 1, [name, b.ToString(f, Loc.Culture), a.ToString(f, Loc.Culture),
                Math.Abs(change) < 2 ? Loc.T("без изменений") : $"{change:+0;-0}%"],
                tone: Math.Abs(change) < 2 ? 0 : (lower ? change < 0 : change > 0) ? 1 : -1);
        }
        CompareHint.Text = Loc.T("Разница меньше 2–3% — в пределах погрешности. Сравнивай прогоны одной сцены.");
    }

    private void AddRow(int row, string[] cells, bool header = false, int tone = 0)
    {
        CompareGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < cells.Length; c++)
        {
            var text = new TextBlock
            {
                Text = cells[c],
                Style = (Style)FindResource(header ? "TextLabel" : "TextBase"),
                Margin = new Thickness(0, 4, 8, 4)
            };
            if (!header && c == 3)
                text.Foreground = tone > 0 ? (Brush)FindResource("Success") : tone < 0 ? (Brush)FindResource("Critical") : (Brush)FindResource("TextTertiary");
            Grid.SetRow(text, row);
            Grid.SetColumn(text, c);
            CompareGrid.Children.Add(text);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (MessageBox.Show(Loc.F("Удалить замер «{0}» от {1:dd.MM HH:mm}?", _selected.Game, _selected.Started), "VELOCITY",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Delete(_selected.Id);
        _selected = null;
        Reload(null);
    }
}
