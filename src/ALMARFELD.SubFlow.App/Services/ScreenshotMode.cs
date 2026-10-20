using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Services;

/// <summary>
/// Documentation/QA helper: <c>SubFlow.exe --screenshots &lt;folder&gt; [--import &lt;path&gt;]</c> renders every page
/// to PNG and exits. With <c>--translate ro,fr</c> it first runs a real batch on the imported videos through the
/// same view-model commands the buttons use (QA end-to-end check; combine with <c>--data</c> for an isolated profile).
/// </summary>
public static class ScreenshotMode
{
    public static IReadOnlyList<string> Translate(string[] args)
    {
        var i = Array.IndexOf(args, "--translate");
        return i >= 0 && i + 1 < args.Length
            ? args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    public static bool TryParse(string[] args, out string outputDir, out string? import)
    {
        outputDir = "";
        import = null;
        var i = Array.IndexOf(args, "--screenshots");
        if (i < 0 || i + 1 >= args.Length) return false;
        outputDir = Path.GetFullPath(args[i + 1]);
        var j = Array.IndexOf(args, "--import");
        if (j >= 0 && j + 1 < args.Length) import = args[j + 1];
        return true;
    }

    public static async Task RunAsync(Window window, MainViewModel vm, string outputDir, string? import, IReadOnlyList<string> translate)
    {
        Directory.CreateDirectory(outputDir);
        if (import is not null) await vm.Videos.AddPathsAsync([import]);
        vm.Videos.Selected = vm.Videos.Entries.FirstOrDefault();
        await Task.Delay(1500); // let Ollama/tool checks complete

        if (translate.Count > 0)
        {
            foreach (var t in vm.Translation.Targets)
                t.IsSelected = translate.Contains(t.Code, StringComparer.OrdinalIgnoreCase);
            if (!vm.Translation.StartCommand.CanExecute(null))
                throw new InvalidOperationException("Start is disabled: no ready videos or no target language.");
            vm.Translation.StartCommand.Execute(null);
            await Task.Delay(500);
            while (vm.Queue.IsRunning) await Task.Delay(500);
            await Task.Delay(500);
        }

        foreach (var nav in vm.Nav)
        {
            vm.Navigate(nav.Key);
            await Settle();
            Capture(window, Path.Combine(outputDir, $"{nav.Key.ToLowerInvariant()}.png"));

            // Long pages get a second picture scrolled to the end.
            if (FindScrollViewer(window) is { ScrollableHeight: > 40 } scroll)
            {
                scroll.ScrollToEnd();
                await Settle();
                Capture(window, Path.Combine(outputDir, $"{nav.Key.ToLowerInvariant()}-end.png"));
                scroll.ScrollToHome();
            }
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            // Skip list boxes and grids, whose own scroll viewers are not the page.
            if (child is System.Windows.Controls.Primitives.Selector or System.Windows.Controls.DataGrid) continue;
            if (child is System.Windows.Controls.ScrollViewer sv && sv.ScrollableHeight > 0) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);
        }
    }

    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        // Paint the window background first so the PNG is not transparent.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
