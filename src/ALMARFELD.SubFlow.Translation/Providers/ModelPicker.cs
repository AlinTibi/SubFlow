namespace ALMARFELD.SubFlow.Translation.Providers;

/// <summary>
/// Suggests an installed Ollama model for translation. Only installed models are considered;
/// SubFlow never downloads models without the user's action.
/// </summary>
public static class ModelPicker
{
    // Families with comparatively broad multilingual training, most preferred first.
    private static readonly string[] Preference =
    [
        "qwen3", "gemma3", "qwen2.5", "aya-expanse", "aya", "gemma2", "mistral-small", "mistral-nemo",
        "llama3.3", "llama3.1", "llama3.2", "llama3", "mistral", "phi4",
    ];

    private static readonly string[] Unsuitable = ["embed", "coder", "code", "vision", "-vl", "llava", "whisper", "guard"];

    public static bool IsSuitable(OllamaModel m) =>
        !m.IsEmbeddingOnly && !Unsuitable.Any(u => m.Name.Contains(u, StringComparison.OrdinalIgnoreCase));

    public static OllamaModel? PickDefault(IEnumerable<OllamaModel> models)
    {
        var suitable = models.Where(IsSuitable).ToList();
        foreach (var family in Preference)
        {
            var match = suitable.Where(m => m.Name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.SizeBytes).FirstOrDefault();
            if (match is not null) return match;
        }
        return suitable.OrderByDescending(m => m.SizeBytes).FirstOrDefault();
    }

    /// <summary>Models users commonly install for translation; shown as suggestions (never auto-installed).</summary>
    public static readonly IReadOnlyList<string> Suggestions = ["qwen3:8b", "gemma3:12b", "qwen2.5:7b", "aya-expanse:8b"];
}
