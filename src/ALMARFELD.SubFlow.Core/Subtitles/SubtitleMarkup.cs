using System.Text.RegularExpressions;

namespace ALMARFELD.SubFlow.Core.Subtitles;

/// <summary>
/// The translatable part of a cue plus the markup that wraps it. Only <see cref="Plain"/> is sent
/// to a translation engine; <see cref="Prefix"/> and <see cref="Suffix"/> are re-applied afterwards
/// so positioning and italics survive translation.
/// </summary>
public sealed record TranslatableText(string Prefix, string Plain, string Suffix)
{
    /// <summary>
    /// Markup wrapping individual lines inside the cue (for example a second line in italics), one
    /// (open, close) pair per line of <see cref="Plain"/>. Null when no line has its own markup.
    /// </summary>
    public IReadOnlyList<(string Open, string Close)>? LineWraps { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Plain);

    public string Rebuild(string translatedPlain)
    {
        var body = translatedPlain.Trim();
        if (LineWraps is { } wraps)
        {
            // Per-line markup can only be restored when the translation kept the line structure.
            var lines = body.Split('\n');
            if (lines.Length == wraps.Count)
                body = string.Join('\n', lines.Select((l, i) => wraps[i].Open + l.Trim() + wraps[i].Close));
        }
        return Prefix + body + Suffix;
    }
}

public static partial class SubtitleMarkup
{
    public static TranslatableText Split(string text, SubtitleFormat format) => format switch
    {
        SubtitleFormat.Ass => SplitAss(text),
        _ => SplitHtmlLike(text),
    };

    public static bool IsAssDrawing(string text) => AssDrawingRegex().IsMatch(text);

    /// <summary>Plain text without any markup, line breaks kept.</summary>
    public static string ToPlain(string text, SubtitleFormat format) => Split(text, format).Plain;

    /// <summary>Converts native markup to SRT-compatible markup (best effort, used for sidecar export).</summary>
    public static string ToSrtMarkup(string text, SubtitleFormat format)
    {
        if (format == SubtitleFormat.Srt) return text;
        var split = Split(text, format);
        var italic = format == SubtitleFormat.Ass
            ? AssItalicRegex().IsMatch(split.Prefix)
            : split.Prefix.Contains("<i>", StringComparison.OrdinalIgnoreCase);
        return italic ? $"<i>{split.Plain}</i>" : split.Plain;
    }

    private static TranslatableText SplitAss(string text)
    {
        var prefixMatch = AssLeadingBlocksRegex().Match(text);
        var prefix = prefixMatch.Success ? prefixMatch.Value : string.Empty;
        var body = text[prefix.Length..];
        // Override blocks wrapping a whole line are restored per line; blocks in the middle of a line
        // cannot be mapped onto translated words and are removed (the original track keeps them).
        var wraps = LineWrapsOf(body, AssLeadingBlocksRegex(), AssTrailingBlocksRegex(), AssBlockRegex());
        body = AssBlockRegex().Replace(body, string.Empty)
            .Replace("\\h", " ", StringComparison.Ordinal);
        return new TranslatableText(prefix, NormaliseLines(body), string.Empty) { LineWraps = wraps };
    }

    /// <summary>
    /// The leading and trailing markup of each line that survives <see cref="NormaliseLines"/>, when
    /// the rest of the line has no markup. Null if no line has any.
    /// </summary>
    private static List<(string Open, string Close)>? LineWrapsOf(string body, Regex leading, Regex trailing, Regex any)
    {
        var result = new List<(string, string)>();
        var found = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (any.Replace(line, string.Empty).Trim().Length == 0) continue;
            var open = leading.Match(line) is { Success: true } o ? o.Value : string.Empty;
            var rest = line[open.Length..];
            var close = trailing.Match(rest) is { Success: true } c ? c.Value : string.Empty;
            if (any.IsMatch(rest[..^close.Length])) open = close = string.Empty;
            found |= open.Length > 0 || close.Length > 0;
            result.Add((open, close));
        }
        return found ? result : null;
    }

    private static TranslatableText SplitHtmlLike(string text)
    {
        var prefix = string.Empty;
        var suffix = string.Empty;
        var body = text;

        // SRT files frequently carry ASS-style positioning such as {\an8}.
        var assPrefix = AssLeadingBlocksRegex().Match(body);
        if (assPrefix.Success)
        {
            prefix = assPrefix.Value;
            body = body[assPrefix.Length..];
        }

        // Whole cue (or every line) wrapped in the same simple tag, e.g. <i>...</i>.
        var wholeCue = false;
        var lines = body.Split('\n');
        foreach (var tag in new[] { "i", "b", "u" })
        {
            var open = $"<{tag}>";
            var close = $"</{tag}>";
            var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
            if (nonEmpty.Count > 0 && nonEmpty.All(l =>
                    l.TrimStart().StartsWith(open, StringComparison.OrdinalIgnoreCase) &&
                    l.TrimEnd().EndsWith(close, StringComparison.OrdinalIgnoreCase)) ||
                body.TrimStart().StartsWith(open, StringComparison.OrdinalIgnoreCase) &&
                body.TrimEnd().EndsWith(close, StringComparison.OrdinalIgnoreCase) &&
                CountOccurrences(body, open) == 1)
            {
                prefix += open;
                suffix = close + suffix;
                wholeCue = true;
                break;
            }
        }

        // Otherwise individual lines may carry their own tags ("- Hi\n<i>- Hello</i>").
        var wraps = wholeCue ? null : LineWrapsOf(body, HtmlLeadingTagsRegex(), HtmlTrailingTagsRegex(), HtmlTagRegex());
        var plain = HtmlTagRegex().Replace(body, string.Empty);
        plain = System.Net.WebUtility.HtmlDecode(plain);
        return new TranslatableText(prefix, NormaliseLines(plain), suffix) { LineWraps = wraps };
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(value, i, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; i += value.Length; }
        return count;
    }

    private static string NormaliseLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^(\{[^{}]*\})+")]
    private static partial Regex AssLeadingBlocksRegex();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex AssBlockRegex();

    [GeneratedRegex(@"(\{[^{}]*\})+$")]
    private static partial Regex AssTrailingBlocksRegex();

    [GeneratedRegex(@"^(</?[a-zA-Z][^<>]*>)+")]
    private static partial Regex HtmlLeadingTagsRegex();

    [GeneratedRegex(@"(</?[a-zA-Z][^<>]*>)+$")]
    private static partial Regex HtmlTrailingTagsRegex();

    [GeneratedRegex(@"\{[^{}]*\\p[1-9][^{}]*\}")]
    private static partial Regex AssDrawingRegex();

    [GeneratedRegex(@"\\i1")]
    private static partial Regex AssItalicRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>")]
    private static partial Regex HtmlTagRegex();
}
