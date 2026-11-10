using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Velocity.Hardware;
using Velocity.Hardware.Bench;

namespace Velocity.App.Views;

/// <summary>Строка истории замеров.</summary>
public sealed class BenchRunItem
{
    public required BenchResult Result { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public bool IsBaseline { get; init; }
}

/// <summary>Строка таблицы сравнения.</summary>
public sealed class BenchRow
{
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string? Hint { get; init; }
    public required string BeforeText { get; init; }
    public required string AfterText { get; init; }
    public required string DeltaText { get; init; }
    public required Brush DeltaBrush { get; init; }
}

[SupportedOSPlatform("windows")]
public partial class BenchmarkView : UserControl
{
    private readonly BenchStore _store = new();
    private readonly ObservableCollection<BenchRow> _rows = [];
    private CancellationTokenSource? _cts;

    public BenchmarkView()
    {
        InitializeComponent();
        ((CollectionViewSource)Resources["GroupedRows"]).Source = _rows;
        LoadHistory(selectId: null);
    }

    // ───────────────────────────── История ─────────────────────────────

    private void LoadHistory(string? selectId)
    {
        var runs = _store.List();
        var baseline = _store.BaselineId;

        // Первый замер автоматически становится точкой «до»: обычно его делают до оптимизации.
        if (runs.Count > 0 && (baseline is null || runs.All(r => r.Id != baseline)))
        {
            baseline = runs[0].Id;
            _store.BaselineId = baseline;
        }

        var items = runs
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new BenchRunItem
            {
                Result = r,
                Title = string.IsNullOrWhiteSpace(r.Label) ? Loc.F("Замер {0:dd.MM HH:mm}", r.CreatedAt) : r.Label,
                Subtitle = $"{r.CreatedAt:dd.MM.yyyy HH:mm} · {Describe(r)}",
                IsBaseline = r.Id == baseline
            })
            .ToList();

        RunList.ItemsSource = items;
        EmptyHistory.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BaselineButton.IsEnabled = DeleteButton.IsEnabled = items.Count > 0;

        RunList.SelectedItem = items.FirstOrDefault(i => i.Result.Id == selectId) ?? items.FirstOrDefault();
        if (items.Count == 0) ShowComparison(null, null);
    }

