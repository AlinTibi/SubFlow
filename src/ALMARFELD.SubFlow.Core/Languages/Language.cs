namespace ALMARFELD.SubFlow.Core.Languages;

/// <summary>
/// A subtitle language. Codes follow ISO 639-1 (two letters) and ISO 639-2
/// (bibliographic "B" form used by FFmpeg/Matroska, terminology "T" form).
/// </summary>
public sealed record Language(
    string Name,
    string NativeName,
    string Iso6391,
    string Iso6392B,
    string Iso6392T,
    bool IsRightToLeft = false,
    string? DeepLCode = null,
    string? GoogleCode = null)
{
    /// <summary>Stable identifier used in settings and job records (ISO 639-1 when available).</summary>
    public string Code => Iso6391;

    /// <summary>Code written into container track metadata (ISO 639-2/B, what FFmpeg and mkvmerge expect).</summary>
    public string ContainerCode => Iso6392B;

    public string DisplayName => string.Equals(Name, NativeName, StringComparison.Ordinal)
        ? Name
        : $"{Name} — {NativeName}";

    public bool Matches(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        // Accept BCP-47 style tags such as "pt-BR" or "zh-Hans" by their primary subtag.
        var primary = code.Split('-', '_')[0];
        return primary.Equals(Iso6391, StringComparison.OrdinalIgnoreCase)
            || primary.Equals(Iso6392B, StringComparison.OrdinalIgnoreCase)
            || primary.Equals(Iso6392T, StringComparison.OrdinalIgnoreCase)
            || code.Equals(Name, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Name;
}
