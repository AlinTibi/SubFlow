using System.Globalization;
using System.Text;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>Temporary folder deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SubFlowTests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* files may be locked briefly */ }
    }
}

public sealed record SyntheticSubtitle(string Content, string Language, string Extension = ".srt", string? Title = null);

/// <summary>Creates small synthetic test videos with FFmpeg (no copyrighted material).</summary>
public static class TestMedia
{
    public static readonly ToolLocator Tools = new();

    public static bool FfmpegAvailable => Tools.FfmpegPath is not null && Tools.FfprobePath is not null;

    public const string EnglishSrt = """
        1
        00:00:00,500 --> 00:00:01,800
        Goku, are you ready?

        2
        00:00:02,000 --> 00:00:03,250
        I was born ready!
        Let's go.

        3
        00:00:03,400 --> 00:00:04,900
        <i>Kamehameha!</i>

        4
        00:00:05,000 --> 00:00:05,900
        This is a very long subtitle line that keeps going and going to test how long lines are handled by the translator.

        """;

    public static string SampleSrt(int cues, string prefix = "Line")
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            var start = TimeSpan.FromMilliseconds(100 + i * 400);
            var end = start + TimeSpan.FromMilliseconds(350);
            sb.Append(i + 1).Append('\n')
              .Append(CultureInfo.InvariantCulture, $"{start:hh\\:mm\\:ss\\,fff} --> {end:hh\\:mm\\:ss\\,fff}\n")
              .Append(CultureInfo.InvariantCulture, $"{prefix} number {i + 1}, what are you doing?\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Generates a ~6 s video (H.264 + AAC) with the given subtitle tracks.</summary>
    public static async Task<string> CreateVideoAsync(string path, IReadOnlyList<SyntheticSubtitle> subtitles,
        double seconds = 6, string container = "matroska", string? subtitleCodec = null)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size=160x120:rate=10",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
        };
        var subFiles = new List<string>();
        for (var i = 0; i < subtitles.Count; i++)
        {
            var f = System.IO.Path.Combine(dir, $"_sub{i}_{Guid.NewGuid():N}{subtitles[i].Extension}");
            await System.IO.File.WriteAllTextAsync(f, subtitles[i].Content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            subFiles.Add(f);
            args.AddRange(["-i", f]);
        }
        args.AddRange(["-map", "0:v", "-map", "1:a"]);
        for (var i = 0; i < subtitles.Count; i++) args.AddRange(["-map", $"{i + 2}:0"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k"]);
        if (subtitleCodec is not null) args.AddRange(["-c:s", subtitleCodec]);
        else if (container == "matroska") args.AddRange(["-c:s", "copy"]);
        for (var i = 0; i < subtitles.Count; i++)
        {
            args.AddRange([$"-metadata:s:s:{i}", $"language={subtitles[i].Language}"]);
            if (subtitles[i].Title is { } t) args.AddRange([$"-metadata:s:s:{i}", $"title={t}"]);
        }
        args.AddRange(["-f", container, path]);

        var result = await ProcessRunner.RunAsync(Tools.FfmpegPath!, args, CancellationToken.None);
        foreach (var f in subFiles) System.IO.File.Delete(f);
        if (result.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + result.StdErr);
        return path;
    }

    /// <summary>MD5 of the demuxed packets of one stream type: equal hashes mean no re-encoding.</summary>
    public static async Task<string> StreamHashAsync(string path, string streamSpecifier)
    {
        var result = await ProcessRunner.RunAsync(Tools.FfmpegPath!,
            ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", $"0:{streamSpecifier}", "-c", "copy", "-f", "md5", "-"],
            CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr);
        return result.StdOut.Trim();
    }

    /// <summary>Extracts subtitle stream N (by kind index) of a file to SRT text using FFmpeg.</summary>
    public static async Task<string> ReadSubtitleAsync(string path, int subtitleIndex)
    {
        var result = await ProcessRunner.RunAsync(Tools.FfmpegPath!,
            ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", $"0:s:{subtitleIndex}", "-c:s", "srt", "-f", "srt", "-"],
            CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr);
        return result.StdOut;
    }
}

/// <summary>
/// Deterministic test engine: "translates" by tagging text with the target code. Used only in tests
/// to verify the pipeline; real translation is covered by the optional live Ollama test.
/// </summary>
public class TaggingProvider : ITranslationProvider
{
    public int Calls;
    public virtual string Id => "test";
    public string DisplayName => "Test engine";
    public ProviderCost Cost => ProviderCost.FreeLocal;
    public bool SendsTextOffMachine => false;
    public string ModelId => "tagger";
    public int PreferredBatchSize { get; set; } = 10;
    public Func<CancellationToken, Task>? BeforeEachCall { get; set; }
    public virtual bool SupportsLanguage(Language language, bool asSource) => true;

    public virtual async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (BeforeEachCall is not null) await BeforeEachCall(ct);
        return request.Segments.ToDictionary(s => s.Id, s => $"[{request.Target.Code}] {s.Text}");
    }
}

/// <summary>Drops the given ids on the first N calls, then behaves.</summary>
public sealed class FlakyProvider(int flakyCalls, Func<TranslationBatchRequest, IEnumerable<int>> drop) : TaggingProvider
{
    public override async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        var result = new Dictionary<int, string>(await base.TranslateBatchAsync(request, ct));
        if (Calls <= flakyCalls) foreach (var id in drop(request)) result.Remove(id);
        return result;
    }
}

/// <summary>Never returns a translation for segments containing a marker.</summary>
public sealed class StubbornProvider(string marker) : TaggingProvider
{
    public override async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        var result = new Dictionary<int, string>(await base.TranslateBatchAsync(request, ct));
        foreach (var s in request.Segments.Where(s => s.Text.Contains(marker))) result.Remove(s.Id);
        return result;
    }
}

public sealed class ThrowingProvider(bool transient) : TaggingProvider
{
    public override Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        throw new TranslationProviderException("Engine unavailable", transient);
    }
}

/// <summary>In-memory cache for unit tests.</summary>
public sealed class MemoryCache : ITranslationCache
{
    public readonly Dictionary<string, string> Data = new();
    public IReadOnlyDictionary<string, string> GetMany(IEnumerable<string> keys) =>
        keys.Where(Data.ContainsKey).Distinct().ToDictionary(k => k, k => Data[k]);
    public void SetMany(IEnumerable<KeyValuePair<string, string>> entries) { foreach (var (k, v) in entries) Data[k] = v; }
    public long Count() => Data.Count;
    public void Clear() => Data.Clear();
}
