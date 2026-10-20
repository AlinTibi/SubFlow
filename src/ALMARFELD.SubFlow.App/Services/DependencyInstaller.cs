using System.Diagnostics;

namespace ALMARFELD.SubFlow.App.Services;

/// <summary>A program SubFlow can install for the user through the Windows Package Manager.</summary>
public sealed record InstallablePackage(string Name, string WingetId, string LicenseNote);

/// <summary>
/// Installs dependencies with winget, or pulls an Ollama model, in a visible console window so the
/// user sees exactly what runs. Always started by the user after a confirmation dialog; no shell is
/// involved and every argument is passed separately.
/// </summary>
public static class DependencyInstaller
{
    public static readonly InstallablePackage Ffmpeg = new("FFmpeg", "Gyan.FFmpeg", "FFmpeg (LGPL/GPL) — https://ffmpeg.org/legal.html");
    public static readonly InstallablePackage MkvToolNix = new("MKVToolNix", "MoritzBunkus.MKVToolNix", "MKVToolNix (GPL-2.0) — https://mkvtoolnix.download");
    public static readonly InstallablePackage Ollama = new("Ollama", "Ollama.Ollama", "Ollama (MIT) — https://ollama.com");

    public static bool WingetAvailable => FindOnPath("winget.exe") is not null;

    public static Task<int> InstallAsync(InstallablePackage package) =>
        RunVisibleAsync(FindOnPath("winget.exe") ?? "winget.exe",
            ["install", "--id", package.WingetId, "--exact", "--source", "winget"]);

    private static string OllamaFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");

    private static string? OllamaExe => FindOnPath("ollama.exe") ?? Existing(Path.Combine(OllamaFolder, "ollama.exe"));

    public static bool OllamaInstalled => OllamaExe is not null;

    /// <summary>Starts the Ollama tray app (which runs the local server).</summary>
    public static void StartOllama()
    {
        var app = Existing(Path.Combine(OllamaFolder, "ollama app.exe"));
        if (app is not null) Process.Start(new ProcessStartInfo(app) { UseShellExecute = false })?.Dispose();
        else if (OllamaExe is { } exe) Process.Start(new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    /// <summary>Runs "ollama pull &lt;model&gt;" (downloads the model from the Ollama library).</summary>
    public static Task<int> PullModelAsync(string model) =>
        RunVisibleAsync(OllamaExe ?? throw new InvalidOperationException("Ollama is not installed."), ["pull", model]);

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static async Task<int> RunVisibleAsync(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        // winget lives in the per-user WindowsApps folder, which is not always on PATH for desktop apps.
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe);
        return File.Exists(apps) ? apps : null;
    }
}
