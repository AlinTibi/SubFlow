using System.Diagnostics;

namespace ALMARFELD.SubFlow.App.Services;

public static class Shell
{
    /// <summary>Opens Explorer with the file selected (or the folder opened).</summary>
    public static void ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch { /* Explorer unavailable */ }
    }

    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Opens a page of the Windows Settings app (ms-settings: links only).</summary>
    public static void OpenWindowsSettings(string uri)
    {
        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }
}
