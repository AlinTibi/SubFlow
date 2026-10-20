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

        // One instance per user: two instances would compete for the same queue and output files.
        _singleInstance = new Mutex(true, @"Local\ALMARFELD.SubFlow.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("ALMARFELD SubFlow is already running.", "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };

        // "--data <folder>" keeps settings, database and logs in a separate profile (portable/QA use).
        var d = Array.IndexOf(e.Args, "--data");
        try
        {
            var paths = d >= 0 && d + 1 < e.Args.Length
                ? new Infrastructure.Persistence.AppPaths(Path.Combine(Path.GetFullPath(e.Args[d + 1]), "roaming"),
                                                          Path.Combine(Path.GetFullPath(e.Args[d + 1]), "local"))
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
