using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Velocity.App;

/// <summary>
/// Прозрачность и размытие окна.
///
/// Прозрачный фон в WPF возможен только у окна без системной рамки (AllowsTransparency),
/// поэтому включается это только при ненулевой прозрачности: обычное окно остаётся как было.
/// Размытие — через SetWindowCompositionAttribute (так же делают «Параметры» Windows 10);
/// работает на Windows 10 и 11, на остальных просто не включится.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowEffects
{
    /// <summary>Вызывается до показа окна: прозрачность нельзя включить у уже показанного.</summary>
    public static void Prepare(Window window, FrameworkElement root, Look look)
    {
        if (look.Transparency <= 0) return;

        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;

        if (Application.Current.Resources["BgBase"] is SolidColorBrush baseBrush && root is System.Windows.Controls.Border border)
        {
            var color = baseBrush.Color;
            color.A = (byte)Math.Round(255 * look.BackgroundOpacity);
            border.Background = ThemeManager.Frozen(color);
        }

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (look.Blur) EnableBlur(handle);
            // Окно без рамки при разворачивании залезает под панель задач — ограничиваем рабочей областью.
            HwndSource.FromHwnd(handle)?.AddHook(MaximizeHook);
        };
    }

    // ───────────────────────────── Размытие ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    private static void EnableBlur(IntPtr handle)
    {
        try
        {
            var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
            int size = Marshal.SizeOf(accent);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new CompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(handle, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { /* нет такой функции — окно останется просто прозрачным */ }
    }

    // ───────────────────────── Разворачивание без рамки ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private static IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }
}

/// <summary>Значок в трее: окно можно убрать туда крестиком и вернуть двойным щелчком.</summary>
[SupportedOSPlatform("windows")]
public static class Tray
{
    private static System.Windows.Forms.NotifyIcon? _icon;

    /// <summary>Пользователь выбрал «Выход» в трее — дальше окно закрывается по-настоящему.</summary>
    public static bool ExitRequested { get; private set; }

    public static bool IsVisible => _icon is not null;

    public static void Show()
    {
        if (_icon is not null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Открыть VELOCITY"), null, (_, _) => Restore());
        menu.Items.Add(Loc.T("Выход"), null, (_, _) => Exit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "VELOCITY",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Restore();
    }

    public static void Hide()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    public static void Restore()
    {
        var window = Application.Current.MainWindow;
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public static void Exit()
    {
        ExitRequested = true;
        Hide();
        Application.Current.Shutdown();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}

/// <summary>Запуск вместе с Windows: запись в автозагрузке текущего пользователя, сразу в трей.</summary>
[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VELOCITY";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\" --tray");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
