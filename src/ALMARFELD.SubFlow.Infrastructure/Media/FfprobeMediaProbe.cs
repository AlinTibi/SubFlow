using System.Globalization;
using System.Text.Json;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Infrastructure.Media;

public sealed class FfprobeMediaProbe(IToolLocator tools) : IMediaProbe
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var ffprobe = tools.FfprobePath ?? throw new MediaToolException("FFprobe was not found. Install FFmpeg (see Settings → Tools).");
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);

        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", "-i", path],
            ct, timeout: TimeSpan.FromMinutes(2));
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw new MediaToolException("The file could not be read. It may be corrupt or not a supported video.", result.StdErr.Trim());

        try
        {
            return Parse(path, result.StdOut);
        }
        catch (JsonException ex)
        {
            throw new MediaToolException("FFprobe returned unreadable output.", ex.Message, ex);
        }
    }

    internal static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<MediaStream>();
        var kindCounters = new Dictionary<StreamKind, int>();

        if (root.TryGetProperty("streams", out var arr))
        {
            foreach (var s in arr.EnumerateArray())
            {
                var kind = Str(s, "codec_type") switch
                {
                    "video" => StreamKind.Video,
                    "audio" => StreamKind.Audio,
                    "subtitle" => StreamKind.Subtitle,
                    "attachment" => StreamKind.Attachment,
                    "data" => StreamKind.Data,
                    _ => StreamKind.Unknown,
                };
                // Cover art is reported as a video stream with the attached_pic disposition.
                var disposition = s.TryGetProperty("disposition", out var d) ? d : default;
                if (kind == StreamKind.Video && Flag(disposition, "attached_pic")) kind = StreamKind.Attachment;

                kindCounters.TryGetValue(kind, out var kindIndex);
                kindCounters[kind] = kindIndex + 1;

                var tags = s.TryGetProperty("tags", out var t) ? t : default;
                streams.Add(new MediaStream
                {
                    Index = s.TryGetProperty("index", out var idx) ? idx.GetInt32() : streams.Count,
                    Kind = kind,
                    KindIndex = kindIndex,
                    CodecName = Str(s, "codec_name") ?? Str(s, "codec_tag_string") ?? "unknown",
                    Language = Tag(tags, "language"),
                    Title = Tag(tags, "title") ?? (Tag(tags, "handler_name") is { } h && !IsGenericHandler(h) ? h : null),
                    IsDefault = Flag(disposition, "default"),
                    IsForced = Flag(disposition, "forced"),
                    IsHearingImpaired = Flag(disposition, "hearing_impaired"),
                    FrameCount = ParseLong(Str(s, "nb_frames")) ?? ParseLong(TagPrefix(tags, "NUMBER_OF_FRAMES")),
                    Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : null,
                    Height = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : null,
                    Channels = s.TryGetProperty("channels", out var c) ? c.GetInt32() : null,
                });
            }
        }

        var format = root.TryGetProperty("format", out var f) ? f : default;
        return new MediaInfo
        {
            Path = path,
            FormatName = format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") ?? "" : "",
            Duration = format.ValueKind == JsonValueKind.Object && double.TryParse(Str(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
                ? TimeSpan.FromSeconds(secs) : null,
            SizeBytes = format.ValueKind == JsonValueKind.Object ? ParseLong(Str(format, "size")) ?? 0 : 0,
            Streams = streams,
        };
    }

    private static bool IsGenericHandler(string h) =>
        h.Contains("Handler", StringComparison.OrdinalIgnoreCase) || h.Equals("SubtitleHandler", StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;

    private static string? Tag(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in tags.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString() is { Length: > 0 } s ? s : null;
        return null;
    }

    private static string? TagPrefix(JsonElement tags, string prefix)
    {
        if (tags.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in tags.EnumerateObject())
            if (p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return p.Value.GetString();
        return null;
    }

    private static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
