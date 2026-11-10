using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velocity.Core;
using Velocity.Hardware.Cleanup;

namespace Velocity.App.Views;

/// <summary>Строка легенды диаграммы занятости диска.</summary>
public sealed class UsageSlice
{
    public required string Name { get; init; }
    public string? Hint { get; init; }
    public required long SizeBytes { get; init; }
    public required double Percent { get; init; }
    public required Brush Color { get; init; }

    public string SizeText => Format.Size(SizeBytes);
    public string PercentText => $"{Percent:0.#}%";
}

/// <summary>Категория мусора с отметкой выбора и раскрытием подробностей.</summary>
public sealed class JunkItem : INotifyPropertyChanged
{
    public required CleanupFinding Finding { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Notify(); }
    }

    public string Title => Finding.Target.Title;
    public string Description => Finding.Target.Description;
    public string Consequence => Finding.Target.Consequence ?? Loc.T("Ничего существенного.");
    public string SizeText => Finding.SizeText;
    public List<string> Locations => [.. Finding.Locations.Select(l => $"{l.Path}  —  {Format.Size(l.Size)}")];

    public string LocationSummary => Finding.Locations.Count == 1
        ? Finding.Locations[0].Path
        : $"{Loc.Plural(Finding.Locations.Count, Loc.T("папка"), Loc.T("папки"), Loc.T("папок"))} · " +
          $"{Loc.Plural(Finding.FileCount, Loc.T("файл"), Loc.T("файла"), Loc.T("файлов"))}";

    public string SafetyLabel => Finding.Target.Safety switch
    {
        CleanupSafety.Safe => Loc.T("Безопасно"),
        CleanupSafety.Regenerates => Loc.T("Создастся заново"),
        _ => Loc.T("Без возврата")
    };

    public Brush SafetyBrush => (Brush)Application.Current.Resources[Finding.Target.Safety switch
    {
        CleanupSafety.Safe => "Success",
        CleanupSafety.Regenerates => "Info",
        _ => "Critical"
    }];

    public Brush SafetyBackground => (Brush)Application.Current.Resources[Finding.Target.Safety switch
    {
        CleanupSafety.Safe => "BgElevated",
        CleanupSafety.Regenerates => "InfoDim",
        _ => "CriticalDim"
    }];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => Loc.F("{0:0.##} ГБ", bytes / 1024d / 1024d / 1024d),
        >= 1024L * 1024 => Loc.F("{0:0.#} МБ", bytes / 1024d / 1024d),
        >= 1024 => Loc.F("{0} КБ", bytes / 1024),
        _ => Loc.F("{0} Б", bytes)
    };
}

[SupportedOSPlatform("windows")]
public partial class CleanupView : UserControl
{
    private readonly ObservableCollection<JunkItem> _junk = [];
    private CancellationTokenSource? _scanCancel;
    private bool _busy;

    /// <summary>Палитра диаграммы: один акцент и оттенки серого, чтобы не рябило.</summary>
    private static readonly string[] SliceColors =
        ["#28E0B0", "#5B9BE8", "#E8A33D", "#A78BFA", "#F2555F", "#5FBF8F", "#7A8496", "#4A5262"];

