using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;

namespace ALMARFELD.SubFlow.Core.Processing;

public static class TrackSelection
{
    /// <summary>
    /// Chooses the subtitle stream to translate from: the full dialogue track. Forced/signs/songs
    /// tracks (by flag or by title) and SDH come last; among the rest the track with the most
    /// subtitles wins, then the default one. With a source language only tracks in that language
    /// (or untagged ones) qualify.
    /// </summary>
    public static MediaStream? SelectSource(MediaInfo media, Language? sourceLanguage, int? explicitStreamIndex = null) =>
        RankSources(media, sourceLanguage, explicitStreamIndex) is { Count: > 0 } ranked ? ranked[0] : null;

    /// <summary>Text tracks that qualify as the source, best first.</summary>
    public static IReadOnlyList<MediaStream> RankSources(MediaInfo media, Language? sourceLanguage, int? explicitStreamIndex = null)
    {
        var text = media.Subtitles.Where(s => s.SubtitleKind == SubtitleCodecKind.Text).ToList();
        if (explicitStreamIndex is { } idx)
            return text.Where(s => s.Index == idx).ToList();
        if (text.Count == 0) return [];

        IEnumerable<MediaStream> candidates = text;
        if (sourceLanguage is not null)
        {
            var tagged = text.Where(s => sourceLanguage.Matches(s.Language)).ToList();
            if (tagged.Count > 0) candidates = tagged;
            else
            {
                // Untagged tracks may still be in the requested language; tracks tagged with
                // another language are not used.
                var untagged = text.Where(s => IsUndetermined(s.Language)).ToList();
                if (untagged.Count == 0) return [];
                candidates = untagged;
            }
        }

        // The default flag is often set on a forced/songs track (so players show signs by
        // default); it must not beat the full dialogue track.
        return candidates
            .OrderBy(s => IsPartial(s) ? 1 : 0)
            .ThenBy(s => s.IsHearingImpaired || HasTitleWord(s, HearingImpairedWords) ? 1 : 0)
            .ThenByDescending(s => s.FrameCount ?? 0)
            .ThenByDescending(s => s.IsDefault ? 1 : 0)
            .ThenBy(s => SubtitleCodecs.IsAss(s.CodecName) ? 0 : 1) // ASS keeps styling for the output
            .ThenBy(s => s.Index)
            .ToList();
    }

    private static readonly string[] PartialWords = ["forced", "foreign", "sign", "signs", "song", "songs", "lyrics", "karaoke", "commentary", "op", "ed"];
    private static readonly string[] HearingImpairedWords = ["sdh", "cc", "hi", "hearing"];

    /// <summary>A track that only covers part of the dialogue: forced, signs, songs, commentary.</summary>
    public static bool IsPartial(MediaStream s) => s.IsForced || HasTitleWord(s, PartialWords);

    private static bool HasTitleWord(MediaStream s, string[] words) =>
        s.Title is { Length: > 0 } title &&
        title.Split([' ', '-', '_', '.', '(', ')', '[', ']', '/', ','], StringSplitOptions.RemoveEmptyEntries)
            .Any(w => words.Contains(w, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Chooses an image subtitle track for OCR, with the same language rules as text tracks.
    /// Forced tracks (signs only) come last.
    /// </summary>
    public static MediaStream? SelectImageSource(MediaInfo media, Language? sourceLanguage, Func<MediaStream, bool> readable, int? explicitStreamIndex = null)
    {
        var image = media.Subtitles.Where(s => s.SubtitleKind == SubtitleCodecKind.Image && readable(s)).ToList();
        if (explicitStreamIndex is { } idx) return image.FirstOrDefault(s => s.Index == idx);
        if (sourceLanguage is not null)
        {
            var tagged = image.Where(s => sourceLanguage.Matches(s.Language)).ToList();
            image = tagged.Count > 0 ? tagged : image.Where(s => IsUndetermined(s.Language)).ToList();
        }
        return image
            .OrderBy(s => IsPartial(s) ? 1 : 0)
            .ThenBy(s => s.IsHearingImpaired ? 1 : 0)
            .ThenByDescending(s => s.IsDefault ? 1 : 0)
            .ThenByDescending(s => s.FrameCount ?? 0)
            .ThenBy(s => s.Index)
            .FirstOrDefault();
    }

    public static bool IsUndetermined(string? language) =>
        string.IsNullOrWhiteSpace(language) ||
        language.Equals("und", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("unk", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("mis", StringComparison.OrdinalIgnoreCase) ||
        language.Equals("zxx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Subtitle streams (any codec) tagged with the given language.</summary>
    public static IReadOnlyList<MediaStream> StreamsInLanguage(MediaInfo media, Language language) =>
        media.Subtitles.Where(s => language.Matches(s.Language)).ToList();
}
