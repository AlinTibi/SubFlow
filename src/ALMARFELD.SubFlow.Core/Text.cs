using System.Globalization;

namespace ALMARFELD.SubFlow.Core;

/// <summary>Small helpers for the English status messages produced by the pipeline.</summary>
public static class Text
{
    /// <summary>"1 subtitle language", "4 subtitle languages".</summary>
    public static string Count(int count, string singular, string? plural = null) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");
}
