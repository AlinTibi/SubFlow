using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ALMARFELD.SubFlow.Core.Subtitles;

public sealed class SubtitleParseException(string message) : Exception(message);

/// <summary>Parses SRT, ASS/SSA and WebVTT text into <see cref="SubtitleDocument"/>s.</summary>
public static partial class SubtitleParser
{
    static SubtitleParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static SubtitleDocument ParseFile(string path)
    {
        var content = ReadTextWithEncodingDetection(path);
        var hint = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => SubtitleFormat.Ass,
            ".vtt" => SubtitleFormat.WebVtt,
            ".srt" => SubtitleFormat.Srt,
            _ => (SubtitleFormat?)null,
        };
        return Parse(content, hint);
    }

    public static SubtitleDocument Parse(string content, SubtitleFormat? hint = null)
    {
        content = content.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        var format = hint ?? Detect(content);
        return format switch
        {
            SubtitleFormat.Ass => ParseAss(content),
            SubtitleFormat.WebVtt => ParseVtt(content),
            _ => ParseSrt(content),
        };
    }

    public static SubtitleFormat Detect(string content)
    {
        var head = content.TrimStart('﻿', ' ', '\n', '\r', '\t');
        if (head.StartsWith("WEBVTT", StringComparison.Ordinal)) return SubtitleFormat.WebVtt;
        if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("[Events]", StringComparison.OrdinalIgnoreCase)) return SubtitleFormat.Ass;
        return SubtitleFormat.Srt;
    }

    /// <summary>
    /// Reads text as UTF-8 (or UTF-16 with BOM). Files that are not valid UTF-8 fall back to the
    /// system ANSI code page, then Windows-1252, so legacy subtitle files remain readable.
    /// </summary>
    public static string ReadTextWithEncodingDetection(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            var ansi = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try { return Encoding.GetEncoding(ansi).GetString(bytes); }
            catch { return Encoding.GetEncoding(1252).GetString(bytes); }
        }
    }

    // ---------------------------------------------------------------- SRT

    private static SubtitleDocument ParseSrt(string content)
    {
        var cues = new List<SubtitleCue>();
        var lines = content.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            // Find the next timing line; an optional numeric counter precedes it.
            var match = SrtTimingRegex().Match(lines[i]);
            if (!match.Success) { i++; continue; }

            var start = ParseSrtTime(match.Groups["s"].Value);
            var end = ParseSrtTime(match.Groups["e"].Value);
            i++;
            var text = new List<string>();
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // A blank line ends the cue unless the next non-blank lines are not a new cue
                    // (some files contain blank lines inside dialogue). Look ahead conservatively.
                    if (IsSrtCueStart(lines, i + 1)) break;
                    var j = i + 1;
                    while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                    if (j >= lines.Length || IsSrtCueStart(lines, j)) { i = j; break; }
                    i++;
                    continue;
                }
                if (SrtTimingRegex().IsMatch(line)) break;
                text.Add(line.TrimEnd());
                i++;
            }
            // Drop a trailing counter line that belongs to the next cue (files with missing blank lines).
            if (text.Count > 0 && int.TryParse(text[^1].Trim(), out _) && i < lines.Length && SrtTimingRegex().IsMatch(lines[i]))
                text.RemoveAt(text.Count - 1);
            cues.Add(new SubtitleCue(cues.Count + 1, start, end, string.Join('\n', text)));
        }
        if (cues.Count == 0 && content.Trim().Length > 0)
            throw new SubtitleParseException("No SRT cues could be read. The file may be corrupt or in an unsupported format.");
        return new SubtitleDocument(SubtitleFormat.Srt, cues);
    }

    private static bool IsSrtCueStart(string[] lines, int i)
    {
        if (i >= lines.Length) return true;
        if (SrtTimingRegex().IsMatch(lines[i])) return true;
        return int.TryParse(lines[i].Trim(), out _) && i + 1 < lines.Length && SrtTimingRegex().IsMatch(lines[i + 1]);
    }

    private static TimeSpan ParseSrtTime(string value)
    {
        // hh:mm:ss,mmm (also tolerates '.' and 1-3 fraction digits, hours above 99).
        var m = SrtTimeRegex().Match(value.Trim());
        if (!m.Success) throw new SubtitleParseException($"Invalid timestamp '{value}'.");
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var frac = m.Groups[4].Value.PadRight(3, '0')[..3];
        var ms = int.Parse(frac, CultureInfo.InvariantCulture);
        return new TimeSpan(0, h, min, s, ms);
    }

    // ---------------------------------------------------------------- ASS / SSA

    private static SubtitleDocument ParseAss(string content)
    {
        var lines = content.Split('\n');
        var header = new StringBuilder();
        var trailer = new StringBuilder();
        string[]? format = null;
        var cues = new List<SubtitleCue>();
        var section = 0; // 0 = before events, 1 = events, 2 = after events

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase)) { section = 1; continue; }
                if (section == 1) section = 2;
            }

            switch (section)
            {
                case 0:
                    header.Append(line).Append('\n');
                    break;
                case 2:
                    trailer.Append(line).Append('\n');
                    break;
                case 1:
                    if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                    {
                        format = trimmed["Format:".Length..].Split(',').Select(f => f.Trim()).ToArray();
                        continue;
                    }
                    var colon = trimmed.IndexOf(':');
                    if (colon <= 0) continue;
                    var type = trimmed[..colon].Trim();
                    if (!type.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) &&
                        !type.Equals("Comment", StringComparison.OrdinalIgnoreCase)) continue;

                    format ??= ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
                    var body = trimmed[(colon + 1)..].TrimStart();
                    // Text is always the last field and may itself contain commas.
                    var parts = body.Split(',', format.Length);
                    if (parts.Length < format.Length) continue;
                    var startIdx = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
                    var endIdx = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
                    if (startIdx < 0 || endIdx < 0) throw new SubtitleParseException("ASS events have no Start/End fields.");
                    var text = parts[^1].Replace("\\N", "\n", StringComparison.Ordinal)
                                        .Replace("\\n", "\n", StringComparison.Ordinal);
                    cues.Add(new SubtitleCue(cues.Count + 1, ParseAssTime(parts[startIdx]), ParseAssTime(parts[endIdx]), text)
                    {
                        AssFields = parts[..^1].Select(p => p.Trim()).ToArray(),
                        AssEventType = type,
                    });
                    break;
            }
        }

        if (format is null && cues.Count == 0)
            throw new SubtitleParseException("The ASS/SSA file has no [Events] section.");

        return new SubtitleDocument(SubtitleFormat.Ass, cues)
        {
            AssHeader = header.ToString().TrimEnd('\n') + "\n",
            AssEventFormat = format,
            AssTrailer = trailer.Length > 0 ? trailer.ToString() : null,
        };
    }

    private static TimeSpan ParseAssTime(string value)
    {
        // h:mm:ss.cc
        var m = AssTimeRegex().Match(value.Trim());
        if (!m.Success) throw new SubtitleParseException($"Invalid ASS timestamp '{value}'.");
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var frac = m.Groups[4].Value.PadRight(3, '0')[..3];
        return new TimeSpan(0, h, min, s, int.Parse(frac, CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- WebVTT

    private static SubtitleDocument ParseVtt(string content)
    {
        var blocks = BlankLinesRegex().Split(content.Trim('\n'));
        if (blocks.Length == 0 || !blocks[0].StartsWith("WEBVTT", StringComparison.Ordinal))
            throw new SubtitleParseException("Missing WEBVTT header.");

        var header = new StringBuilder(blocks[0]);
        var cues = new List<SubtitleCue>();
        foreach (var block in blocks.Skip(1))
        {
            var lines = block.Split('\n');
            var timingLine = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timingLine < 0 || timingLine > 1)
            {
                // STYLE, REGION and NOTE blocks before the first cue belong to the header.
                if (cues.Count == 0) header.Append("\n\n").Append(block);
                continue;
            }
            var m = VttTimingRegex().Match(lines[timingLine]);
            if (!m.Success) continue;
            cues.Add(new SubtitleCue(cues.Count + 1, ParseVttTime(m.Groups["s"].Value), ParseVttTime(m.Groups["e"].Value),
                string.Join('\n', lines.Skip(timingLine + 1)))
            {
                VttIdentifier = timingLine == 1 ? lines[0] : null,
                VttSettings = m.Groups["set"].Value.Trim() is { Length: > 0 } set ? set : null,
            });
        }
        return new SubtitleDocument(SubtitleFormat.WebVtt, cues) { VttHeader = header.ToString() };
    }

    private static TimeSpan ParseVttTime(string value)
    {
        var parts = value.Trim().Split(':');
        int h = 0, m, s, ms;
        string secPart;
        if (parts.Length == 3) { h = int.Parse(parts[0], CultureInfo.InvariantCulture); m = int.Parse(parts[1], CultureInfo.InvariantCulture); secPart = parts[2]; }
        else if (parts.Length == 2) { m = int.Parse(parts[0], CultureInfo.InvariantCulture); secPart = parts[1]; }
        else throw new SubtitleParseException($"Invalid WebVTT timestamp '{value}'.");
        var sp = secPart.Split('.');
        s = int.Parse(sp[0], CultureInfo.InvariantCulture);
        ms = sp.Length > 1 ? int.Parse(sp[1].PadRight(3, '0')[..3], CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(0, h, m, s, ms);
    }

    [GeneratedRegex(@"^\s*(?<s>\d+:\d{1,2}:\d{1,2}(?:[,.]\d{1,3})?)\s*-->\s*(?<e>\d+:\d{1,2}:\d{1,2}(?:[,.]\d{1,3})?)")]
    private static partial Regex SrtTimingRegex();

    [GeneratedRegex(@"^(\d+):(\d{1,2}):(\d{1,2})(?:[,.](\d{1,3}))?$")]
    private static partial Regex SrtTimeRegex();

    [GeneratedRegex(@"^(\d+):(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$")]
    private static partial Regex AssTimeRegex();

    [GeneratedRegex(@"^\s*(?<s>(?:\d+:)?\d{2}:\d{2}(?:\.\d{1,3})?)\s*-->\s*(?<e>(?:\d+:)?\d{2}:\d{2}(?:\.\d{1,3})?)(?<set>.*)$")]
    private static partial Regex VttTimingRegex();

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLinesRegex();
}
