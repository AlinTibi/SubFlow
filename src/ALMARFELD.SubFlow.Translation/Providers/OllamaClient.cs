using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ALMARFELD.SubFlow.Core.Translation;

namespace ALMARFELD.SubFlow.Translation.Providers;

public sealed record OllamaModel(string Name, string? ParameterSize, string? Family, long SizeBytes, IReadOnlyList<string> Capabilities)
{
    public bool IsEmbeddingOnly => Capabilities.Count > 0 && Capabilities.All(c => c == "embedding");
    public string Display => ParameterSize is { Length: > 0 } p ? $"{Name}  ({p})" : Name;
}

public sealed record OllamaStatus(bool IsRunning, string? Version, IReadOnlyList<OllamaModel> Models, string? Error);

/// <summary>Minimal client for the documented Ollama REST API (https://github.com/ollama/ollama/blob/main/docs/api.md).</summary>
public sealed class OllamaClient
{
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _handler;

    public OllamaClient(string endpoint, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _handler = handler;
        Endpoint = endpoint.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(Endpoint + "/");
        _http.Timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    public string Endpoint { get; }

    /// <summary>Is the endpoint on this computer? Remote Ollama servers receive subtitle text over the network.</summary>
    public bool IsLocal => Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    public async Task<OllamaStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var version = await _http.GetFromJsonAsync<JsonObject>("api/version", cts.Token);
            var models = await ListModelsAsync(ct);
            return new OllamaStatus(true, version?["version"]?.GetValue<string>(), models, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return new OllamaStatus(false, null, Array.Empty<OllamaModel>(),
                $"Ollama is not reachable at {Endpoint}. Start Ollama (or install it from https://ollama.com/download).");
        }
    }

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct)
    {
        var tags = await _http.GetFromJsonAsync<JsonObject>("api/tags", ct);
        var list = new List<OllamaModel>();
        if (tags?["models"] is JsonArray arr)
        {
            foreach (var m in arr.OfType<JsonObject>())
            {
                var caps = (m["capabilities"] as JsonArray)?.Select(c => c?.GetValue<string>() ?? "").Where(c => c.Length > 0).ToList()
                           ?? new List<string>();
                var details = m["details"] as JsonObject;
                list.Add(new OllamaModel(
                    m["name"]?.GetValue<string>() ?? "",
                    details?["parameter_size"]?.GetValue<string>(),
                    details?["family"]?.GetValue<string>(),
                    m["size"]?.GetValue<long>() ?? 0,
                    caps));
            }
        }
        return list.Where(m => m.Name.Length > 0).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Downloads a model from the Ollama library (POST /api/pull, streamed), reporting progress as
    /// a fraction 0–1 together with Ollama's status text. Large models take many minutes, so this
    /// call has no timeout of its own; cancel it with <paramref name="ct"/>.
    /// </summary>
    public async Task PullAsync(string model, IProgress<(double Fraction, string Status)>? progress, CancellationToken ct)
    {
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.BaseAddress = _http.BaseAddress;
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true }),
        };
        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new TranslationProviderException($"Ollama could not download {model} ({(int)resp.StatusCode}).", isTransient: false);

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        var succeeded = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var obj = JsonNode.Parse(line) as JsonObject;
            if (obj?["error"]?.GetValue<string>() is { } error)
                throw new TranslationProviderException($"Ollama could not download {model}: {error}", isTransient: false);
            var status = obj?["status"]?.GetValue<string>() ?? "";
            if (status == "success") succeeded = true;
            var total = obj?["total"]?.GetValue<long>() ?? 0;
            var done = obj?["completed"]?.GetValue<long>() ?? 0;
            progress?.Report((total > 0 ? Math.Clamp(done / (double)total, 0, 1) : status == "success" ? 1 : -1, status));
        }
        if (!succeeded)
            throw new TranslationProviderException($"Ollama download of {model} ended without confirming success.", isTransient: true);
    }

    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(string model, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/show", new { model }, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();
            var obj = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            return (obj?["capabilities"] as JsonArray)?.Select(c => c?.GetValue<string>() ?? "").ToList() ?? new List<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>Non-streaming chat call. Returns the assistant message content.</summary>
    public async Task<string> ChatAsync(JsonObject body, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync("api/chat",
                new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException($"Cannot reach Ollama at {Endpoint}. Is it running?", isTransient: false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TranslationProviderException("Ollama did not answer in time (timeout). Try a smaller batch size or model.", isTransient: true, ex);
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = TryReadError(text) ?? resp.ReasonPhrase;
                var notFound = resp.StatusCode == HttpStatusCode.NotFound || error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;
                throw new TranslationProviderException(
                    notFound ? $"Model not installed in Ollama: {error}" : $"Ollama error ({(int)resp.StatusCode}): {error}",
                    isTransient: !notFound && (int)resp.StatusCode >= 500);
            }
            try
            {
                var obj = JsonNode.Parse(text) as JsonObject;
                // "length" means the output budget ran out: usually a model stuck repeating itself.
                if (obj?["done_reason"]?.GetValue<string>() == "length")
                    throw new TranslationProviderException("The model kept writing without finishing the answer (output limit reached).", isTransient: true);
                return obj?["message"]?["content"]?.GetValue<string>() ?? "";
            }
            catch (JsonException ex)
            {
                throw new TranslationProviderException("Ollama returned an unreadable response.", isTransient: true, ex);
            }
        }
    }

    private static string? TryReadError(string body)
    {
        try { return (JsonNode.Parse(body) as JsonObject)?["error"]?.GetValue<string>(); }
        catch { return string.IsNullOrWhiteSpace(body) ? null : body.Trim(); }
    }
}
