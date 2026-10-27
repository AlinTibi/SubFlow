using System.Text;
using System.Windows;
using System.Windows.Threading;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per profile: two instances would compete for the same queue and output files.
        // A separate profile ("--data", used for QA and portable use) gets its own instance.
        var d = Array.IndexOf(e.Args, "--data");
        string? dataDir;
        string mutexName;
        bool first;
        try
        {
            dataDir = SingleInstanceProfile.DataDirectory(e.Args);
            mutexName = SingleInstanceProfile.MutexName(dataDir);
            _singleInstance = new Mutex(true, mutexName, out first);
        }
        catch (Exception ex)
        {
            MessageBox.Show("SubFlow could not start: " + ex.Message, "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (!first)
        {
            // Already open: bring that window to the front instead of starting a second copy.
            if (!ActivateRunningInstance(mutexName))
                MessageBox.Show("ALMARFELD SubFlow is already running.", "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };

        // "--data <folder>" keeps settings, database and logs in a separate profile (portable/QA use).
        try
        {
            var paths = dataDir is not null
                ? new Infrastructure.Persistence.AppPaths(Path.Combine(dataDir, "roaming"), Path.Combine(dataDir, "local"))
                : null;
            _services = new AppServices(paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show("SubFlow could not start: " + ex.Message, "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        L.SetCulture(_services.Settings.UiLanguage);

        var vm = new MainViewModel(_services);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            _ = SetProp(handle, mutexName, new IntPtr(1));
            window.Closed += (_, _) => _ = RemoveProp(handle, mutexName);
        };

        if (ScreenshotMode.TryParse(e.Args, out var shotDir, out var shotImport))
        {
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Show();
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await ScreenshotMode.RunAsync(window, vm, shotDir, shotImport, ScreenshotMode.Translate(e.Args)); }
                catch (Exception ex) { Log(ex); }
                finally { Shutdown(); }
            });
            return;
        }

        window.Show();

        // Files passed on the command line (e.g. "Open with") are imported.
        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (d >= 0 && d + 1 < e.Args.Length) files.Remove(e.Args[d + 1]);
        if (files.Count > 0) _ = ((MainViewModel)window.DataContext).DropAsync(files);
    }

    private static bool ActivateRunningInstance(string mutexName)
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                try
                {
                    var handle = other.MainWindowHandle;
                    if (other.Id == current.Id || handle == IntPtr.Zero || GetProp(handle, mutexName) == IntPtr.Zero) continue;
                    if (IsIconic(handle)) _ = ShowWindow(handle, 9); // SW_RESTORE
                    return SetForegroundWindow(handle);
                }
                catch (InvalidOperationException) { /* The other process exited while enumerating. */ }
                catch (System.ComponentModel.Win32Exception) { /* Ignore inaccessible processes. */ }
            }
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(e.Exception.Message, "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = _services?.Paths.LogDirectory ?? Path.GetTempPath();
            File.AppendAllText(Path.Combine(dir, $"subflow-{DateTime.Now:yyyy-MM-dd}.log"),
                $"[{DateTime.Now:O}] {ex}\r\n\r\n", Encoding.UTF8);
        }
        catch { /* logging must never crash the app */ }
    }
}
