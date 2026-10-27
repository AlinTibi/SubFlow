using System.Text.Json.Nodes;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation.Llm;

namespace ALMARFELD.SubFlow.Translation.Providers;

public sealed class OllamaOptions
{
    public required string Model { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int ContextLength { get; init; } = 8192;
    public int BatchSize { get; init; } = 20;
}

/// <summary>
/// Translation with a locally installed Ollama model. Free and offline once the model is installed;
/// SubFlow never pulls models on its own.
/// </summary>
public sealed class OllamaTranslationProvider(OllamaClient client, OllamaOptions options) : ITranslationProvider
{
    private bool? _supportsThinking;

    public string Id => "ollama";
    public string DisplayName => client.IsLocal ? "Ollama (local)" : "Ollama (network)";
    public ProviderCost Cost => ProviderCost.FreeLocal;
    public string ModelId => options.Model;
    public int PreferredBatchSize => Math.Clamp(options.BatchSize, 1, 100);
    public bool SendsTextOffMachine => !client.IsLocal;

    // LLM language coverage depends on the model; SubFlow does not block any language but quality varies.
    public bool SupportsLanguage(Language language, bool asSource) => true;

    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        // Reasoning ("thinking") models are asked to answer directly: faster and keeps JSON clean.
        _supportsThinking ??= (await client.GetCapabilitiesAsync(options.Model, ct)).Contains("thinking");

        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = false,
            ["format"] = LlmPrompt.ResponseSchema(),
            ["keep_alive"] = "10m",
            ["options"] = new JsonObject
            {
                ["temperature"] = options.Temperature,
                ["num_ctx"] = options.ContextLength,
                // A translation is about as long as its source. The cap stops a model that falls into
                // a repetition loop within a token budget rather than letting it write until the timeout.
                ["num_predict"] = MaxOutputTokens(request, options.ContextLength),
            },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = LlmPrompt.System(request) },
                new JsonObject { ["role"] = "user", ["content"] = LlmPrompt.User(request) },
            },
        };
        if (_supportsThinking == true) body["think"] = false;

        var content = await client.ChatAsync(body, ct);
        return LlmResponseParser.Parse(content);
    }

    /// <summary>
    /// Generous output budget: JSON overhead per segment plus several tokens per source character
    /// (scripts such as Thai or Hindi need many tokens per character).
    /// </summary>
    internal static int MaxOutputTokens(TranslationBatchRequest request, int contextLength)
    {
        var chars = request.Segments.Sum(s => (long)s.Text.Length);
        var budget = 256L + request.Segments.Count * 32L + chars * 3;
        return (int)Math.Clamp(budget, 512L, Math.Max(512L, contextLength / 2L));
    }
}
