using ALMARFELD.SubFlow.Core.Languages;

namespace ALMARFELD.SubFlow.Core.Subtitles;

/// <summary>An external subtitle file next to a video ("Episode 01.en.forced.srt").</summary>
public sealed record SidecarSubtitle(string Path, Language? Language, bool IsForced, bool IsHearingImpaired)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
}

/// <summary>
/// Finds subtitle files that belong to a video: same folder, same base name, optionally followed by
/// a language and flags, e.g. <c>Movie.srt</c>, <c>Movie.en.srt</c>, <c>Movie.eng.sdh.ass</c>.
/// </summary>
public static class SidecarFinder
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt" };
    private static readonly HashSet<string> ForcedTokens = new(StringComparer.OrdinalIgnoreCase) { "forced", "foreign" };
    private static readonly HashSet<string> HearingImpairedTokens = new(StringComparer.OrdinalIgnoreCase) { "sdh", "cc", "hi", "hoh" };

    public static IReadOnlyList<SidecarSubtitle> Find(string videoPath, LanguageCatalog catalog)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(videoPath));
        if (dir is null || !Directory.Exists(dir)) return [];
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var result = new List<SidecarSubtitle>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, baseName + ".*"); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!Extensions.Contains(Path.GetExtension(file))) continue;
            if (file.Contains(".subflow-tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            // EnumerateFiles("Movie.*") also matches "Movie.Extended.mkv"-style siblings; the part
            // after the base name must be empty or dot-separated tokens.
            if (name.Length != baseName.Length && name[baseName.Length] != '.') continue;
            var tokens = name[baseName.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);
            result.Add(Parse(file, tokens, catalog));
        }
        return result;
    }

    internal static SidecarSubtitle Parse(string path, IReadOnlyList<string> tokens, LanguageCatalog catalog)
    {
        Language? language = null;
        bool forced = false, hearingImpaired = false;
        foreach (var token in tokens)
        {
            if (ForcedTokens.Contains(token)) { forced = true; continue; }
            // "hi" is Hindi when it is the first token ("Movie.hi.srt"), hearing impaired after a language.
            if (HearingImpairedTokens.Contains(token) && (language is not null || !token.Equals("hi", StringComparison.OrdinalIgnoreCase)))
            {
                hearingImpaired = true;
                continue;
            }
            if (language is null && token.Length >= 2 && catalog.Find(token) is { } l) language = l;
        }
        return new SidecarSubtitle(path, language, forced, hearingImpaired);
    }

    /// <summary>
    /// Chooses the sidecar to translate from. With a source language, only files in that language
    /// (or without a language tag) qualify; files in one of the target languages are never used,
    /// so SubFlow's own sidecar outputs are not translated again.
    /// </summary>
    public static SidecarSubtitle? Select(IReadOnlyList<SidecarSubtitle> sidecars, Language? source, IReadOnlyCollection<Language> targets)
    {
        var candidates = sidecars.Where(s => s.Language is null || !targets.Contains(s.Language)).ToList();
        if (source is not null)
        {
            var tagged = candidates.Where(s => s.Language == source).ToList();
            candidates = tagged.Count > 0 ? tagged : candidates.Where(s => s.Language is null).ToList();
        }
        return candidates
            .OrderBy(s => s.IsForced ? 1 : 0)
            .ThenBy(s => s.IsHearingImpaired ? 1 : 0)
            .ThenByDescending(s => SafeLength(s.Path))
            .FirstOrDefault();
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
