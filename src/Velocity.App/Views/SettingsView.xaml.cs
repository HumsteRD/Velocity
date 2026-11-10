using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Velocity.Tweaks;
using Velocity.Tweaks.Actions;

namespace Velocity.App.Views;

[SupportedOSPlatform("windows")]
public partial class SettingsView : UserControl
{
    private static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity");

    private bool _ready;

    public SettingsView()
    {
        InitializeComponent();
        BuildLookControls();
        Loaded += (_, _) =>
        {
            Render();
            _ready = true;
        };
    }

    // ───────────────────────────── Оформление ─────────────────────────────

    private void BuildLookControls()
    {
        var look = ThemeManager.Current;
        ThemeList.ItemsSource = ThemeManager.Themes;
        FromFiButton.Visibility = File.Exists(ThemeManager.FiLookFile) ? Visibility.Visible : Visibility.Collapsed;

        AccentPanel.Children.Clear();
        AccentPanel.Children.Add(Chip(Loc.T("Как в теме"), "LookAccent", look.Accent is null, () => ChangeLook(look with { Accent = null })));
        foreach (var hex in ThemeManager.Accents)
        {
            var swatch = new RadioButton
            {
                GroupName = "LookAccent",
                Style = (Style)FindResource("Swatch"),
                Background = ThemeManager.Frozen((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                IsChecked = string.Equals(look.Accent, hex, StringComparison.OrdinalIgnoreCase),
                ToolTip = hex
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, hex);
            swatch.Checked += (_, _) => ChangeLook(ThemeManager.Current with { Accent = hex });
            AccentPanel.Children.Add(swatch);
        }
        CustomAccent.Text = look.Accent ?? "";

        FontPanel.Children.Clear();
        foreach (var (id, name, family) in ThemeManager.Fonts)
        {
            var chip = Chip(name, "LookFont", (look.Font ?? "") == id,
                () => ChangeLook(ThemeManager.Current with { Font = id == "" ? null : id }));
            // Каждый вариант написан своим шрифтом — так виден выбор до применения.
            if (family != "") chip.FontFamily = new System.Windows.Media.FontFamily(family);
            FontPanel.Children.Add(chip);
        }

        RadiusPanel.Children.Clear();
        foreach (var (id, name) in ThemeManager.Radii)
            RadiusPanel.Children.Add(Chip(name, "LookRadius", (look.Radius ?? "") == id,
                () => ChangeLook(ThemeManager.Current with { Radius = id == "" ? null : id }), segment: true));

        ScalePanel.Children.Clear();
        foreach (var (value, name) in ThemeManager.Scales)
            ScalePanel.Children.Add(Chip(name, "LookScale", look.Scale == value || (value == 0 && look.ScaleFactor == 1),
                () => ChangeLook(ThemeManager.Current with { Scale = value }), segment: true));

        ButtonStylePanel.Children.Clear();
        foreach (var (id, name) in ThemeManager.ButtonStyles)
            ButtonStylePanel.Children.Add(Chip(name, "LookButtonStyle", (look.ButtonStyle ?? "") == id,
                () => ChangeLook(ThemeManager.Current with { ButtonStyle = id == "" ? null : id }), segment: true));

        ButtonColorPanel.Children.Clear();
        ButtonColorPanel.Children.Add(Chip(Loc.T("Как акцент"), "LookButtonColor", look.ButtonColor is null,
            () => ChangeLook(ThemeManager.Current with { ButtonColor = null })));
        foreach (var hex in ThemeManager.Accents)
            ButtonColorPanel.Children.Add(Swatch(hex, "LookButtonColor", look.ButtonColor,
                () => ChangeLook(ThemeManager.Current with { ButtonColor = hex })));
        CustomButtonColor.Text = look.ButtonColor ?? "";

        BackgroundPanel.Children.Clear();
        BackgroundPanel.Children.Add(Chip(Loc.T("Как в теме"), "LookBackground", look.Background is null,
            () => ChangeLook(ThemeManager.Current with { Background = null })));
        foreach (var hex in Backgrounds)
            BackgroundPanel.Children.Add(Swatch(hex, "LookBackground", look.Background,
                () => ChangeLook(ThemeManager.Current with { Background = hex })));
        CustomBackground.Text = look.Background ?? "";

        AnimPanel.Children.Clear();
        foreach (var (id, name) in ThemeManager.Animations)
            AnimPanel.Children.Add(Chip(name, "LookAnim", (look.BackgroundAnim ?? "") == id,
                () => ChangeLook(ThemeManager.Current with { BackgroundAnim = id == "" ? null : id })));

        ClickPanel.Children.Clear();
        foreach (var (id, name) in ThemeManager.ClickEffects)
            ClickPanel.Children.Add(Chip(name, "LookClick", (look.ClickEffect ?? "") == id,
                () => ChangeLook(ThemeManager.Current with { ClickEffect = id == "" ? null : id }), segment: true));

        ShadowPanel.Children.Clear();
        foreach (var (id, name) in ThemeManager.ShadowLevels)
            ShadowPanel.Children.Add(Chip(name, "LookShadow", (look.Shadows ?? "") == id,
                () => ChangeLook(ThemeManager.Current with { Shadows = id == "" ? null : id }), segment: true));

        CardPanel.Children.Clear();
        foreach (var (value, name) in ThemeManager.CardLevels)
            CardPanel.Children.Add(Chip(name, "LookCard", look.CardTransparency == value,
                () => ChangeLook(ThemeManager.Current with { CardTransparency = value }), segment: true));

        MotionToggle.IsChecked = look.Motion;
        BuildWindowControls();
    }

    /// <summary>Готовые фоны: графит, уголь, полночь, глубокий синий, тёплый тёмный, светлый.</summary>
    private static readonly string[] Backgrounds = ["#0E1013", "#121212", "#0B0F1A", "#0A1622", "#14110F", "#F4F5F7"];

    private RadioButton Swatch(string hex, string group, string? current, Action onSelect)
    {
        var swatch = new RadioButton
        {
            GroupName = group,
            Style = (Style)FindResource("Swatch"),
            Background = ThemeManager.Frozen((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
            IsChecked = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase),
            ToolTip = hex
        };
        System.Windows.Automation.AutomationProperties.SetName(swatch, hex);
        swatch.Checked += (_, _) => onSelect();
        return swatch;
    }

    private static string? ReadHex(TextBox box, string title)
    {
        var text = box.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (ThemeManager.TryParseHex(text, out var color)) return ThemeManager.Hex(color);
        MessageBox.Show(Loc.T("Нужен цвет в формате #RRGGBB, например #FF5FA8."), title,
            MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void CustomButtonColor_Click(object sender, RoutedEventArgs e)
    {
        if (ReadHex(CustomButtonColor, Loc.T("Цвет кнопок")) is { } hex) ChangeLook(ThemeManager.Current with { ButtonColor = hex });
    }

    private void CustomBackground_Click(object sender, RoutedEventArgs e)
    {
        if (ReadHex(CustomBackground, Loc.T("Цвет фона")) is { } hex) ChangeLook(ThemeManager.Current with { Background = hex });
    }

    private RadioButton Chip(string text, string group, bool isChecked, Action onSelect, bool segment = false)
    {
        var chip = new RadioButton
        {
            Content = text,
            GroupName = group,
            IsChecked = isChecked,
            Style = (Style)FindResource(segment ? "Segment" : "Chip")
        };
        // Обработчик вешаем после IsChecked: начальная отметка не должна запускать смену оформления.
        chip.Checked += (_, _) => onSelect();
        return chip;
    }

    /// <summary>
    /// Смена оформления. Откладываем до конца обработки клика: главное окно
    /// пересоздаётся, и этот экран вместе с ним.
    /// </summary>
    private void ChangeLook(Look look)
    {
        if (look == ThemeManager.Current) return;
        Dispatcher.BeginInvoke(() => ((App)Application.Current).ApplyLook(look, "Settings"),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id }) ChangeLook(ThemeManager.Current with { Theme = id });
    }

    private void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        var text = CustomAccent.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (!ThemeManager.TryParseHex(text, out var color))
        {
            MessageBox.Show(Loc.T("Нужен цвет в формате #RRGGBB, например #FF5FA8."), Loc.T("Свой акцент"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ChangeLook(ThemeManager.Current with { Accent = ThemeManager.Hex(color) });
    }

    private void FromFi_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeManager.ReadFiLook() is { } fi) ChangeLook(fi);
        else MessageBox.Show(Loc.T("Не удалось прочитать оформление FI."), "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ───────────────────────────── Окно и язык ─────────────────────────────

    private static readonly (int Value, string Name)[] TransparencyLevels =
        [(0, "Нет"), (15, "Лёгкая"), (30, "Средняя"), (45, "Сильная")];

    private void BuildWindowControls()
    {
        var look = ThemeManager.Current;
        var settings = AppSettings.Current;

        LanguagePanel.Children.Clear();
        foreach (var (id, name) in Loc.Languages)
            LanguagePanel.Children.Add(Chip(name, "Language", Loc.Language == id, () => ChangeLanguage(id), segment: true));

        TransparencyPanel.Children.Clear();
        foreach (var (value, name) in TransparencyLevels)
            TransparencyPanel.Children.Add(Chip(Loc.T(name), "Transparency", look.Transparency == value,
                () => ChangeLook(ThemeManager.Current with { Transparency = value }), segment: true));

        BlurToggle.IsChecked = look.Blur;
        BlurToggle.IsEnabled = look.Transparency > 0;
        TopmostToggle.IsChecked = settings.Topmost;
        AutostartToggle.IsChecked = Autostart.IsEnabled;
        TrayToggle.IsChecked = settings.CloseToTray;
    }

    private void ChangeLanguage(string id)
    {
        if (id == Loc.Language) return;
        AppSettings.Current.Language = id;
        AppSettings.Current.Save();

        // Спрашиваем на новом языке: человек его только что выбрал.
        Loc.Load(id);
        var answer = MessageBox.Show(Loc.T("Чтобы сменить язык, программа перезапустится. Перезапустить сейчас?"),
            "VELOCITY", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) ((App)Application.Current).Restart();
    }

    private void Blur_Click(object sender, RoutedEventArgs e)
        => ChangeLook(ThemeManager.Current with { Blur = BlurToggle.IsChecked == true });

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.Topmost = TopmostToggle.IsChecked == true;
        AppSettings.Current.Save();
        if (Window.GetWindow(this) is { } window) window.Topmost = AppSettings.Current.Topmost;
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartToggle.IsChecked == true); }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось изменить автозапуск: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AutostartToggle.IsChecked = Autostart.IsEnabled;
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CloseToTray = TrayToggle.IsChecked == true;
        AppSettings.Current.Save();
        if (AppSettings.Current.CloseToTray) Tray.Show(); else Tray.Hide();
    }

    private void Motion_Click(object sender, RoutedEventArgs e)
        => ChangeLook(ThemeManager.Current with { Motion = MotionToggle.IsChecked == true });

    private void ResetLook_Click(object sender, RoutedEventArgs e) => ChangeLook(new Look(ThemeManager.DefaultTheme));

    private void Render()
    {
        var state = AppState.Current;
        var settings = AppSettings.Current;

        CheckUpdatesToggle.IsChecked = settings.CheckUpdates;
        AutoDownloadToggle.IsChecked = settings.AutoDownloadUpdates;

        VersionLine.Text = Loc.F("Установлена версия {0}", Updater.CurrentVersion);
        UpdateStatus.Text = settings.LastUpdateCheck is { } when
            ? Loc.F("Последняя проверка: {0:d MMMM, HH:mm}", when.LocalDateTime)
            : Loc.T("Обновления ещё не проверялись");

        AboutList.ItemsSource = new List<SpecRow>
        {
            new(Loc.T("Версия"), Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                ?? "0.2.0"),
            new(Loc.T("Права администратора"), TweakEngine.IsElevated() ? Loc.T("есть") : Loc.T("нет")),
            new(Loc.T("Драйвер NVIDIA (NVAPI)"), NvidiaExecutor.IsAvailable ? Loc.T("доступен") : Loc.T("не обнаружен")),
            new(Loc.T("Активные античиты"), TweakEngine.DetectActiveAntiCheats() is { Count: > 0 } ac
                ? string.Join(", ", ac)
                : Loc.T("не обнаружены"))
        };

        DataList.ItemsSource = new List<SpecRow>
        {
            new(Loc.T("Папка данных"), DataFolder),
            new(Loc.T("Точек отката"), new SnapshotStore().List().Count.ToString())
        };

        CatalogList.ItemsSource = new List<SpecRow>
        {
            new(Loc.T("Настроек в каталоге"), state.Catalog.All.Count.ToString()),
            new(Loc.T("Подходит этой системе"), state.Facts is null
                ? "—"
                : state.Catalog.Applicable(state.Facts).Count.ToString()),
            new(Loc.T("Профилей игр"), state.GameProfiles.Profiles.Count.ToString())
        };

        ErrorList.ItemsSource = state.Catalog.LoadErrors.Concat(state.GameProfiles.LoadErrors).ToList();
    }

    /// <summary>
    /// Переключатели срабатывают и при первичной установке значений в Render,
    /// поэтому до конца загрузки настройки не сохраняем — иначе затрём их сами собой.
    /// </summary>
    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var settings = AppSettings.Current;
        settings.CheckUpdates = CheckUpdatesToggle.IsChecked == true;
        settings.AutoDownloadUpdates = AutoDownloadToggle.IsChecked == true;
        settings.Save();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        CheckNowButton.Content = Loc.T("Проверяю…");
        UpdateStatus.Text = Loc.T("Связываюсь с GitHub…");

        var update = await Updater.CheckAsync();

        AppSettings.Current.LastUpdateCheck = DateTimeOffset.Now;
        AppSettings.Current.Save();

        CheckNowButton.Content = Loc.T("Проверить сейчас");
        CheckNowButton.IsEnabled = true;

        if (update is null)
        {
            UpdateStatus.Text = Loc.F("Установлена актуальная версия. Проверено {0:HH:mm}", DateTime.Now);
            return;
        }

        UpdateStatus.Text = Loc.F("Доступна версия {0}", update.Version);
        await UpdateFlow.OfferAsync(update, Window.GetWindow(this));
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось открыть папку: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var report = AppState.Current.Report;
        if (report is null)
        {
            MessageBox.Show(Loc.T("Отчёт ещё не готов — дождись окончания скана."), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Loc.F("velocity-отчёт-{0:yyyy-MM-dd-HHmm}.json", DateTime.Now),
            Filter = Loc.T("Файл JSON|*.json"),
            Title = Loc.T("Сохранить отчёт о системе")
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new JsonStringEnumConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, options));

            if (Window.GetWindow(this) is MainWindow main) main.SetStatus(Loc.T("Отчёт сохранён"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось сохранить: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
