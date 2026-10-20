using System.Globalization;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Infrastructure.Media;

/// <summary>
/// Remuxes into Matroska with FFmpeg using stream copy (<c>-c copy</c>): video, audio, attachments,
/// chapters and original subtitle tracks are copied bit-for-bit. Only MP4 timed text, which Matroska
/// cannot store, is converted to SRT.
/// </summary>
public sealed class FfmpegMuxer(IToolLocator tools) : IMuxer
{
    public string Name => "FFmpeg";

    public bool CanMux(MediaInfo source) => tools.FfmpegPath is not null;

    public async Task MuxAsync(MuxRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var ffmpeg = tools.FfmpegPath ?? throw new MediaToolException("FFmpeg was not found.");
        var args = BuildArguments(request);
        var duration = request.Source.Duration?.TotalSeconds ?? 0;

        var result = await ProcessRunner.RunAsync(ffmpeg, args, ct,
            onStdOutLine: line =>
            {
                // -progress pipe:1 emits key=value lines.
                if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    progress?.Report(Math.Clamp(us / 1_000_000.0 / duration, 0, 1));
            },
            timeout: TimeSpan.FromHours(6));

        if (result.ExitCode != 0 || !File.Exists(request.OutputPath))
        {
            try { if (File.Exists(request.OutputPath)) File.Delete(request.OutputPath); } catch { }
            throw new MediaToolException("FFmpeg could not write the output file.", result.StdErr.Trim());
        }
        progress?.Report(1);
    }

    internal static List<string> BuildArguments(MuxRequest request)
    {
        var source = request.Source;
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", source.Path };
        foreach (var track in request.NewTracks) args.AddRange(["-i", track.FilePath]);

        // Which source streams are kept, in output order.
        var kept = source.Streams
            .Where(s => s.Kind is StreamKind.Video or StreamKind.Audio or StreamKind.Subtitle or StreamKind.Attachment)
            .Where(s => !(s.Kind == StreamKind.Subtitle && request.ReplacedSubtitleStreams.Contains(s.Index)))
            .ToList();
        foreach (var s in kept) args.AddRange(["-map", $"0:{s.Index}"]);
        for (var i = 0; i < request.NewTracks.Count; i++) args.AddRange(["-map", $"{i + 1}:0"]);

        args.AddRange(["-c", "copy"]);

        // Per-output-stream adjustments.
        for (var n = 0; n < kept.Count; n++)
        {
            var s = kept[n];
            if (s.Kind != StreamKind.Subtitle) continue;
            if (s.CodecName is "mov_text" or "tx3g" or "text")
                args.AddRange([$"-c:{n}", "srt"]);
            if (request.ClearOriginalDefaultSubtitle && s.IsDefault)
                args.AddRange([$"-disposition:{n}", s.IsForced ? "forced" : "0"]);
        }
        for (var i = 0; i < request.NewTracks.Count; i++)
        {
            var n = kept.Count + i;
            var track = request.NewTracks[i];
            args.AddRange([$"-metadata:s:{n}", $"language={track.Language.ContainerCode}"]);
            args.AddRange([$"-metadata:s:{n}", $"title={track.Title}"]);
            args.AddRange([$"-disposition:{n}", track.IsDefault ? "default" : "0"]);
        }

        args.AddRange(["-max_interleave_delta", "0", "-progress", "pipe:1", "-nostats", "-f", "matroska", request.OutputPath]);
        return args;
    }
}
