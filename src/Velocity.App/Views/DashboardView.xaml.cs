using Velocity.Tweaks;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velocity.Core;

namespace Velocity.App.Views;

public sealed record SpecRow(string Label, string Value);

[SupportedOSPlatform("windows")]
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();

        // Подписку держим на паре Loaded/Unloaded, а не на конструкторе:
        // представления кэшируются в MainWindow, и после первого ухода со вкладки
        // подписка из конструктора терялась навсегда.
        Loaded += (_, _) => AppState.Current.ReportChanged += Refresh;
        Unloaded += (_, _) => AppState.Current.ReportChanged -= Refresh;

        if (AppState.Current.HasReport) Refresh();
    }

    private void Refresh()
    {
        var report = AppState.Current.Report;
        if (report is null) return;

        Dispatcher.Invoke(() =>
        {
            RenderScore(report);
            RenderSpecs(report);
            RenderFindings(report);
        });
    }

    private void RenderScore(HardwareReport report)
    {
        ScoreValue.Text = report.Score.ToString();
        ScoreLabel.Text = Loc.T(Scoring.Label(report.Score));

        var brush = (Brush)FindResource(report.Score switch
        {
            >= 90 => "Accent",
            >= 75 => "Success",
            >= 55 => "Warning",
            _ => "Critical"
        });

        ScoreValue.Foreground = brush;
        ScoreLabel.Foreground = brush;
        ScoreBar.Background = brush;

        // Ширину шкалы считаем от фактической ширины контейнера, а не от жёсткого числа:
        // окно резиновое, и полоса обязана тянуться вместе с ним.
        ScoreBar.Loaded += (_, _) => UpdateBarWidth(report.Score);
        if (ScoreBar.Parent is FrameworkElement parent)
        {
            parent.SizeChanged += (_, _) => UpdateBarWidth(report.Score);
            UpdateBarWidth(report.Score);
        }

        var actionable = report.Findings.Count(f => f.Severity != FindingSeverity.Info);
        ProblemCount.Text = actionable.ToString();
        FixableCount.Text = report.Findings.Count(f => f.AutoFixable).ToString();
        GameCount.Text = report.Games.Count.ToString();

        SubtitleText.Text = $"{report.Cpu.Name} · {report.Gpus.FirstOrDefault()?.Name} · " +
                            Loc.F("скан {0} мс", report.ScanDurationMs);
    }

    private void UpdateBarWidth(int score)
    {
        if (ScoreBar.Parent is not FrameworkElement parent || parent.ActualWidth <= 0) return;
        ScoreBar.Width = parent.ActualWidth * score / 100.0;
    }

    private void RenderSpecs(HardwareReport report)
    {
        var rows = new List<SpecRow>();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new SpecRow(label, value));
        }

        Add(Loc.T("Процессор"), report.Cpu.Name is { } cpu
            ? $"{cpu}  ·  {report.Cpu.PhysicalCores}C/{report.Cpu.LogicalCores}T" +
              (report.Cpu.IsHybrid ? $"  ·  {report.Cpu.PerformanceCores}P + {report.Cpu.EfficiencyCores}E" : "") +
              (report.Cpu.IsAsymmetricCache ? "  ·  X3D" : "")
            : null);

        var gpu = report.Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? report.Gpus.FirstOrDefault();
        Add(Loc.T("Видеокарта"), gpu is not null
            ? $"{gpu.Name}" + (gpu.VramGb > 0 ? Loc.F("  ·  {0} ГБ", gpu.VramGb) : "") +
              (gpu.VendorDriverVersion is { } v ? Loc.F("  ·  драйвер {0}", v) : "")
            : null);

        Add(Loc.T("Память"), report.Memory.TotalGb > 0
            ? Loc.F("{0} ГБ {1} @ {2} МГц", report.Memory.TotalGb, PrettyMemory(report.Memory.Kind), report.Memory.ConfiguredSpeedMhz) +
              (report.Memory.Channels > 0 ? Loc.F("  ·  {0}-канал", report.Memory.Channels) : "")
            : null);

        var system = report.Storage.FirstOrDefault(s => s.IsSystemDrive);
        Add(Loc.T("Системный диск"), system is not null
            ? Loc.F("{0}  ·  {1}  ·  свободно {2} ГБ из {3}", system.Model, PrettyStorage(system.Kind), system.FreeGb, system.SizeGb)
            : null);

        foreach (var d in report.Displays)
        {
            Add(d.IsPrimary ? Loc.T("Монитор") : Loc.T("Монитор 2"),
                Loc.F("{0}  ·  {1}×{2} @ {3} Гц", d.FriendlyName, d.Width, d.Height, d.RefreshHz) +
                (d.MaxRefreshAtCurrentResolution > d.RefreshHz + 1
                    ? Loc.F("  ·  доступно {0} Гц", d.MaxRefreshAtCurrentResolution)
                    : ""));
        }

        Add(Loc.T("Материнская плата"), $"{report.Machine.BoardManufacturer} {report.Machine.BoardProduct}".Trim());
        Add("Windows", Loc.F("{0} {1}  ·  сборка {2}", report.Os.ProductName, report.Os.DisplayVersion, report.Os.FullBuild));
        Add(Loc.T("Схема питания"), report.Tuning.PowerPlanName);
        Add(Loc.T("Игр установлено"), report.Games.Count > 0 ? Loc.Plural(report.Games.Count, "игра", "игры", "игр") : null);

        SpecList.ItemsSource = rows;
    }

    private void RenderFindings(HardwareReport report)
    {
        // Информационные находки на обзорном экране не показываем: они разбавляют
        // список тем, по чему не нужно принимать решений. Их место — раздел «Оптимизация».
        var actionable = report.Findings.Where(f => f.Severity != FindingSeverity.Info).ToList();

        FindingsList.ItemsSource = actionable;
        FindingsList.Visibility = actionable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FindingsHeader.Visibility = actionable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanState.Visibility = actionable.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string PrettyMemory(MemoryKind kind) => kind switch
    {
        MemoryKind.Ddr3 => "DDR3",
        MemoryKind.Ddr4 => "DDR4",
        MemoryKind.Ddr5 => "DDR5",
        MemoryKind.Lpddr4 => "LPDDR4",
        MemoryKind.Lpddr5 => "LPDDR5",
        _ => ""
    };

    private static string PrettyStorage(StorageKind kind) => kind switch
    {
        StorageKind.Hdd => Loc.T("жёсткий диск"),
        StorageKind.Ssd => "SSD",
        StorageKind.Nvme => "NVMe SSD",
        _ => Loc.T("накопитель")
    };

    /// <summary>
    /// Исправляет находку на месте. Так отчёт становится инструментом:
    /// увидел проблему — нажал — починил, без похода в другой раздел.
    /// </summary>
    /// <summary>Свечение не должно вылезать за скруглённые углы карточки.</summary>
    private void Hero_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = (Application.Current.Resources["RadiusLg"] as CornerRadius?)?.TopLeft ?? 12;
        HeroGrid.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tweakId } button) return;

        var state = AppState.Current;
        if (state.Engine is null) return;

        var tweak = state.Catalog.ById(tweakId);
        if (tweak is null)
        {
            MessageBox.Show(Loc.T("Настройка не найдена в каталоге."), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (tweak.RequiresElevation && !Elevation.IsElevated)
        {
            if (Elevation.RequestRestart(Loc.F("Настройке «{0}» нужны права администратора.", tweak.Title))) return;
            return;
        }

        button.IsEnabled = false;
        button.Content = Loc.T("Применяю…");

        var session = await Task.Run(() => state.Engine.Apply([tweak],
            new TweakEngine.ApplyOptions { Reason = Loc.F("Исправление: {0}", tweak.Title) }));

        button.Content = Loc.T("Исправить");
        button.IsEnabled = true;

        if (session.AbortReason is { } abort)
        {
            MessageBox.Show(abort, Loc.T("Не применено"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = session.Results.FirstOrDefault();
        if (result?.Outcome == ApplyOutcome.Applied)
        {
            var text = Loc.T("Готово.") + (session.RebootRequired
                ? Loc.T(" Изменение вступит в силу после перезагрузки.")
                : "");
            if (Window.GetWindow(this) is MainWindow main) main.SetStatus(text);

            // Пересканируем: находка должна исчезнуть из списка, а оценка — вырасти.
            await AppState.Current.ScanAsync();
        }
        else
        {
            MessageBox.Show(Loc.F("Не удалось: {0}", result?.Message ?? Loc.T("неизвестная причина")),
                tweak.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        RescanButton.Content = Loc.T("Сканирую…");

        await AppState.Current.ScanAsync();

        RescanButton.Content = Loc.T("Пересканировать");
        RescanButton.IsEnabled = true;
    }

    private void OpenSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (Window.GetWindow(this) is MainWindow main) main.NavigateTo(key);
    }
}
