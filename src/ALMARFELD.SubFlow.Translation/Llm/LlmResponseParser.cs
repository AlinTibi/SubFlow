using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALMARFELD.SubFlow.Translation.Llm;

/// <summary>
/// Tolerant parser for model replies. Accepts the requested shape plus common deviations
/// (bare arrays, id→text objects, code fences, reasoning blocks). Returns only what it can
/// read with confidence; validation of completeness happens in the document translator.
/// </summary>
public static partial class LlmResponseParser
{
    public static Dictionary<int, string> Parse(string content)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var text = ThinkRegex().Replace(content, "").Trim();
        text = FenceRegex().Replace(text, m => m.Groups[1].Value).Trim();

        JsonDocument? doc = TryParse(text);
        if (doc is null)
        {
            // Extract the outermost JSON object/array if the model wrapped it in prose.
            var start = text.IndexOfAny(['{', '[']);
            var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
            if (start >= 0 && end > start) doc = TryParse(text[start..(end + 1)]);
        }
        if (doc is null) return result;

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetArray(root, out var arr)) ReadArray(arr, result);
                else
                {
                    // {"1": "text", "2": "text"}
                    foreach (var p in root.EnumerateObject())
                        if (int.TryParse(p.Name, out var id) && ReadText(p.Value) is { } t) result[id] = t;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                ReadArray(root, result);
            }
        }
        return result;
    }

    private static bool TryGetArray(JsonElement obj, out JsonElement array)
    {
        foreach (var name in new[] { "translations", "segments", "subtitles", "items", "result" })
        {
            foreach (var p in obj.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                {
                    array = p.Value;
                    return true;
                }
        }
        array = default;
        return false;
    }

    private static void ReadArray(JsonElement arr, Dictionary<int, string> result)
    {
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            int? id = null;
            string? text = null;
            foreach (var p in item.EnumerateObject())
            {
                if (p.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) id = n;
                    else if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var s)) id = s;
                }
                else if (p.Name is "text" or "translation" or "translated" or "Text")
                {
                    text = ReadText(p.Value);
                }
            }
            if (id is { } i && text is not null && !result.ContainsKey(i)) result[i] = text;
        }
    }

    private static string? ReadText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Array => string.Join('\n', e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
        _ => null,
    };

    private static JsonDocument? TryParse(string s)
    {
        try { return JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkRegex();

    [GeneratedRegex(@"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FenceRegex();
}
