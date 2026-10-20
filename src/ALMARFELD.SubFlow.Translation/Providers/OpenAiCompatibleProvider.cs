using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation.Llm;

namespace ALMARFELD.SubFlow.Translation.Providers;

/// <summary>
/// Any server implementing the OpenAI Chat Completions API (<c>POST {base}/chat/completions</c>):
/// OpenAI itself, or local servers such as LM Studio / llama.cpp. Endpoints on this computer are
/// treated as free/local; anything else is a paid cloud service.
/// </summary>
public sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _batchSize;
    private readonly bool _isLocal;
    private readonly string _endpoint;

    public OpenAiCompatibleProvider(string endpoint, string model, string? apiKey, int batchSize = 30, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _batchSize = batchSize;
        _isLocal = Uri.TryCreate(_endpoint, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost");
        if (!_isLocal && !_endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Remote API endpoints must use HTTPS so the API key and subtitle text are encrypted in transit.");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string Id => "openai";
    public string DisplayName => _isLocal ? "OpenAI-compatible (local server)" : "OpenAI-compatible API";
    public ProviderCost Cost => _isLocal ? ProviderCost.FreeLocal : ProviderCost.PaidCloud;
    public bool SendsTextOffMachine => !_isLocal;
    public string ModelId => _model;
    public int PreferredBatchSize => Math.Clamp(_batchSize, 1, 100);
    public bool SupportsLanguage(Language language, bool asSource) => true;

    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = 0.2,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = LlmPrompt.System(request) },
                new JsonObject { ["role"] = "user", ["content"] = LlmPrompt.User(request) },
            },
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(_endpoint + "/chat/completions",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException($"Cannot reach {_endpoint}: {ex.Message}", isTransient: true, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TranslationProviderException("The API did not answer in time.", isTransient: true, ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var transient = resp.StatusCode is HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                var message = resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "The API key was rejected (401).",
                    HttpStatusCode.PaymentRequired => "The API account has no remaining credit (402).",
                    HttpStatusCode.NotFound => $"Model '{_model}' or endpoint not found (404).",
                    HttpStatusCode.TooManyRequests => "Rate limit or quota reached (429).",
                    _ => $"API error {(int)resp.StatusCode}.",
                };
                throw new TranslationProviderException(message, transient);
            }
            string content;
            try
            {
                content = (JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()) ?? "";
            }
            catch (Exception ex)
            {
                throw new TranslationProviderException("The API returned an unreadable response.", isTransient: true, ex);
            }
            return LlmResponseParser.Parse(content);
        }
    }
}
