using ALMARFELD.SubFlow.Core.Abstractions;

namespace ALMARFELD.SubFlow.Infrastructure.Tools;

public sealed class ToolPathOverrides
{
    public string? Ffmpeg { get; set; }
    public string? Ffprobe { get; set; }
    public string? MkvMerge { get; set; }
}

/// <summary>
/// Finds FFmpeg, FFprobe and mkvmerge. Search order: user-configured path, a "tools" folder next
/// to the application, PATH, then common install locations (WinGet, MKVToolNix installer).
/// SubFlow does not bundle these tools; see THIRD-PARTY-NOTICES.md.
/// </summary>
public sealed class ToolLocator : IToolLocator
{
    private readonly Func<ToolPathOverrides> _overrides;

    public ToolLocator(Func<ToolPathOverrides>? overrides = null)
    {
        _overrides = overrides ?? (() => new ToolPathOverrides());
        Refresh();
    }

    public string? FfmpegPath { get; private set; }
    public string? FfprobePath { get; private set; }
    public string? MkvMergePath { get; private set; }

    private readonly object _statusLock = new();
    private IReadOnlyList<ToolStatus>? _status;

    public void Refresh()
    {
        lock (_statusLock) _status = null;
        var o = _overrides();
        FfmpegPath = Find("ffmpeg.exe", o.Ffmpeg, FfmpegCandidates());
        FfprobePath = Find("ffprobe.exe", o.Ffprobe,
            FfmpegPath is not null ? [Path.Combine(Path.GetDirectoryName(FfmpegPath)!, "ffprobe.exe")] : FfmpegCandidates());
        FfprobePath ??= Find("ffprobe.exe", null, FfmpegCandidates());
        MkvMergePath = Find("mkvmerge.exe", o.MkvMerge, MkvCandidates());
    }

    /// <summary>
    /// Runs each tool once to read its version. The result is cached until <see cref="Refresh"/>,
    /// so several pages asking at startup share one check.
    /// </summary>
    public IReadOnlyList<ToolStatus> GetStatus()
    {
        lock (_statusLock)
        {
            return _status ??=
            [
                Status("FFmpeg", FfmpegPath, "-version", required: true,
                    "Required. Install with: winget install Gyan.FFmpeg (or https://ffmpeg.org/download.html)"),
                Status("FFprobe", FfprobePath, "-version", required: true,
                    "Required. Included with FFmpeg."),
                Status("MKVToolNix (mkvmerge)", MkvMergePath, "--version", required: false,
                    "Optional. Preferred for remuxing MKV files (https://mkvtoolnix.download)."),
            ];
        }
    }

    private static ToolStatus Status(string name, string? path, string versionArg, bool required, string help)
    {
        if (path is null) return new ToolStatus(name, false, null, null, (required ? "Not found. " : "Not installed. ") + help);
        try
        {
            var result = ProcessRunner.RunAsync(path, [versionArg], CancellationToken.None, timeout: TimeSpan.FromSeconds(45))
                .GetAwaiter().GetResult();
            var first = result.StdOut.Split('\n').FirstOrDefault()?.Trim();
            return new ToolStatus(name, result.ExitCode == 0, path, ShortVersion(first),result.ExitCode == 0 ? null : "The tool did not run correctly.");
        }
        catch (Exception ex)
        {
            return new ToolStatus(name, false, path, null, ex.Message);
        }
    }

    /// <summary>
    /// "ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) …" → "7.1";
    /// "mkvmerge v88.0 ('…') 64-bit" → "88.0". Falls back to the first line.
    /// </summary>
    internal static string? ShortVersion(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(?:version\s+|\sv)(n?\d+(?:\.\d+)*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : line.Length > 60 ? line[..60] + "…" : line;
    }

    private static string? Find(string exe, string? configured, IEnumerable<string> candidates)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim().Trim('"');
            if (Directory.Exists(c)) c = Path.Combine(c, exe);
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        var local = Path.Combine(AppContext.BaseDirectory, "tools", exe);
        if (File.Exists(local)) return local;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> FfmpegCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        yield return Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe");
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateDirectories(packages, "*FFmpeg*")
                    .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                    .ToList();
            }
            catch { found = Array.Empty<string>(); }
            foreach (var f in found) yield return f;
        }
    }

    private static IEnumerable<string> MkvCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix", "mkvmerge.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MKVToolNix", "mkvmerge.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MKVToolNix", "mkvmerge.exe");
    }
}
