using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            UseDarkTitleBar();
            RestorePlacement();
        };
        FitToWorkArea();
        ContentRendered += (_, _) => FitToCurrentMonitor();
        DragEnter += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) Vm.Navigate(key);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else e.Effects = DragDropEffects.None;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            try { await Vm.DropAsync(paths); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Vm.Queue.IsRunning &&
            MessageBox.Show(this, L.Get("Main_CloseWhileRunning"), "ALMARFELD SubFlow", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        // Running items are stored as Interrupted on next start (see QueueViewModel) and can be resumed.
        SavePlacement();
        base.OnClosing(e);
    }

    /// <summary>
    /// On small or highly scaled screens the default size may not fit: keep the window (title bar
    /// and close button included) inside the work area. CenterScreen then centres it on the
    /// monitor with the mouse cursor.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.92);
        Height = Math.Min(Height, area.Height * 0.92);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    /// <summary>
    /// With monitors at different scaling, WPF can size the window for the primary monitor's DPI and
    /// then place it on another monitor (where the mouse is), so it ends up larger than that screen
    /// with the title bar off screen. Once shown, the window is fitted to the monitor it is on, in
    /// physical pixels and at that monitor's own scaling.
    /// </summary>
    private void FitToCurrentMonitor()
    {
        if (WindowState != WindowState.Normal || WindowStartupLocation != WindowStartupLocation.CenterScreen) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var rect)) return;
            var work = info.Work;
            var workWidth = work.Right - work.Left;
            var workHeight = work.Bottom - work.Top;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var fits = rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom;
            if (fits) return;

            // The intended size at this monitor's scaling, never more than 92 % of its work area.
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            width = (int)Math.Min(DesignWidth * scale, workWidth * 0.92);
            height = (int)Math.Min(DesignHeight * scale, workHeight * 0.92);
            _ = SetWindowPos(handle, IntPtr.Zero, work.Left + (workWidth - width) / 2, work.Top + (workHeight - height) / 2,
                width, height, SwpNoZOrder | SwpNoActivate);
        }
        catch { /* keep the default placement */ }
    }

    /// <summary>Reopens the window where the user left it, if that place is still on a monitor.</summary>
    private void RestorePlacement()
    {
        if (DataContext is not MainViewModel vm || WindowStartupLocation != WindowStartupLocation.CenterScreen) return;
        if (vm.Services.Settings.WindowBounds is not [var left, var top, var right, var bottom] || right - left < 400 || bottom - top < 300) return;
        try
        {
            var rect = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
            // The title bar must be visible on some monitor, otherwise fall back to the default placement.
            var titleBar = new NativeRect { Left = left + 40, Top = top, Right = right - 40, Bottom = top + 30 };
            if (MonitorFromRect(ref titleBar, MonitorDefaultToNull) == IntPtr.Zero) return;
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>(), ShowCmd = 1, NormalPosition = rect };
            if (!SetWindowPlacement(new WindowInteropHelper(this).Handle, ref placement)) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (vm.Services.Settings.WindowMaximized) WindowState = WindowState.Maximized;
        }
        catch { /* default placement */ }
    }

    private void SavePlacement()
    {
        try
        {
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(new WindowInteropHelper(this).Handle, ref placement)) return;
            var r = placement.NormalPosition;
            var settings = Vm.Services.Settings;
            settings.WindowBounds = [r.Left, r.Top, r.Right, r.Bottom];
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            Vm.Services.SettingsStore.Save(settings);
        }
        catch { /* not worth bothering the user about */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCmd;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }

    private const uint MonitorDefaultToNull = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    private const double DesignWidth = 1320, DesignHeight = 840;
    private const uint MonitorDefaultToNearest = 2, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var on = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ / Windows 11); 19 on older builds.
            if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref on, sizeof(int)); // failure only means a light title bar
        }
        catch { /* cosmetic only */ }
    }
}
