using System.Reflection;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Velocity.Tweaks;

namespace Velocity.App.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = [];

    private readonly bool _isReload;

    public MainWindow() : this(null, isReload: false) { }

    /// <param name="startView">Раздел, который открыть сразу. null — из аргументов командной строки.</param>
    /// <param name="isReload">Окно пересоздано при смене темы: скан и проверку обновлений не повторяем.</param>
    public MainWindow(string? startView, bool isReload)
    {
        _isReload = isReload;
        InitializeComponent();
        ApplyScale(ThemeManager.Current.ScaleFactor);
        WindowEffects.Prepare(this, RootFrame, ThemeManager.Current);
        Topmost = AppSettings.Current.Topmost;
        Closing += OnClosing;
        PreviewKeyDown += OnHotkey;

        VersionText.Text = Loc.T("версия ") + (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.2.0");

        AdminBadge.Visibility = TweakEngine.IsElevated() ? Visibility.Visible : Visibility.Collapsed;

        // Раздел можно открыть сразу: Velocity.exe --view Cleanup.
        // Удобно для ярлыка на конкретную функцию и для проверки интерфейса.
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--view");
        var start = startView ?? (index >= 0 && index + 1 < args.Length ? args[index + 1] : "Diagnosis");

        Show(start);
        foreach (var nav in new[] { NavDiagnosis, NavDashboard, NavOptimize, NavPrivacy, NavStartup, NavNetwork, NavCleanup,
                     NavFps, NavGames, NavBenchmark, NavGuides, NavRestore, NavSettings })
            if ((string)nav.Tag == start) nav.IsChecked = true;
        Loaded += (_, _) => StartDecor();
        DecorLayer.SizeChanged += (_, _) => StartDecor();
        ClickEffects.Attach(this, EffectLayer);
        SizeChanged += (_, _) => FitNavigation();

        Loaded += async (_, _) =>
        {
            if (_isReload)
            {
                // Скан мог ещё идти в прежнем окне — тогда ScanAsync сразу вернётся,
                // а разделы обновятся по событию ReportChanged, когда он закончится.
                if (!AppState.Current.HasReport) await AppState.Current.ScanAsync();
                SetStatus(Loc.F("Оформление: {0}", ThemeManager.Find(ThemeManager.Current.Theme).Name));
                return;
            }

            SetStatus(Loc.T("Сканирование системы…"));
            await AppState.Current.ScanAsync();
            SetStatus(Loc.F("Скан занял {0} мс", AppState.Current.Report?.ScanDurationMs));

            // Проверку обновлений делаем после скана: она не должна задерживать
            // первое появление данных на экране.
            await UpdateFlow.CheckOnStartupAsync(this);
        };
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Ctrl+F — поиск на текущем экране, F5 — пересканировать систему.</summary>
    private async void OnHotkey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && ContentHost.Content is ISearchable searchable)
        {
            searchable.FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && !AppState.Current.IsScanning)
        {
            e.Handled = true;
            SetStatus(Loc.T("Сканирование системы…"));
            await AppState.Current.ScanAsync();
            SetStatus(Loc.F("Скан занял {0} мс", AppState.Current.Report?.ScanDurationMs));
        }
    }

    private bool _replacing;

    /// <summary>Закрытие при смене оформления: в трей не прячемся, окно заменяется новым.</summary>
    public void CloseForReplace()
    {
        _replacing = true;
        Close();
    }

    /// <summary>Крестик при включённом «Сворачивать в трей» прячет окно, а не закрывает программу.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_replacing || Tray.ExitRequested || !AppSettings.Current.CloseToTray) return;
        e.Cancel = true;
        Tray.Show();
        Hide();
    }

    /// <summary>Масштаб интерфейса из «Оформления»: всё окно увеличивается целиком, как в FI.</summary>
    private void ApplyScale(double scale)
    {
        if (Math.Abs(scale - 1) < 0.01) return;
        RootFrame.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth * scale, area.Width);
        MinHeight = Math.Min(MinHeight * scale, area.Height);
        Width = Math.Min(Width * scale, area.Width);
        Height = Math.Min(Height * scale, area.Height);
    }

    /// <summary>Переход из экрана, где следующая функция уже очевидна.</summary>
    public void NavigateTo(string key)
    {
        Show(key);
        var nav = key switch
        {
            "Diagnosis" => NavDiagnosis,
            "Fps" => NavFps,
            "Dashboard" => NavDashboard,
            "Optimize" => NavOptimize,
            "Privacy" => NavPrivacy,
            "Startup" => NavStartup,
            "Network" => NavNetwork,
            "Games" => NavGames,
            "Benchmark" => NavBenchmark,
            "Guides" => NavGuides,
            "Cleanup" => NavCleanup,
            "Restore" => NavRestore,
            "Settings" => NavSettings,
            _ => null
        };
        if (nav is not null) nav.IsChecked = true;
    }

    /// <summary>Открыть раздел «Руководства» на конкретном руководстве.</summary>
    public void OpenGuide(string guideId)
    {
        NavigateTo("Guides");
        if (_views.TryGetValue("Guides", out var view) && view is GuidesView guides)
            guides.Select(guideId);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Обработчик срабатывает и при первичной установке IsChecked в разметке,
        // когда контейнера содержимого ещё нет.
        if (ContentHost is null) return;
        if (sender is RadioButton { Tag: string tag }) Show(tag);
    }

    private void Show(string key)
    {
        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "Diagnosis" => new DiagnosisView(),
                "Fps" => new FpsView(),
                "Dashboard" => new DashboardView(),
                "Optimize" => new OptimizeView(),
                "Privacy" => new OptimizeView(
                    TweakCategory.Privacy,
                    Loc.T("Приватность и фон"),
                    Loc.T("Диагностика, рекламный идентификатор и фоновые приложения")),
                "Startup" => new StartupView(),
                "Network" => new NetworkView(),
                "Games" => new GamesView(),
                "Benchmark" => new BenchmarkView(),
                "Guides" => new GuidesView(),
                "Cleanup" => new CleanupView(),
                "Restore" => new RestoreView(),
                "Settings" => new SettingsView(),
                _ => new DashboardView()
            };
            _views[key] = view;
        }

        if (ReferenceEquals(ContentHost.Content, view)) return;
        ContentHost.Content = view;
        AnimateEnter();
    }

    // ─────────────────────── Анимации, как в FI ───────────────────────

    /// <summary>
    /// Появление экрана: подъём на 8 пикселей с проявлением, как .screen-enter в FI.
    /// Выключается вместе с анимациями в «Оформлении» или в настройках Windows.
    /// </summary>
    private void AnimateEnter()
    {
        if (!ThemeManager.Current.AnimationsOn || !IsLoaded) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ContentShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// Лепестки сакуры в теме «Аниме»: семь штук, каждая падает со своей скоростью,
    /// покачиваясь и вращаясь, — те же позиции и длительности, что в FI (.decor i).
    /// </summary>
    /// <summary>Живой фон: перестраивается при изменении размера окна, чтобы волны и частицы занимали его целиком.</summary>
    private void StartDecor()
    {
        // При перетаскивании края окна размер меняется десятки раз в секунду —
        // фон перестраивается один раз, когда размер устоялся.
        _decorTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(180),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { _decorTimer!.Stop(); BackdropAnimator.Start(DecorLayer, ThemeManager.Current); },
            Dispatcher);
        _decorTimer.Stop();
        _decorTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _decorTimer;
    private bool? _compactNav;

    /// <summary>
    /// На низком окне меню сжимается: пункты ниже, подписи групп скрыты. Так все разделы
    /// видны без прокрутки и на ноутбуке с 768 точками по высоте, и при крупном масштабе.
    /// </summary>
    private void FitNavigation()
    {
        var height = ActualHeight / ThemeManager.Current.ScaleFactor;
        var compact = height < 790;
        if (compact == _compactNav) return;
        _compactNav = compact;
        Resources["NavItemHeight"] = compact ? 29.0 : 34.0;
        foreach (var label in NavPanel.Children.OfType<TextBlock>())
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // Без подписей группы разделяет небольшой отступ у первого пункта каждой группы.
        bool afterLabel = false;
        foreach (UIElement child in NavPanel.Children)
        {
            if (child is TextBlock) { afterLabel = true; continue; }
            if (child is RadioButton nav)
            {
                nav.Margin = compact && afterLabel && NavPanel.Children.IndexOf(child) > 1 ? new Thickness(0, 8, 0, 0) : new Thickness(0);
                afterLabel = false;
            }
        }
    }

    private static void Run(Animatable target, DependencyProperty property, AnimationTimeline animation, TimeSpan offset)
    {
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        clock.Controller?.Seek(offset, TimeSeekOrigin.BeginTime);
    }

    // ─────────────────────── Управление окном ───────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        // Символы «развернуть» и «восстановить» в шрифте Segoe Fluent Icons разные.
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }
}
