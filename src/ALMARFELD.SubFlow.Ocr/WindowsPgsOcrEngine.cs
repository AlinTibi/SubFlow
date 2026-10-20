using System.Runtime.InteropServices.WindowsRuntime;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Infrastructure.Tools;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using SubtitleLanguage = ALMARFELD.SubFlow.Core.Languages.Language;

namespace ALMARFELD.SubFlow.Ocr;

/// <summary>
/// Reads Blu-ray PGS subtitle tracks with the OCR engine built into Windows 10/11. The track is
/// copied out with FFmpeg (no decoding), each subtitle image is recognised locally, and identical
/// consecutive subtitles are merged. Nothing is sent off the machine.
/// </summary>
public sealed class WindowsPgsOcrEngine(IToolLocator tools) : ISubtitleOcrEngine
{
    public string Name => "Windows OCR";

    // Test hooks for tuning preprocessing.
    internal static int OcrScaleTarget = 150;

    public bool Supports(MediaStream stream) =>
        stream.CodecName.Equals("hdmv_pgs_subtitle", StringComparison.OrdinalIgnoreCase) ||
        stream.CodecName.Equals("pgssub", StringComparison.OrdinalIgnoreCase);

    public bool SupportsLanguage(SubtitleLanguage language) => FindRecognizerLanguage(language) is not null;

    /// <summary>Language tags of the OCR languages installed in Windows (e.g. "en-US").</summary>
    public static IReadOnlyList<string> InstalledLanguages
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList(); }
            catch { return []; }
        }
    }

    public async Task<SubtitleDocument> RecognizeAsync(MediaInfo media, MediaStream stream, SubtitleLanguage language,
        string workDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        var tag = FindRecognizerLanguage(language)
                  ?? throw new InvalidOperationException($"Windows has no OCR language pack for {language.Name}.");
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag))
                     ?? throw new InvalidOperationException($"Windows OCR could not start for {language.Name}.");

        var sup = await ExtractAsync(media, stream, workDirectory, ct);
        var images = PgsReader.Read(sup);
        if (images.Count == 0) throw new MediaToolException($"Track #{stream.Index} contains no readable PGS images.");

        var cues = new List<SubtitleCue>();
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var text = await RecognizeAsync(engine, images[i]);
            progress?.Report((i + 1) / (double)images.Count);
            if (text.Length == 0) continue;
            var last = cues.Count > 0 ? cues[^1] : null;
            // Fades and re-compositions show the same text several times in a row.
            if (last is not null && last.Text == text && images[i].Start - last.End <= TimeSpan.FromMilliseconds(250))
                cues[^1] = new SubtitleCue(last.Index, last.Start, images[i].End, text);
            else
                cues.Add(new SubtitleCue(cues.Count + 1, images[i].Start, images[i].End, text));
        }
        return new SubtitleDocument(SubtitleFormat.Srt, cues);
    }

    internal static async Task<string> RecognizeAsync(OcrEngine engine, PgsBitmap image)
    {
        // Windows OCR sometimes skips lines of a multi-line image, so each text line is recognised
        // on its own.
        var lines = new List<string>();
        foreach (var (top, bottom) in TextBands(image))
        {
            var text = await RecognizeBandAsync(engine, image, top, bottom);
            lines.AddRange(text.Select(CleanLine).Where(l => l.Length > 0));
        }
        return string.Join('\n', lines);
    }

    /// <summary>Row ranges [top, bottom) that contain text, split at blank rows between lines.</summary>
    internal static List<(int Top, int Bottom)> TextBands(PgsBitmap image)
    {
        const byte ink = 170;
        var hasInk = new bool[image.Height];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (image.Gray[y * image.Width + x] < ink) { hasInk[y] = true; break; }

        var bands = new List<(int Top, int Bottom)>();
        var start = -1;
        for (var y = 0; y <= image.Height; y++)
        {
            var on = y < image.Height && hasInk[y];
            if (on && start < 0) start = y;
            else if (!on && start >= 0) { bands.Add((start, y)); start = -1; }
        }
        // Glue fragments (dots of "i", accents, descenders) to the line they belong to.
        var tallest = bands.Count == 0 ? 0 : bands.Max(b => b.Bottom - b.Top);
        var merged = new List<(int Top, int Bottom)>();
        foreach (var band in bands)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var small = band.Bottom - band.Top < tallest / 2 || last.Bottom - last.Top < tallest / 2;
                if (small && band.Top - last.Bottom <= Math.Max(2, tallest / 4))
                {
                    merged[^1] = (last.Top, band.Bottom);
                    continue;
                }
            }
            merged.Add(band);
        }
        return merged;
    }

    private static async Task<IEnumerable<string>> RecognizeBandAsync(OcrEngine engine, PgsBitmap image, int top, int bottom)
    {
        var bandHeight = bottom - top;
        // Large text with a white margin is recognised best; small subtitles are enlarged.
        var scale = Math.Clamp((int)Math.Ceiling(OcrScaleTarget / (double)bandHeight), 1, 4);
        var pad = Math.Max(16, bandHeight * scale / 2);
        var maxSide = (int)OcrEngine.MaxImageDimension;
        while (scale > 1 && (image.Width * scale + 2 * pad > maxSide || bandHeight * scale + 2 * pad > maxSide)) scale--;
        var width = Math.Min(image.Width * scale + 2 * pad, maxSide);
        var height = Math.Min(bandHeight * scale + 2 * pad, maxSide);

        var gray = new byte[width * height];
        Array.Fill(gray, (byte)255);
        for (var y = 0; y < bandHeight * scale && y + pad < height; y++)
        for (var x = 0; x < image.Width * scale && x + pad < width; x++)
            gray[(y + pad) * width + x + pad] = image.Gray[(top + y / scale) * image.Width + x / scale];

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(), BitmapPixelFormat.Gray8, width, height);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l => l.Text);
    }

    /// <summary>Fixes typical OCR artefacts that are unambiguous in subtitles.</summary>
    internal static string CleanLine(string line)
    {
        line = line.Trim()
            .Replace('|', 'I')
            .Replace(" ,", ",").Replace(" .", ".").Replace(" ?", "?").Replace(" !", "!")
            .Replace("''", "\"");
        // Dialogue dashes are often read as other dash characters.
        if (line.Length > 1 && line[0] is '—' or '–' or '_' && line[1] == ' ') line = "-" + line[1..];
        return line;
    }

    private static string? FindRecognizerLanguage(SubtitleLanguage language)
    {
        IReadOnlyList<Windows.Globalization.Language> available;
        try { available = OcrEngine.AvailableRecognizerLanguages; }
        catch { return null; }
        return available.FirstOrDefault(l => language.Matches(l.LanguageTag))?.LanguageTag;
    }

    private async Task<string> ExtractAsync(MediaInfo media, MediaStream stream, string workDirectory, CancellationToken ct)
    {
        var ffmpeg = tools.FfmpegPath ?? throw new MediaToolException("FFmpeg was not found. Install FFmpeg (see Settings → Tools).");
        Directory.CreateDirectory(workDirectory);
        var output = Path.Combine(workDirectory, $"source-{stream.Index}.sup");
        var result = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", media.Path, "-map", $"0:{stream.Index}", "-c:s", "copy", "-f", "sup", output],
            ct, timeout: TimeSpan.FromMinutes(30));
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new MediaToolException($"Could not extract image subtitle track #{stream.Index}.", result.StdErr.Trim());
        return output;
    }
}
