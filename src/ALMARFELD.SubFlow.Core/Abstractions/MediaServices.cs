using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Subtitles;

namespace ALMARFELD.SubFlow.Core.Abstractions;

public sealed class MediaToolException(string message, string? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Tool output (stderr tail) useful for troubleshooting.</summary>
    public string? Details { get; } = details;
}

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
}

public interface ISubtitleExtractor
{
    /// <summary>
    /// Extracts a text subtitle stream to a file in <paramref name="workDirectory"/> and returns its path.
    /// ASS/SSA are extracted verbatim; other text codecs are converted to SRT.
    /// </summary>
    Task<string> ExtractAsync(MediaInfo media, MediaStream stream, string workDirectory, CancellationToken ct);
}

public sealed record NewSubtitleTrack(string FilePath, Language Language, string Title, bool IsDefault = false);

public sealed record MuxRequest
{
    public required MediaInfo Source { get; init; }
    /// <summary>Destination MKV. Callers pass a temporary path and rename after validation.</summary>
    public required string OutputPath { get; init; }
    public required IReadOnlyList<NewSubtitleTrack> NewTracks { get; init; }
    /// <summary>Absolute stream indexes of original subtitle tracks the user explicitly chose to replace.</summary>
    public IReadOnlyCollection<int> ReplacedSubtitleStreams { get; init; } = Array.Empty<int>();
    /// <summary>Clear the "default" flag on original subtitle tracks so a new track can be default.</summary>
    public bool ClearOriginalDefaultSubtitle { get; init; }
}

/// <summary>Adds subtitle tracks to a video by remuxing (stream copy). Video and audio are never re-encoded.</summary>
public interface IMuxer
{
    string Name { get; }
    bool CanMux(MediaInfo source);
    Task MuxAsync(MuxRequest request, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Turns a bitmap subtitle track (PGS, …) into text cues with optical character recognition.</summary>
public interface ISubtitleOcrEngine
{
    string Name { get; }
    /// <summary>True when the track's codec can be read by this engine.</summary>
    bool Supports(MediaStream stream);
    /// <summary>True when text in this language can be recognised on this PC.</summary>
    bool SupportsLanguage(Language language);
    Task<SubtitleDocument> RecognizeAsync(MediaInfo media, MediaStream stream, Language language,
        string workDirectory, IProgress<double>? progress, CancellationToken ct);
}

public sealed record ToolStatus(string Name, bool IsAvailable, string? Path, string? Version, string? Message);

public interface IToolLocator
{
    string? FfmpegPath { get; }
    string? FfprobePath { get; }
    string? MkvMergePath { get; }
    void Refresh();
    IReadOnlyList<ToolStatus> GetStatus();
}
