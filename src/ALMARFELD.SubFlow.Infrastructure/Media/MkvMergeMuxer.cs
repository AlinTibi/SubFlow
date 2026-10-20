using System.Text.Json;
using System.Text.RegularExpressions;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Infrastructure.Media;

/// <summary>
/// Remuxes Matroska sources with mkvmerge (MKVToolNix). mkvmerge always copies streams; it never
/// re-encodes. Used for MKV inputs when MKVToolNix is installed; FFmpeg is the fallback.
/// </summary>
public sealed partial class MkvMergeMuxer(IToolLocator tools) : IMuxer
{
    public string Name => "MKVToolNix";

    public bool CanMux(MediaInfo source) => tools.MkvMergePath is not null && source.IsMatroska;

    public async Task MuxAsync(MuxRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var mkvmerge = tools.MkvMergePath ?? throw new MediaToolException("mkvmerge was not found.");

        Dictionary<int, int>? trackIds = null;
        if (request.ReplacedSubtitleStreams.Count > 0 || request.ClearOriginalDefaultSubtitle)
            trackIds = await MapTrackIdsAsync(mkvmerge, request.Source, ct);

        var args = BuildArguments(request, trackIds);
        var result = await ProcessRunner.RunAsync(mkvmerge, args, ct,
            onStdOutLine: line =>
            {
                var m = ProgressRegex().Match(line);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) progress?.Report(pct / 100.0);
            },
            timeout: TimeSpan.FromHours(6));

        // Exit code 1 = completed with warnings (output is valid), 2 = error.
        if (result.ExitCode >= 2 || !File.Exists(request.OutputPath))
        {
            try { if (File.Exists(request.OutputPath)) File.Delete(request.OutputPath); } catch { }
            throw new MediaToolException("mkvmerge could not write the output file.", (result.StdOut + result.StdErr).Trim());
        }
        progress?.Report(1);
    }

    internal static List<string> BuildArguments(MuxRequest request, IReadOnlyDictionary<int, int>? trackIds)
    {
        var args = new List<string> { "--ui-language", "en", "-o", request.OutputPath };
        var source = request.Source;

        if (request.ReplacedSubtitleStreams.Count > 0)
        {
            var keep = source.Subtitles.Where(s => !request.ReplacedSubtitleStreams.Contains(s.Index))
                .Select(s => Id(trackIds, s.Index)).ToList();
            // An empty subtitle track list is expressed as --no-subtitles.
            if (keep.Count == 0) args.Add("--no-subtitles");
            else args.AddRange(["--subtitle-tracks", string.Join(",", keep)]);
        }
        if (request.ClearOriginalDefaultSubtitle)
            foreach (var s in source.Subtitles.Where(s => s.IsDefault && !request.ReplacedSubtitleStreams.Contains(s.Index)))
                args.AddRange(["--default-track-flag", $"{Id(trackIds, s.Index)}:no"]);

        args.Add(source.Path);

        foreach (var track in request.NewTracks)
        {
            args.AddRange([
                "--language", $"0:{track.Language.Iso6391}",
                "--track-name", $"0:{track.Title}",
                "--default-track-flag", track.IsDefault ? "0:yes" : "0:no",
            ]);
            if (!track.FilePath.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) &&
                !track.FilePath.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["--sub-charset", "0:UTF-8"]);
            args.Add(track.FilePath);
        }
        return args;
    }

    private static int Id(IReadOnlyDictionary<int, int>? map, int ffprobeIndex) =>
        map is not null && map.TryGetValue(ffprobeIndex, out var id) ? id : ffprobeIndex;

    /// <summary>
    /// Maps ffprobe stream indexes to mkvmerge track ids. Both enumerate Matroska tracks in file
    /// order, but ffprobe also lists attachments as streams, so the mapping is verified by type.
    /// </summary>
    private static async Task<Dictionary<int, int>> MapTrackIdsAsync(string mkvmerge, MediaInfo source, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(mkvmerge, ["-J", source.Path], ct, timeout: TimeSpan.FromMinutes(2));
        if (result.ExitCode >= 2) throw new MediaToolException("mkvmerge could not identify the source file.", result.StdOut);
        using var doc = JsonDocument.Parse(result.StdOut);
        var tracks = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(t => (Id: t.GetProperty("id").GetInt32(), Type: t.GetProperty("type").GetString()))
            .OrderBy(t => t.Id).ToList();
        var streams = source.Streams.Where(s => s.Kind is StreamKind.Video or StreamKind.Audio or StreamKind.Subtitle)
            .OrderBy(s => s.Index).ToList();
        if (tracks.Count != streams.Count)
            throw new MediaToolException("mkvmerge and FFprobe report different track layouts; use the FFmpeg muxer for this file.");

        var map = new Dictionary<int, int>();
        for (var i = 0; i < streams.Count; i++)
        {
            var expected = streams[i].Kind switch { StreamKind.Video => "video", StreamKind.Audio => "audio", _ => "subtitles" };
            if (!string.Equals(tracks[i].Type, expected, StringComparison.OrdinalIgnoreCase))
                throw new MediaToolException("mkvmerge and FFprobe report different track layouts; use the FFmpeg muxer for this file.");
            map[streams[i].Index] = tracks[i].Id;
        }
        return map;
    }

    [GeneratedRegex(@"Progress:\s*(\d+)%")]
    private static partial Regex ProgressRegex();
}