    public CleanupView()
    {
        InitializeComponent();
        JunkList.ItemsSource = _junk;

        foreach (var drive in DiskUsageScanner.AvailableDrives()) DriveSelector.Items.Add(drive);
        if (DriveSelector.Items.Count > 0) DriveSelector.SelectedIndex = 0;
    }

    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Смена диска обесценивает показанную картину — убираем её, чтобы
        // пользователь не принял данные с одного диска за данные другого.
        if (UsagePanel is not null) UsagePanel.Visibility = Visibility.Collapsed;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _scanCancel?.Cancel();
            return;
        }

        var drive = DriveSelector.SelectedItem as string ?? "C:\\";

        _busy = true;
        _scanCancel = new CancellationTokenSource();
        ScanButton.Content = Loc.T("Остановить");
        SubtitleText.Text = Loc.T("Считаю размеры папок…");

        var progress = new Progress<string>(name => SubtitleText.Text = Loc.F("Считаю: {0}", name));

        try
        {
            var games = AppState.Current.Report?.Games ?? [];

            var (usage, junk) = await Task.Run(() =>
            {
                var u = new DiskUsageScanner().Scan(drive, games, progress, _scanCancel.Token);
                // Мусор — только на выбранном диске: иначе после скана D: программа
                // предлагала бы удалить кэши с C:, и было бы непонятно, что где освободится.
                var j = new CleanupScanner().Scan(driveRoot: drive);
                return (u, j);
            }, _scanCancel.Token);

            RenderUsage(usage);
            RenderJunk(junk, drive);
        }
        catch (OperationCanceledException)
        {
            SubtitleText.Text = Loc.T("Сканирование остановлено");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось просканировать диск: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            ScanButton.Content = Loc.T("Сканировать");
            _scanCancel?.Dispose();
            _scanCancel = null;
        }
    }

    /// <summary>Переключиться на системный диск и сразу просканировать его.</summary>
    private void SystemDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string system }) return;
        var match = DriveSelector.Items.Cast<object>().FirstOrDefault(d => string.Equals(d as string, system, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;
        DriveSelector.SelectedItem = match;
        Scan_Click(sender, e);
    }

    private void RenderUsage(DiskUsageReport usage)
    {
        var slices = new List<UsageSlice>();
        int colorIndex = 0;

        foreach (var entry in usage.Entries.Take(7))
        {
            slices.Add(new UsageSlice
            {
                Name = entry.Name,
                Hint = entry.IsGames ? Loc.T("здесь установлены игры") : null,
                SizeBytes = entry.SizeBytes,
                Percent = entry.Percent,
                Color = Brush(SliceColors[colorIndex++ % SliceColors.Length])
            });
        }

        // Остальные папки сворачиваем в одну строку, иначе легенда вырастает на экран.
        var rest = usage.Entries.Skip(7).Sum(e => e.SizeBytes) + usage.UnaccountedBytes;
        if (rest > 0)
        {
            slices.Add(new UsageSlice
            {
                Name = Loc.T("Остальное"),
                Hint = Loc.T("мелкие папки и защищённые системные каталоги"),
                SizeBytes = rest,
                Percent = usage.UsedBytes > 0 ? rest * 100.0 / usage.UsedBytes : 0,
                Color = Brush("#3A4250")
            });
        }

        UsageLegend.ItemsSource = slices;
        BuildBar(slices);

        UsageSummary.Text = Loc.F("занято {0} ГБ из {1} · ", usage.UsedGb, usage.TotalGb) +
                            Loc.F("свободно {0} ГБ ({1:0}%)", usage.FreeGb, usage.FreePercent);
        UsagePanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Собирает полосу из сегментов. Ширины задаются звёздочными долями Grid,
    /// поэтому диаграмма тянется вместе с окном без пересчёта в коде.
    /// </summary>
    private void BuildBar(List<UsageSlice> slices)
    {
        var host = new Grid();

        foreach (var slice in slices)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(slice.Percent, 0.4), GridUnitType.Star)
            });

            var segment = new Border
            {
                Background = slice.Color,
                ToolTip = $"{slice.Name} — {slice.SizeText} ({slice.PercentText})"
            };

            Grid.SetColumn(segment, host.ColumnDefinitions.Count - 1);
            host.Children.Add(segment);
        }

        UsageBar.ItemsSource = null;
        UsageBar.Items.Clear();
        UsageBar.Items.Add(host);
    }

    private void RenderJunk(List<CleanupFinding> findings, string drive)
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        bool otherDrive = !string.Equals(drive, system, StringComparison.OrdinalIgnoreCase);
        EmptyTitle.Text = otherDrive ? Loc.F("На диске {0} мусора нет", drive.TrimEnd('\\')) : Loc.T("Мусора не найдено");
        EmptyText.Text = otherDrive
            ? Loc.F("Временные файлы, кэши драйверов, браузеров и лаунчеров Windows хранит на системном диске {0}. Выбери его, чтобы их найти.", system.TrimEnd('\\'))
            : Loc.T("Временные файлы, кэши драйверов и остатки обновлений либо отсутствуют, либо занимают незначительное место.");
        SystemDriveButton.Visibility = otherDrive ? Visibility.Visible : Visibility.Collapsed;
        SystemDriveButton.Tag = system;

        _junk.Clear();

        foreach (var finding in findings)
        {
            var item = new JunkItem
            {
                Finding = finding,
                // По умолчанию отмечаем только заведомо безопасное. Всё, что имеет
                // последствия, пользователь должен включить сам и осознанно.
                IsSelected = finding.Target.Safety == CleanupSafety.Safe
            };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(JunkItem.IsSelected)) UpdateSelection();
            };
            _junk.Add(item);
        }

        long total = findings.Sum(f => f.SizeBytes);
        JunkHeader.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = findings.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        SubtitleText.Text = findings.Count > 0
            ? Loc.F("На диске {0} можно освободить до {1}", drive.TrimEnd('\\'), Format.Size(total))
            : EmptyTitle.Text;

        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _junk.Where(j => j.IsSelected).ToList();
        long total = selected.Sum(j => j.Finding.SizeBytes);

        SelectionText.Text = selected.Count == 0
            ? Loc.T("Ничего не выбрано")
            : Loc.F("Выбрано {0} · {1}", Loc.Plural(selected.Count, Loc.T("категория"), Loc.T("категории"), Loc.T("категорий")), Format.Size(total));

        bool risky = selected.Any(j => j.Finding.Target.Safety == CleanupSafety.PointOfNoReturn);
        SelectionHint.Text = risky
            ? Loc.T("Среди выбранного есть то, что нельзя будет вернуть")
            : Loc.T("Удаление необратимо — точки отката на файлы не распространяются");

        CleanButton.IsEnabled = selected.Count > 0;
    }

    private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: JunkItem item })
            item.IsExpanded = !item.IsExpanded;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _junk.Where(j => j.IsSelected).ToList();
        if (selected.Count == 0) return;

        long expected = selected.Sum(j => j.Finding.SizeBytes);
        var needElevation = selected.Where(j => j.Finding.Target.RequiresElevation).ToList();

        if (needElevation.Count > 0 && !Elevation.IsElevated)
        {
            var names = string.Join(Environment.NewLine, needElevation.Select(j => "  • " + j.Title));
            if (Elevation.RequestRestart(
                    Loc.F("Для части категорий нужны права администратора:{0}{1}{2}", Environment.NewLine, Environment.NewLine, names)))
                return;
        }

        var list = string.Join(Environment.NewLine, selected.Select(j => $"  • {j.Title} — {j.SizeText}"));
        var risky = selected.Where(j => j.Finding.Target.Safety == CleanupSafety.PointOfNoReturn).ToList();

        var warning = risky.Count > 0
            ? Environment.NewLine + Environment.NewLine +
              Loc.T("Внимание: ") + string.Join("; ", risky.Select(j => j.Consequence))
            : "";

        var answer = MessageBox.Show(
            Loc.F("Будет удалено:{0}{1}{2}{3}", Environment.NewLine, Environment.NewLine, list, Environment.NewLine) +
            Loc.F("{0}Всего: {1}{2}{3}", Environment.NewLine, Format.Size(expected), warning, Environment.NewLine) +
            Loc.F("{0}Файлы удаляются мимо корзины. Продолжить?", Environment.NewLine),
            Loc.T("Подтверждение очистки"), MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        CleanButton.IsEnabled = false;
        CleanButton.Content = Loc.T("Удаляю…");

        var results = await Task.Run(() =>
        {
            var scanner = new CleanupScanner();
            return selected.Select(j => scanner.Clean(j.Finding)).ToList();
        });

        CleanButton.Content = Loc.T("Удалить выбранное");

        long freed = results.Sum(r => r.FreedBytes);
        int files = results.Sum(r => r.DeletedFiles);
        var errors = results.SelectMany(r => r.Errors).Take(5).ToList();

        var text = Loc.F("Освобождено: {0}{1}", Format.Size(freed), Environment.NewLine) +
                   Loc.F("Удалено файлов: {0}", files);

        if (freed < expected * 0.8)
            text += Environment.NewLine + Environment.NewLine +
                    Loc.T("Часть файлов была занята работающими программами и осталась на месте. ") +
                    Loc.T("Закрой лаунчеры и Discord, затем просканируй снова.");

        if (errors.Count > 0)
            text += Environment.NewLine + Environment.NewLine + Loc.T("Не удалось:") + Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Select(x => "  • " + x));

        MessageBox.Show(text, Loc.T("Очистка завершена"), MessageBoxButton.OK, MessageBoxImage.Information);

        Scan_Click(sender, e);
    }

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
