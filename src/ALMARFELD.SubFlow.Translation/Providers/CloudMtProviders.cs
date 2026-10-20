using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Translation;

namespace ALMARFELD.SubFlow.Translation.Providers;

/// <summary>
/// DeepL API v2 (https://developers.deepl.com/docs/api-reference/translate). Paid/quota-based cloud service:
/// subtitle text is sent to DeepL. Glossary terms are checked after translation (warnings), not enforced.
/// </summary>
public sealed class DeepLProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public DeepLProvider(string apiKey, bool? useFreeEndpoint = null, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A DeepL API key is required.");
        // DeepL Free keys end with ":fx" and must use the api-free host.
        var free = useFreeEndpoint ?? apiKey.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
        _endpoint = new Uri(free ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey.Trim());
    }

    public string Id => "deepl";
    public string DisplayName => "DeepL API";
    public ProviderCost Cost => ProviderCost.PaidCloud;
    public bool SendsTextOffMachine => true;
    public string ModelId => "deepl-v2";
    public int PreferredBatchSize => 40;

    public bool SupportsLanguage(Language language, bool asSource) => language.DeepLCode is not null;

    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        var target = request.Target.DeepLCode ?? throw new TranslationProviderException($"DeepL does not support {request.Target.Name}.", false);
        var source = request.Source.DeepLCode?.Split('-')[0];
        var texts = new JsonArray();
        foreach (var s in request.Segments) texts.Add(s.Text);
        var body = new JsonObject
        {
            ["text"] = texts,
            ["target_lang"] = target,
            ["preserve_formatting"] = true,
        };
        if (source is not null) body["source_lang"] = source;
        if (request.PrecedingContext.Count > 0)
            body["context"] = string.Join("\n", request.PrecedingContext.Select(c => c.Source));

        var json = await PostAsync(_http, _endpoint, body, "DeepL", ct);
        var arr = json["translations"] as JsonArray
                  ?? throw new TranslationProviderException("DeepL returned an unexpected response.", true);
        var result = new Dictionary<int, string>();
        for (var i = 0; i < arr.Count && i < request.Segments.Count; i++)
            if (arr[i]?["text"]?.GetValue<string>() is { } t) result[request.Segments[i].Id] = t;
        return result;
    }

    internal static async Task<JsonObject> PostAsync(HttpClient http, Uri uri, JsonObject body, string name, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(uri, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException($"Cannot reach {name}: {ex.Message}", true, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TranslationProviderException($"{name} did not answer in time.", true, ex);
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                var message = code switch
                {
                    401 or 403 => $"{name} rejected the API key ({code}).",
                    456 => $"{name} quota exceeded (456).",
                    429 => $"{name} rate limit reached (429).",
                    400 => $"{name} rejected the request (400): {Truncate(text)}",
                    _ => $"{name} error {code}.",
                };
                throw new TranslationProviderException(message, isTransient: code == 429 || code >= 500);
            }
            try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
            catch (Exception ex) { throw new TranslationProviderException($"{name} returned unreadable JSON.", true, ex); }
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// Google Cloud Translation API — Basic (v2) (https://cloud.google.com/translate/docs/reference/rest/v2/translate).
/// Paid cloud service. The API key is sent in the X-Goog-Api-Key header, never in the URL.
/// </summary>
public sealed class GoogleTranslateProvider : ITranslationProvider
{
    private static readonly Uri Endpoint = new("https://translation.googleapis.com/language/translate/v2");
    private readonly HttpClient _http;

    public GoogleTranslateProvider(string apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("A Google Cloud API key is required.");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(2);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Goog-Api-Key", apiKey.Trim());
    }

    public string Id => "google";
    public string DisplayName => "Google Cloud Translation";
    public ProviderCost Cost => ProviderCost.PaidCloud;
    public bool SendsTextOffMachine => true;
    public string ModelId => "google-v2-nmt";
    public int PreferredBatchSize => 50;
    public bool SupportsLanguage(Language language, bool asSource) => language.GoogleCode is not null;

    public async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
    {
        var q = new JsonArray();
        foreach (var s in request.Segments) q.Add(s.Text);
        var body = new JsonObject
        {
            ["q"] = q,
            ["target"] = request.Target.GoogleCode,
            ["source"] = request.Source.GoogleCode,
            ["format"] = "text",
        };
        var json = await DeepLProvider.PostAsync(_http, Endpoint, body, "Google Cloud Translation", ct);
        var arr = json["data"]?["translations"] as JsonArray
                  ?? throw new TranslationProviderException("Google returned an unexpected response.", true);
        var result = new Dictionary<int, string>();
        for (var i = 0; i < arr.Count && i < request.Segments.Count; i++)
            if (arr[i]?["translatedText"]?.GetValue<string>() is { } t) result[request.Segments[i].Id] = WebUtility.HtmlDecode(t);
        return result;
    }
}
