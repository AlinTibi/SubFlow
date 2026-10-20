using System.Globalization;
using System.Text;

namespace ALMARFELD.SubFlow.Core.Subtitles;

/// <summary>Serialises <see cref="SubtitleDocument"/>s back to their native format (UTF-8).</summary>
public static class SubtitleWriter
{
    public static string Write(SubtitleDocument doc) => doc.Format switch
    {
        SubtitleFormat.Ass => WriteAss(doc),
        SubtitleFormat.WebVtt => WriteVtt(doc),
        _ => WriteSrt(doc),
    };

    /// <summary>Writes UTF-8 with BOM (best compatibility with Windows players and muxers) via a temp file + rename.</summary>
    public static void WriteFile(SubtitleDocument doc, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Write(doc), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(temp, path, overwrite: true);
    }

    private static string WriteSrt(SubtitleDocument doc)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var cue in doc.Cues)
        {
            sb.Append(n++.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(SrtTime(cue.Start)).Append(" --> ").Append(SrtTime(cue.End)).Append("\r\n");
            // An empty line would terminate the cue early; keep empty cues as a single space line.
            var text = string.IsNullOrEmpty(cue.Text) ? " " : cue.Text.Replace("\n", "\r\n");
            sb.Append(text).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    private static string WriteAss(SubtitleDocument doc)
    {
        var format = doc.AssEventFormat ?? ["Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text"];
        var startIdx = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
        var endIdx = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.Append((doc.AssHeader ?? DefaultAssHeader).Replace("\n", "\r\n"));
        if (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) sb.Append("\r\n");
        sb.Append("[Events]\r\n");
        sb.Append("Format: ").Append(string.Join(", ", format)).Append("\r\n");
        foreach (var cue in doc.Cues)
        {
            var fields = cue.AssFields is { } f && f.Length == format.Length - 1
                ? (string[])f.Clone()
                : DefaultFields(format.Length - 1, startIdx, endIdx);
            fields[startIdx] = AssTime(cue.Start);
            fields[endIdx] = AssTime(cue.End);
            sb.Append(cue.AssEventType ?? "Dialogue").Append(": ")
              .Append(string.Join(",", fields)).Append(',')
              .Append(cue.Text.Replace("\n", "\\N", StringComparison.Ordinal))
              .Append("\r\n");
        }
        if (doc.AssTrailer is { Length: > 0 } trailer)
            sb.Append("\r\n").Append(trailer.Replace("\n", "\r\n"));
        return sb.ToString();
    }

    private static string[] DefaultFields(int count, int startIdx, int endIdx)
    {
        var fields = Enumerable.Repeat(string.Empty, count).ToArray();
        if (count >= 9) { fields[0] = "0"; fields[3] = "Default"; fields[5] = "0"; fields[6] = "0"; fields[7] = "0"; }
        fields[startIdx] = fields[endIdx] = string.Empty;
        return fields;
    }

    private const string DefaultAssHeader =
        "[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n" +
        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,10,10,10,1\n";

    private static string WriteVtt(SubtitleDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append((doc.VttHeader ?? "WEBVTT").Replace("\n", "\r\n")).Append("\r\n\r\n");
        foreach (var cue in doc.Cues)
        {
            if (!string.IsNullOrEmpty(cue.VttIdentifier)) sb.Append(cue.VttIdentifier).Append("\r\n");
            sb.Append(VttTime(cue.Start)).Append(" --> ").Append(VttTime(cue.End));
            if (!string.IsNullOrEmpty(cue.VttSettings)) sb.Append(' ').Append(cue.VttSettings);
            sb.Append("\r\n").Append(cue.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    public static string SrtTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");

    public static string VttTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}");

    public static string AssTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
}
