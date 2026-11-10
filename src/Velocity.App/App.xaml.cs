using System.Threading;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Velocity.App;

public partial class App : Application
{
    /// <summary>
    /// Имя должно совпадать с тем, что проверяет установщик (installer/velocity.iss):
    /// по нему он понимает, что программу нужно закрыть перед обновлением.
    /// Заодно не даём запустить второй экземпляр — две копии наперегонки
    /// применяли бы настройки и писали снимки поверх друг друга.
    /// </summary>
    private const string MutexName = "VelocityAppRunning";
    private static Mutex? _instanceLock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --preview используется только для изолированной проверки новой сборки:
        // не перехватывает окно установленной версии и не меняет её состояние.
        var mutexName = e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase)
            ? MutexName + ".Preview"
            : MutexName;
        _instanceLock = new Mutex(initiallyOwned: true, mutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            FocusExistingWindow();
            Shutdown();
            return;
        }

        // Язык — раньше всего остального: от него зависят надписи во всех экранах.
        Loc.Load(AppSettings.Current.Language);

        // Тема применяется до создания главного окна: экраны берут цвета через
        // StaticResource в момент загрузки разметки.
        try
        {
            // Первый запуск: берём оформление из FI, если он установлен, — приложения выглядят одинаково.
            var settings = AppSettings.Current;
            if (string.IsNullOrEmpty(settings.Theme) && ThemeManager.ReadFiLook() is { } fiLook)
            {
                settings.Look = fiLook;
                settings.Save();
            }
            ThemeManager.Apply(settings.Look);
        }
        catch { /* битое оформление не повод не запускаться — останутся цвета из Tokens.xaml */ }

        // Необработанное исключение в интерфейсе не должно выглядеть как «программа
        // просто исчезла». Показываем внятное окно и продолжаем работу, если можем.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Report(ex, fatal: true);
        };

        // Окно создаём сами, а не через StartupUri: при автозапуске (--tray)
        // программа стартует свёрнутой в трей и окно не показывает.
        var window = new Views.MainWindow();
        MainWindow = window;
        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) Tray.Show();
        else window.Show();
        if (AppSettings.Current.CloseToTray) Tray.Show();
    }


    /// <summary>
    /// Перезапуск программы — после смены языка: часть надписей вычисляется один раз
    /// при запуске, и честнее начать заново, чем показывать смесь двух языков.
    /// </summary>
    public void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var args = Environment.GetCommandLineArgs().Skip(1)
                    .Where(a => !a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
                // Сначала отпускаем блокировку «один экземпляр», иначе новый процесс
                // решит, что программа уже запущена, и просто закроется.
                try { _instanceLock?.ReleaseMutex(); } catch { }
                _instanceLock?.Dispose();
                _instanceLock = null;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, string.Join(' ', args))
                {
                    UseShellExecute = false
                });
            }
        }
        catch { }
        Tray.Exit();
    }

    /// <summary>
    /// Меняет тему на лету: подменяет ресурсы и пересоздаёт главное окно на том же месте.
    /// Отчёт скана, каталог и история живут в AppState и пересоздание переживают.
    /// </summary>
    public void ApplyLook(Look look, string reopenView)
    {
        if (ThemeManager.Current == look) return;

        var oldScale = ThemeManager.Current.ScaleFactor;
        ThemeManager.Apply(look);
        AppSettings.Current.Look = ThemeManager.Current;
        AppSettings.Current.Save();

        var old = MainWindow;
        var fresh = new Views.MainWindow(reopenView, isReload: true);
        if (old is not null)
        {
            fresh.WindowStartupLocation = WindowStartupLocation.Manual;
            var bounds = old.WindowState == WindowState.Normal
                ? new Rect(old.Left, old.Top, old.Width, old.Height)
                : old.RestoreBounds;
            // При смене масштаба окно растёт или сжимается вместе с содержимым.
            var k = ThemeManager.Current.ScaleFactor / oldScale;
            var area = SystemParameters.WorkArea;
            fresh.Width = Math.Min(bounds.Width * k, area.Width);
            fresh.Height = Math.Min(bounds.Height * k, area.Height);
            fresh.Left = Math.Max(area.Left, Math.Min(bounds.Left, area.Right - fresh.Width));
            fresh.Top = Math.Max(area.Top, Math.Min(bounds.Top, area.Bottom - fresh.Height));
        }

        MainWindow = fresh;
        fresh.Show();
        if (old?.WindowState == WindowState.Maximized) fresh.WindowState = WindowState.Maximized;
        if (old is Views.MainWindow previous) previous.CloseForReplace();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Выводит вперёд уже запущенный экземпляр вместо молчаливого выхода.</summary>
    private static void FocusExistingWindow()
    {
        try
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var other = System.Diagnostics.Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

            if (other is null) return;

            ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE — развернуть, если свёрнуто
            SetForegroundWindow(other.MainWindowHandle);
        }
        catch { /* не смогли переключиться — просто выходим */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray.Hide();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, fatal: false);
        e.Handled = true;
    }

    private static void Report(Exception ex, bool fatal)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";

        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Velocity", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
        }
        catch { /* не смогли записать журнал — окно всё равно покажем */ }

        MessageBox.Show(
            fatal
                ? Loc.F("Произошла ошибка, работа будет прервана.\n\n{0}", text)
                : Loc.F("Произошла ошибка. Программа продолжит работу.\n\n{0}", text),
            "VELOCITY", MessageBoxButton.OK,
            fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
    }
}
