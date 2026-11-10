using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velocity.Core;
using Velocity.Hardware.Bench;
using Velocity.Tweaks;

namespace Velocity.App.Views;

/// <summary>
/// Разбор ПК — главный сценарий программы: она рассчитана на один подход, а не на
/// постоянную работу в фоне. Экран ведёт по шагам и показывает, что уже сделано.
/// </summary>
public partial class DiagnosisView : UserControl
{
    private enum StepState { Done, Current, Pending }

    private readonly GameChecklistStore _progress = new("guide-checklist.json");

    public DiagnosisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppState.Current.ReportChanged += Rebuild;
            Rebuild();
        };
        Unloaded += (_, _) => AppState.Current.ReportChanged -= Rebuild;
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    /// <summary>Узкое окно: колонка итога переезжает под шаги, а не сжимает их текст.</summary>
    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 860;
        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(280);
        GapColumn.Width = narrow ? new GridLength(0) : new GridLength(16);
        Grid.SetColumn(SidePanel, narrow ? 0 : 2);
        Grid.SetRow(SidePanel, narrow ? 1 : 0);
        SidePanel.Margin = narrow ? new Thickness(0, 4, 0, 0) : new Thickness(0);
    }

    private void Rebuild() => Dispatcher.Invoke(Build);

    private void Build()
    {
        StepsPanel.Children.Clear();
        var state = AppState.Current;
        if (state.Report is not { } report)
        {
            StepsPanel.Children.Add(Step(1, Loc.T("Проверка системы"), Loc.T("Сканирование…"),
                Loc.T("Программа читает конфигурацию, настройки Windows, драйверы и автозагрузку. Ничего не меняется."),
                StepState.Current));
            return;
        }

        var session = DiagnosisSession.Ensure(report);
        var bench = new BenchStore();
        var benchRuns = bench.List();
        var frames = new FrameStore().List();
        var applied = new SnapshotStore().List()
            .Where(s => s.CreatedAt.LocalDateTime >= session.Started && s.Changes.Any(c => !c.Reverted))
            .ToList();
        var lastApplied = applied.Count > 0 ? applied.Max(s => s.CreatedAt) : (DateTimeOffset?)null;

        var problems = report.Findings.Where(f => f.Severity != FindingSeverity.Info).ToList();
        var fixable = DiagnosisPlan.AutoFixable(state);
        var manual = DiagnosisPlan.ManualGuides(state);
        int manualDone = manual.Count(g => g.Steps.Count > 0
            && Enumerable.Range(0, g.Steps.Count).All(i => _progress.IsComplete($"{g.Id}#{i}")));

        bool beforeDone = benchRuns.Any(r => r.CreatedAt.LocalDateTime >= session.Started.AddDays(-30) && (lastApplied is null || r.CreatedAt < lastApplied))
                          || frames.Any(f => f.Stage == "before");
        // Исправления применили раньше первого замера — «до» уже не снять. Шаг не должен
        // висеть текущим: отмечаем его пропущенным и честно говорим, что сравнения не будет.
        bool beforeSkipped = !beforeDone && lastApplied is not null;
        beforeDone |= beforeSkipped;
        bool fixDone = fixable.Count == 0 || applied.Count > 0;
        bool manualAllDone = manual.Count == 0 || manualDone == manual.Count;
        bool afterDone = lastApplied is not null
                         && (benchRuns.Any(r => r.CreatedAt > lastApplied) || frames.Any(f => f.Stage == "after" && f.Started > lastApplied.Value.LocalDateTime));

        // ── Итог справа
        ScoreText.Text = report.Score.ToString(Loc.Culture);
        var delta = report.Score - session.InitialScore;
        ScoreDelta.Text = delta == 0 ? Loc.T("В начале разбора — столько же")
            : Loc.F("В начале разбора — {0} ({1:+0;-0})", session.InitialScore, delta);
        FoundText.Text = session.InitialFindings.Count.ToString(Loc.Culture);
        FixedText.Text = session.InitialFindings.Count(t => problems.All(p => p.Title != t)).ToString(Loc.Culture);
        SessionText.Text = Loc.F("Разбор начат {0:dd.MM HH:mm}", session.Started)
            + (session.ReportCreated is { } rc ? Loc.F(" · отчёт {0:dd.MM HH:mm}", rc) : "");

        // ── Шаги
        var current = !beforeDone ? 2 : !fixDone ? 3 : !manualAllDone ? 4 : !afterDone ? 5 : 6;
        StepState S(int n, bool done) => done ? StepState.Done : n == current ? StepState.Current : StepState.Pending;

        var critical = problems.Count(p => p.Severity == FindingSeverity.Critical);
        StepsPanel.Children.Add(Step(1, Loc.T("Проверка системы"),
            Loc.F("Оценка {0}/100 · проблем: {1}, из них критичных: {2}", report.Score, problems.Count, critical),
            problems.Count == 0
                ? Loc.T("Явных проблем не найдено. Дальше — замер и тонкая настройка.")
                : string.Join("\n", problems.Take(4).Select(p => "• " + p.Title)) + (problems.Count > 4 ? "\n" + Loc.F("…и ещё {0}", problems.Count - 4) : ""),
            StepState.Done,
            (Loc.T("Пересканировать"), Rescan, false),
            (Loc.T("Подробнее"), () => Main?.NavigateTo("Dashboard"), false)));

        var lastBench = benchRuns.FirstOrDefault();
        StepsPanel.Children.Add(Step(2, Loc.T("Замер «до»"),
            beforeSkipped
                ? Loc.T("Пропущен: изменения применены раньше первого замера")
                : Loc.F("Система: {0} · FPS в играх: {1}",
                    lastBench is null ? Loc.T("нет замера") : Loc.F("замер {0:dd.MM HH:mm}", lastBench.CreatedAt.LocalDateTime),
                    frames.Count(f => f.Stage == "before")),
            Loc.T("Без точки отсчёта не видно, что дала оптимизация. Замер системы — 20 секунд. Замер FPS — 60 секунд в одной и той же сцене игры."),
            S(2, beforeDone),
            (Loc.T("Замер системы"), () => Main?.NavigateTo("Benchmark"), !beforeDone),
            (Loc.T("FPS в игре"), () => Main?.NavigateTo("Fps"), false)));

        StepsPanel.Children.Add(Step(3, Loc.T("Автоматические исправления"),
            fixable.Count == 0 ? Loc.T("Всё, что программа может исправить сама, уже в порядке")
                : Loc.F("Программа исправит сама: {0}", fixable.Count),
            fixable.Count == 0
                ? Loc.T("Дополнительные настройки под игры — в разделе «Оптимизация»: набор «Соревновательный» подходит большинству.")
                : string.Join("\n", fixable.Take(5).Select(f => "• " + f.Title))
                  + "\n" + Loc.T("Перед применением создаётся точка отката."),
            S(3, fixDone),
            fixable.Count > 0 ? (Loc.F("Исправить ({0})", fixable.Count), FixAll, true) : (Loc.T("Оптимизация"), () => Main?.NavigateTo("Optimize"), false),
            fixable.Count > 0 ? (Loc.T("Выбрать вручную"), () => Main?.NavigateTo("Optimize"), false) : default));

        StepsPanel.Children.Add(Step(4, Loc.T("Ручные шаги: BIOS, железо, сеть"),
            manual.Count == 0 ? Loc.T("Для этой системы ручных шагов не требуется")
                : Loc.F("Выполнено {0} из {1}", manualDone, manual.Count),
            manual.Count == 0
                ? Loc.T("Скан не нашёл того, что нужно менять в BIOS или руками. Руководства всё равно доступны в разделе «Руководства».")
                : string.Join("\n", manual.Take(5).Select(g => "• " + g.Title + (string.IsNullOrEmpty(g.Gain) ? "" : " — " + g.Gain))),
            S(4, manualAllDone),
            (Loc.T("Руководства"), () => Main?.NavigateTo("Guides"), manual.Count > 0 && !manualAllDone)));

        StepsPanel.Children.Add(Step(5, Loc.T("Замер «после»"),
            afterDone ? Loc.T("Есть замер после изменений") : Loc.T("Повтори те же замеры после перезагрузки"),
            Loc.T("Перезагрузись, если программа просила, подожди пару минут после входа в Windows и повтори замеры в тех же условиях: та же сцена, те же настройки графики."),
            S(5, afterDone),
            (Loc.T("Замер системы"), () => Main?.NavigateTo("Benchmark"), current == 5),
            (Loc.T("FPS в игре"), () => Main?.NavigateTo("Fps"), false)));

        StepsPanel.Children.Add(Step(6, Loc.T("Отчёт"),
            session.ReportCreated is { } created ? Loc.F("Создан {0:dd.MM HH:mm}", created) : Loc.T("Ещё не создан"),
            Loc.T("Итог в одном файле: что было, что сделано, цифры до и после, что осталось."),
            session.ReportCreated is not null ? StepState.Done : current == 6 ? StepState.Current : StepState.Pending,
            (Loc.T("Создать отчёт"), CreateReport, current == 6)));
    }

    // ───────────────────────────── Карточка шага ─────────────────────────────

    private Border Step(int number, string title, string status, string detail, StepState state,
        params (string Label, Action Act, bool Primary)[] actions)
    {
        var accent = (Brush)FindResource("Accent");
        var success = (Brush)FindResource("Success");

        var marker = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1.5),
            BorderBrush = state switch
            {
                StepState.Done => success,
                StepState.Current => accent,
                _ => (Brush)FindResource("BorderStrong")
            },
            Background = state == StepState.Current ? (Brush)FindResource("AccentTint") : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = state == StepState.Done ? "" : number.ToString(Loc.Culture),
                FontFamily = state == StepState.Done ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") : (FontFamily)FindResource("FontUi"),
                FontSize = state == StepState.Done ? 12 : 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = state switch
                {
                    StepState.Done => success,
                    StepState.Current => (Brush)FindResource("AccentInk"),
                    _ => (Brush)FindResource("TextTertiary")
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var body = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
        body.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("TextBase"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = state == StepState.Pending ? (Brush)FindResource("TextSecondary") : (Brush)FindResource("TextPrimary")
        });
        body.Children.Add(new TextBlock
        {
            Text = status,
            Style = (Style)FindResource("TextMuted"),
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = state == StepState.Current ? (Brush)FindResource("AccentInk") : (Brush)FindResource("TextTertiary"),
            TextWrapping = TextWrapping.Wrap
        });
        if (state != StepState.Done || number == 1)
            body.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)FindResource("TextBody"),
                FontSize = (double)FindResource("FontSm"),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            });

        var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        foreach (var (label, act, primary) in actions.Where(a => a.Label is not null))
        {
            var button = new Button
            {
                Content = label,
                // Главная кнопка — только у текущего шага: глаз сразу видит, что делать дальше.
                Style = (Style)FindResource(primary && state == StepState.Current ? "ButtonPrimary" : "ButtonCompact"),
                Margin = new Thickness(0, 0, 0, 6),
                MinWidth = 140
            };
            button.Click += (_, _) => act();
            buttons.Children.Add(button);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        grid.Children.Add(buttons);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(20, 18, 20, 12),
            Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = state == StepState.Current ? (Brush)FindResource("BorderAccent") : (Brush)FindResource("BorderSubtle"),
            Opacity = state == StepState.Pending ? 0.82 : 1,
            Child = grid
        };
    }

    // ───────────────────────────── Действия ─────────────────────────────

    private async void Rescan()
    {
        Main?.SetStatus(Loc.T("Сканирование системы…"));
        await AppState.Current.ScanAsync();
        Main?.SetStatus(Loc.F("Скан занял {0} мс", AppState.Current.Report?.ScanDurationMs));
    }

    private async void FixAll()
    {
        var state = AppState.Current;
        if (state.Engine is null) return;
        var tweaks = DiagnosisPlan.AutoFixable(state)
            .Select(f => state.Catalog.ById(f.FixTweakId!)!)
            .DistinctBy(t => t.Id)
            .ToList();
        if (tweaks.Count == 0) return;

        if (tweaks.Any(t => t.RequiresElevation) && !Elevation.IsElevated)
        {
            if (Elevation.RequestRestart(Loc.T("Часть исправлений меняет системные настройки и требует прав администратора.")))
                return;
            tweaks = tweaks.Where(t => !t.RequiresElevation).ToList();
            if (tweaks.Count == 0) return;
        }

        var list = string.Join("\n", tweaks.Select(t => "• " + t.Title));
        if (MessageBox.Show(Loc.F("Будет применено:\n\n{0}\n\nПеред изменениями создаётся точка отката. Продолжить?", list),
                "VELOCITY", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        Main?.SetStatus(Loc.T("Применяю…"));
        var session = await Task.Run(() => state.Engine.Apply(tweaks,
            new TweakEngine.ApplyOptions { Reason = Loc.T("Разбор: исправление найденного") }));

        if (session.AbortReason is { } abort)
        {
            MessageBox.Show(abort, Loc.T("Не применено"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var applied = session.Results.Count(r => r.Outcome == ApplyOutcome.Applied);
        var failed = session.Results.Where(r => r.Outcome is ApplyOutcome.Failed or ApplyOutcome.VerifyFailed).ToList();
        var text = Loc.F("Применено: {0}", applied)
                   + (failed.Count > 0 ? "\n\n" + Loc.T("Не удалось:") + "\n" + string.Join("\n", failed.Select(f => $"• {f.Title}: {f.Message}")) : "")
                   + (session.RebootRequired ? "\n\n" + Loc.T("Часть изменений вступит в силу после перезагрузки.") : "");
        MessageBox.Show(text, "VELOCITY", MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        await AppState.Current.ScanAsync();
    }

    private void CreateReport()
    {
        var state = AppState.Current;
        if (state.Report is not { } report) return;
        try
        {
            var session = DiagnosisSession.Ensure(report);
            var path = ReportBuilder.SaveAndOpen(ReportBuilder.Build(state, session, _progress));
            session.ReportCreated = DateTime.Now;
            session.Save();
            Main?.SetStatus(Loc.F("Отчёт сохранён: {0}", path));
            Build();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось создать отчёт: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Report_Click(object sender, RoutedEventArgs e) => CreateReport();

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Report is not { } report) return;
        if (MessageBox.Show(Loc.T("Начать новый разбор? Текущее состояние станет точкой «до». Замеры и точки отката сохранятся."),
                "VELOCITY", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DiagnosisSession.StartNew(report).Save();
        Build();
    }
}
