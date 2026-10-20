namespace ALMARFELD.SubFlow.Core.Subtitles;

public enum SubtitleFormat
{
    Srt,
    Ass,   // Also used for SSA (v4) files; the original header is preserved.
    WebVtt,
}

/// <summary>A single timed subtitle entry.</summary>
public sealed class SubtitleCue
{
    public SubtitleCue(int index, TimeSpan start, TimeSpan end, string text)
    {
        Index = index;
        Start = start;
        End = end;
        Text = text;
    }

    /// <summary>1-based position inside the document. Stable identifier for translation.</summary>
    public int Index { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    /// <summary>
    /// Text in the document's native markup (SRT/VTT HTML-like tags, ASS override blocks),
    /// with line breaks normalised to '\n'.
    /// </summary>
    public string Text { get; set; }

    /// <summary>ASS/SSA: the event's fields in Format order (text field excluded). Null for other formats.</summary>
    public string[]? AssFields { get; init; }

    /// <summary>ASS/SSA: "Dialogue" or "Comment".</summary>
    public string? AssEventType { get; init; }

    /// <summary>WebVTT: optional cue identifier line and cue settings after the timing.</summary>
    public string? VttIdentifier { get; init; }
    public string? VttSettings { get; init; }

    /// <summary>True when this cue carries no translatable dialogue (comments, vector drawings).</summary>
    public bool IsNonDialogue => AssEventType is not null &&
                                 (!AssEventType.Equals("Dialogue", StringComparison.OrdinalIgnoreCase) ||
                                  SubtitleMarkup.IsAssDrawing(Text));

    public SubtitleCue With(string text) => new(Index, Start, End, text)
    {
        AssFields = AssFields,
        AssEventType = AssEventType,
        VttIdentifier = VttIdentifier,
        VttSettings = VttSettings,
    };
}

/// <summary>A parsed subtitle file. Header data is preserved so the file can be written back faithfully.</summary>
public sealed class SubtitleDocument
{
    public SubtitleDocument(SubtitleFormat format, IReadOnlyList<SubtitleCue> cues)
    {
        Format = format;
        Cues = cues;
    }

    public SubtitleFormat Format { get; }
    public IReadOnlyList<SubtitleCue> Cues { get; }

    /// <summary>ASS/SSA: everything before the [Events] section, verbatim.</summary>
    public string? AssHeader { get; init; }

    /// <summary>ASS/SSA: the Format field names of the [Events] section.</summary>
    public string[]? AssEventFormat { get; init; }

    /// <summary>ASS/SSA: any sections after [Events] (e.g. [Fonts], [Graphics]), verbatim.</summary>
    public string? AssTrailer { get; init; }

    /// <summary>WebVTT: header block (the WEBVTT line plus any STYLE/REGION/NOTE blocks).</summary>
    public string? VttHeader { get; init; }

    public string DefaultExtension => Format switch
    {
        SubtitleFormat.Ass => AssHeader?.Contains("[V4 Styles]", StringComparison.OrdinalIgnoreCase) == true ? ".ssa" : ".ass",
        SubtitleFormat.WebVtt => ".vtt",
        _ => ".srt",
    };

    /// <summary>Creates a copy of this document with cue texts replaced. Timing and all metadata are kept.</summary>
    public SubtitleDocument WithTexts(IReadOnlyDictionary<int, string> textsByIndex)
    {
        var cues = Cues.Select(c => textsByIndex.TryGetValue(c.Index, out var t) ? c.With(t) : c.With(c.Text)).ToList();
        return new SubtitleDocument(Format, cues)
        {
            AssHeader = AssHeader,
            AssEventFormat = AssEventFormat,
            AssTrailer = AssTrailer,
            VttHeader = VttHeader,
        };
    }

    /// <summary>Converts to SRT, keeping timing and the plain dialogue (used for sidecar export).</summary>
    public SubtitleDocument ToSrt()
    {
        if (Format == SubtitleFormat.Srt) return this;
        var cues = Cues.Where(c => !c.IsNonDialogue)
            .Select((c, i) => new SubtitleCue(i + 1, c.Start, c.End, SubtitleMarkup.ToSrtMarkup(c.Text, Format)))
            .ToList();
        return new SubtitleDocument(SubtitleFormat.Srt, cues);
    }
}
