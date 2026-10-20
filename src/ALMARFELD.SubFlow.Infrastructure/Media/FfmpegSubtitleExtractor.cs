using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Infrastructure.Media;

/// <summary>Extracts text subtitle streams with FFmpeg (stream-level only; video is not decoded).</summary>
public sealed class FfmpegSubtitleExtractor(IToolLocator tools) : ISubtitleExtractor
{
    public async Task<string> ExtractAsync(MediaInfo media, MediaStream stream, string workDirectory, CancellationToken ct)
    {
        var ffmpeg = tools.FfmpegPath ?? throw new MediaToolException("FFmpeg was not found. Install FFmpeg (see Settings → Tools).");
        if (stream.Kind != StreamKind.Subtitle) throw new ArgumentException("Not a subtitle stream.", nameof(stream));
        if (stream.SubtitleKind != SubtitleCodecKind.Text)
            throw new MediaToolException($"Track #{stream.Index} is {SubtitleCodecs.Friendly(stream.CodecName)}; image subtitles require OCR, which is not supported in this version.");

        Directory.CreateDirectory(workDirectory);
        var isAss = SubtitleCodecs.IsAss(stream.CodecName);
        var isVtt = stream.CodecName.Equals("webvtt", StringComparison.OrdinalIgnoreCase);
        var extension = isAss ? ".ass" : isVtt ? ".vtt" : ".srt";
        var output = Path.Combine(workDirectory, $"source-{stream.Index}{extension}");

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-i", media.Path,
            "-map", $"0:{stream.Index}",
            // ASS/SSA and WebVTT are copied as-is to keep styling; everything else becomes SRT.
            "-c:s", isAss ? "copy" : isVtt ? "webvtt" : "srt",
            "-f", isAss ? "ass" : isVtt ? "webvtt" : "srt",
            output,
        };
        var result = await ProcessRunner.RunAsync(ffmpeg, args, ct, timeout: TimeSpan.FromMinutes(30));
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new MediaToolException($"Could not extract subtitle track #{stream.Index}.", result.StdErr.Trim());
        return output;
    }
}