    private static string Describe(BenchResult r)
    {
        var wake = r.Get("wake.p99");
        var mem = r.Get("mem.latency");
        var parts = new List<string>();
        if (wake is not null) parts.Add(Loc.F("пробуждение {0} мкс", Format(wake.Value, wake.Unit)));
        if (mem is not null) parts.Add(Loc.F("память {0} нс", Format(mem.Value, mem.Unit)));
        return parts.Count == 0 ? Loc.T("без данных") : string.Join(", ", parts);
    }

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunList.SelectedItem is not BenchRunItem selected) return;

        var all = (IEnumerable<BenchRunItem>)RunList.ItemsSource;
        var baseline = all.FirstOrDefault(i => i.IsBaseline)?.Result;

        // Выбрана сама точка «до» — сравниваем её с самым свежим замером после неё, если есть.
        if (baseline is not null && selected.Result.Id == baseline.Id)
        {
            var later = all.Select(i => i.Result).Where(r => r.CreatedAt > baseline.CreatedAt)
                .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            ShowComparison(baseline, later);
            return;
        }

        ShowComparison(baseline, selected.Result);
    }

    private void Baseline_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not BenchRunItem selected) return;
        _store.BaselineId = selected.Result.Id;
        LoadHistory(selected.Result.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RunList.SelectedItem is not BenchRunItem selected) return;
        var answer = MessageBox.Show(Loc.F("Удалить «{0}» из истории?", selected.Title), Loc.T("Удаление замера"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _store.Delete(selected.Result.Id);
        LoadHistory(null);
    }

    // ───────────────────────────── Сравнение ─────────────────────────────

    private void ShowComparison(BenchResult? before, BenchResult? after)
    {
        _rows.Clear();
        ErrorsText.Visibility = Visibility.Collapsed;

        if (before is null && after is null)
        {
            CompareTitle.Text = Loc.T("Замеров пока нет");
            CompareSubtitle.Text = Loc.T("Нажми «Запустить замер». Сделай его до применения оптимизаций — он станет точкой «до».");
            ContextText.Text = "";
            return;
        }

        // Есть только точка «до» и ничего после неё: показываем её значения без сравнения.
        var current = after ?? before!;
        var reference = after is null ? null : before;

        CompareTitle.Text = reference is null
            ? Loc.T("Результаты замера")
            : Loc.F("«До» {0:dd.MM HH:mm}  →  «после» {1:dd.MM HH:mm}", reference.CreatedAt, current.CreatedAt);
        CompareSubtitle.Text = reference is null
            ? Loc.T("Это точка «до». Примени оптимизации, перезагрузись, если нужно, и запусти замер снова — здесь появится разница.")
            : Loc.T("Зелёное — стало лучше, красное — хуже. Разница меньше погрешности замера помечена как «без изменений».");

        foreach (var metric in current.Metrics)
        {
            var old = reference?.Get(metric.Key);
            _rows.Add(BuildRow(metric, old));
        }

        ContextText.Text = DescribeContext(current, reference);
        if (current.Errors.Count > 0)
        {
            ErrorsText.Text = Loc.T("Не удалось измерить: ") + string.Join("; ", current.Errors);
            ErrorsText.Visibility = Visibility.Visible;
        }
    }

    private static BenchRow BuildRow(BenchMetric now, BenchMetric? before)
    {
        string deltaText;
        Brush brush;

        if (before is null)
        {
            deltaText = "—";
            brush = Res("TextTertiary");
        }
        else
        {
            double diff = now.Value - before.Value;
            double basis = Math.Abs(before.Value);
            double percent = basis > 1e-9 ? diff / basis * 100 : 0;
            double noise = Math.Max(Math.Max(now.NoisePercent, before.NoisePercent), 2);

            // Потери пакетов сравниваем в абсолютных процентах: от нуля «процент изменения» не считается.
            bool absolute = now.Unit == "%";
            double magnitude = absolute ? Math.Abs(diff) : Math.Abs(percent);
            bool significant = absolute ? magnitude >= 1 : magnitude >= noise;

            if (!significant)
            {
                deltaText = Loc.T("без изменений");
                brush = Res("TextTertiary");
            }
            else
            {
                bool better = now.LowerIsBetter ? diff < 0 : diff > 0;
                var sign = diff > 0 ? "+" : "−";
                deltaText = absolute
                    ? Loc.F("{0}{1} п.п. · {2}", sign, Math.Abs(diff).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")), (better ? Loc.T("лучше") : Loc.T("хуже")))
                    : $"{sign}{Math.Abs(percent):0}% · {(better ? Loc.T("лучше") : Loc.T("хуже"))}";
                brush = better ? Res("Success") : Res("Critical");
            }
        }

        return new BenchRow
        {
            // Названия метрик приходят из модуля замеров по-русски — переводим при выводе.
            Group = Loc.T(now.Group),
            Title = $"{Loc.T(now.Title)}, {Loc.T(now.Unit)}",
            Hint = now.Hint is null ? null : Loc.T(now.Hint),
            BeforeText = before is null ? "—" : Format(before.Value, before.Unit),
            AfterText = Format(now.Value, now.Unit),
            DeltaText = deltaText,
            DeltaBrush = brush
        };
    }

    private static string DescribeContext(BenchResult current, BenchResult? reference)
    {
        string Line(BenchResult r) =>
            string.Join(" · ", r.Context.Select(kv => $"{kv.Key}: {kv.Value}")
                .Append(Loc.F("длительность {0:0} с", r.DurationSeconds)));

        var text = Loc.T("Условия «после»: ") + Line(current);
        if (reference is not null) text = Loc.T("Условия «до»: ") + Line(reference) + "\n" + text;
        return text;
    }

    private static string Format(double value, string unit)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return unit switch
        {
            "мкс" or "млн оп/с" => value.ToString("N0", ru),
            "нс" or "ГБ/с" => value.ToString("0.0", ru),
            "мс" => value.ToString(value < 10 ? "0.00" : "0.0", ru),
            "%" => value.ToString("0.#", ru),
            _ => value.ToString("0.##", ru)
        };
    }

    private static Brush Res(string key) => Application.Current.Resources[key] as Brush ?? Brushes.Gray;

    // ───────────────────────────── Запуск ─────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;

        var report = AppState.Current.Report;
        var context = new Dictionary<string, string>();
        if (report?.Tuning.PowerPlanName is { } plan) context[Loc.T("схема питания")] = plan;
        if (report?.Tuning.CurrentTimerResolutionMs is { } timer) context[Loc.T("таймер при скане")] = Loc.F("{0:0.###} мс", timer);
        context[Loc.T("права")] = Elevation.IsElevated ? Loc.T("администратор") : Loc.T("обычные");
        var interceptors = NetworkProbe.TrafficInterceptors();
        if (interceptors.Count > 0) context[Loc.T("перехват трафика")] = string.Join(", ", interceptors);

        var options = new BenchOptions
        {
            IncludeNetwork = IncludeNetwork.IsChecked == true,
            Context = context
        };

        _cts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        RunButton.Content = Loc.T("Идёт замер…");
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = Loc.T("Подготовка…");
        var progress = new Progress<string>(step => ProgressText.Text = step + "…");

        try
        {
            var result = await Task.Run(() => BenchmarkRunner.Run(options, progress, _cts.Token));
            result.Label = "";
            _store.Save(result);
            LoadHistory(result.Id);
            (Window.GetWindow(this) as MainWindow)?.SetStatus(Loc.F("Замер готов за {0:0} с", result.DurationSeconds));
        }
        catch (OperationCanceledException)
        {
            (Window.GetWindow(this) as MainWindow)?.SetStatus(Loc.T("Замер остановлен"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Замер не удался: {0}", ex.Message), "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            RunButton.Content = Loc.T("Запустить замер");
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// Фактическая частота опроса мыши через Raw Input. Результат попадает в отчёт скана,
    /// и раздел «Обзор» покажет находку, если частота низкая.
    /// </summary>
    private async void Mouse_Click(object sender, RoutedEventArgs e)
    {
        MouseButton.IsEnabled = false;
        MouseButton.Content = Loc.T("Води мышью…");
        MouseResult.Visibility = Visibility.Visible;
        MouseVerdict.Visibility = Visibility.Collapsed;
        MouseResult.Text = Loc.T("Води мышью быстрыми кругами, не останавливаясь, 5 секунд…");

        var result = await Task.Run(() => new MousePollingMeter().Measure(TimeSpan.FromSeconds(5),
            fraction => Dispatcher.BeginInvoke(() =>
                MouseResult.Text = Loc.F("Води мышью быстрыми кругами, не останавливаясь… {0:0}%", fraction * 100))));

        MouseButton.IsEnabled = true;
        MouseButton.Content = Loc.T("Замерить ещё раз");

        if (result.Samples == 0 || result.NormalizedHz == 0)
        {
            MouseResult.Text = Loc.T("Движения не было");
            MouseVerdict.Text = Loc.T("Мышь не сообщала о движении. Повтори и всё время води мышью. Тачпад ноутбука так не измерить.");
            MouseVerdict.Visibility = Visibility.Visible;
            return;
        }

        int hz = result.NormalizedHz;
        MouseResult.Text = Loc.F("{0} Гц", hz) + (result.Reliable ? "" : Loc.T(" — мало данных, повтори с более быстрыми движениями"));
        MouseVerdict.Text = hz switch
        {
            <= 125 => Loc.T("125 Гц — значение USB по умолчанию: между отчётами 8 мс, курсор идёт ступеньками. ") +
                      Loc.T("Поставь 1000 Гц в программе мыши и подключи её в задний порт платы, не в хаб и не в монитор."),
            <= 500 => Loc.T("Для игровой мыши мало. Проверь частоту в программе мыши, порт подключения и энергосбережение USB (раздел «Оптимизация»)."),
            < 1500 => Loc.T("Норма для игр: отчёт каждую миллисекунду."),
            _ => Loc.T("Высокая частота. Она снижает задержку на доли миллисекунды, но нагружает одно ядро процессора. ") +
                 Loc.T("Если в играх появились статтеры — снизь до 1000–2000 Гц.")
        } + Loc.F(" Замер: {0} Гц по {1} отчётам.", result.Hz, result.Samples);
        MouseVerdict.Visibility = Visibility.Visible;

        if (AppState.Current.Report is { } report)
        {
            report.Input.MeasuredPollingHz = hz;
            AppState.Current.RefreshFindings();
        }
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenGuide("diag.benchmarks");
}
