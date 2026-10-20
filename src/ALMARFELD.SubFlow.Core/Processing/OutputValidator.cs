using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Media;

namespace ALMARFELD.SubFlow.Core.Processing;

public sealed class OutputValidationException(string message) : Exception(message);

/// <summary>Verifies a remuxed file before it replaces anything: same video/audio, expected subtitle tracks.</summary>
public static class OutputValidator
{
    public static void Validate(MediaInfo source, MediaInfo output, MuxRequest request)
    {
        CompareStreams(source.Video.ToList(), output.Video.ToList(), "video");
        CompareStreams(source.Audio.ToList(), output.Audio.ToList(), "audio");

        var expectedSubs = source.Subtitles.Count(s => !request.ReplacedSubtitleStreams.Contains(s.Index)) + request.NewTracks.Count;
        var actualSubs = output.Subtitles.Count();
        if (actualSubs != expectedSubs)
            throw new OutputValidationException($"Output has {actualSubs} subtitle tracks, expected {expectedSubs}.");

        foreach (var group in request.NewTracks.GroupBy(t => t.Language.Code))
        {
            var language = group.First().Language;
            var expected = group.Count() + source.Subtitles.Count(s =>
                !request.ReplacedSubtitleStreams.Contains(s.Index) && language.Matches(s.Language));
            var actual = output.Subtitles.Count(s => language.Matches(s.Language));
            if (actual < expected)
                throw new OutputValidationException($"Output is missing the {language.Name} subtitle track (language metadata).");
        }

        if (source.Duration is { } sd && output.Duration is { } od && sd > TimeSpan.Zero)
        {
            var tolerance = TimeSpan.FromSeconds(Math.Max(2, sd.TotalSeconds * 0.01));
            if ((od - sd).Duration() > tolerance)
                throw new OutputValidationException($"Output duration {od:hh\\:mm\\:ss} differs from source {sd:hh\\:mm\\:ss}.");
        }
    }

    private static void CompareStreams(IReadOnlyList<MediaStream> source, IReadOnlyList<MediaStream> output, string kind)
    {
        if (source.Count != output.Count)
            throw new OutputValidationException($"Output has {Text.Count(output.Count, kind + " stream")}, the source has {source.Count}.");
        for (var i = 0; i < source.Count; i++)
        {
            if (!source[i].CodecName.Equals(output[i].CodecName, StringComparison.OrdinalIgnoreCase))
                throw new OutputValidationException($"The {kind} codec changed ({source[i].CodecName} → {output[i].CodecName}); the output was discarded.");
            if (source[i].Width != output[i].Width || source[i].Height != output[i].Height)
                throw new OutputValidationException($"The {kind} resolution changed; the output was discarded.");
            if (source[i].Channels != output[i].Channels)
                throw new OutputValidationException($"The {kind} channel layout changed; the output was discarded.");
        }
    }
}
