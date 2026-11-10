using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Velocity.Core;
using Velocity.Hardware;
using Velocity.Tweaks;
using Velocity.Tweaks.Games;
using Velocity.Tweaks.Guides;

namespace Velocity.App;

/// <summary>
/// Общее состояние сессии: отчёт о системе, каталог твиков, профили игр.
/// Сканирование дорогое, поэтому результат живёт здесь, а не пересчитывается
/// при каждом переключении раздела.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppState : INotifyPropertyChanged
{
    public static AppState Current { get; } = new();

    private AppState() { }

    public TweakCatalog Catalog { get; private set; } = TweakCatalog.Load();
    public GameProfileEngine GameProfiles { get; private set; } = GameProfileEngine.Load();

    private GuideCatalog? _guides;
    /// <summary>Руководства плюс ручные пункты каталога твиков.</summary>
    public GuideCatalog Guides => _guides ??= GuideCatalog.Load(Catalog);

    private HardwareReport? _report;
    public HardwareReport? Report
    {
        get => _report;
        private set { _report = value; Notify(); Notify(nameof(HasReport)); }
    }

    public bool HasReport => _report is not null;

    public SystemFacts? Facts { get; private set; }
    public TweakEngine? Engine { get; private set; }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; Notify(); }
    }

    /// <summary>Срабатывает после завершения скана — разделы обновляют себя по нему.</summary>
    public event Action? ReportChanged;

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;

        try
        {
            var report = await Task.Run(() =>
            {
                var scanner = new HardwareScanner();
                var result = scanner.Scan();
                result.Findings = FindingsEngine.Analyze(result);
                result.Score = Scoring.Compute(result.Findings);
                return result;
            });

            Facts = SystemFacts.From(report);
            Engine = new TweakEngine(Facts);
            Report = report;
            ReportChanged?.Invoke();
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Пересчитывает находки и оценку по уже собранному отчёту.
    /// Нужен после применения твиков: полный скан ради этого гонять незачем.
    /// </summary>
    public void RefreshFindings()
    {
        if (_report is null) return;

        _report.Findings = FindingsEngine.Analyze(_report);
        _report.Score = Scoring.Compute(_report.Findings);
        ReportChanged?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
