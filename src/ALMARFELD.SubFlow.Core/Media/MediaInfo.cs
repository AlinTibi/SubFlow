namespace ALMARFELD.SubFlow.Core.Media;

public enum StreamKind { Video, Audio, Subtitle, Attachment, Data, Unknown }

public enum SubtitleCodecKind
{
    /// <summary>Text subtitles SubFlow can extract and translate (SRT, ASS/SSA, WebVTT, MP4 timed text).</summary>
    Text,
    /// <summary>Bitmap subtitles (PGS, VobSub, DVB). Require OCR, not supported in this version.</summary>
    Image,
    Unknown,
}

public sealed record MediaStream
{
    /// <summary>Absolute stream index inside the container (ffprobe "index").</summary>
    public int Index { get; init; }
    public StreamKind Kind { get; init; }
    public string CodecName { get; init; } = "";
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsHearingImpaired { get; init; }
    public long? FrameCount { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Channels { get; init; }

    /// <summary>Position among streams of the same kind (0-based), e.g. the "N" in FFmpeg's "0:s:N".</summary>
    public int KindIndex { get; init; }

    public SubtitleCodecKind SubtitleKind => Kind != StreamKind.Subtitle
        ? SubtitleCodecKind.Unknown
        : SubtitleCodecs.Classify(CodecName);

    public string Describe()
    {
        var parts = new List<string> { $"#{Index}", Kind.ToString(), CodecName };
        if (!string.IsNullOrEmpty(Language)) parts.Add(Language!);
        if (!string.IsNullOrEmpty(Title)) parts.Add($"\"{Title}\"");
        if (IsForced) parts.Add("forced");
        return string.Join(" · ", parts);
    }
}

public sealed record MediaInfo
{
    public required string Path { get; init; }
    public string FormatName { get; init; } = "";
    public TimeSpan? Duration { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<MediaStream> Streams { get; init; } = Array.Empty<MediaStream>();

    public IEnumerable<MediaStream> Video => Streams.Where(s => s.Kind == StreamKind.Video);
    public IEnumerable<MediaStream> Audio => Streams.Where(s => s.Kind == StreamKind.Audio);
    public IEnumerable<MediaStream> Subtitles => Streams.Where(s => s.Kind == StreamKind.Subtitle);

    public bool IsMatroska => FormatName.Contains("matroska", StringComparison.OrdinalIgnoreCase) ||
                              FormatName.Contains("webm", StringComparison.OrdinalIgnoreCase);
}

public static class SubtitleCodecs
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "tx3g",
    };

    private static readonly HashSet<string> ImageCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub", "xsub", "dvb_teletext",
    };

    public static SubtitleCodecKind Classify(string codec) =>
        TextCodecs.Contains(codec) ? SubtitleCodecKind.Text
        : ImageCodecs.Contains(codec) ? SubtitleCodecKind.Image
        : SubtitleCodecKind.Unknown;

    public static bool IsAss(string codec) => codec.Equals("ass", StringComparison.OrdinalIgnoreCase) ||
                                              codec.Equals("ssa", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable codec name for the UI.</summary>
    public static string Friendly(string codec) => codec.ToLowerInvariant() switch
    {
        "subrip" or "srt" => "SRT",
        "ass" => "ASS",
        "ssa" => "SSA",
        "webvtt" => "WebVTT",
        "mov_text" or "tx3g" => "MP4 Timed Text",
        "hdmv_pgs_subtitle" or "pgssub" => "PGS (image)",
        "dvd_subtitle" or "dvdsub" => "VobSub (image)",
        "dvb_subtitle" or "dvbsub" => "DVB (image)",
        "dvb_teletext" => "Teletext",
        _ => codec,
    };
}
