using System.Net;
using System.Text;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation.Providers;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>Model downloads from the Setup page: streamed progress and clear errors (simulated Ollama).</summary>
public class OllamaPullTests
{
    private sealed class FakeOllama(string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? RequestBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
        }
    }

    private sealed class ListProgress : IProgress<(double Fraction, string Status)>
    {
        public List<(double Fraction, string Status)> Items { get; } = new();
        public void Report((double Fraction, string Status) value) => Items.Add(value);
    }

    [Fact]
    public async Task Pull_ReportsProgressUntilSuccess()
    {
        var stream = string.Join("\n",
            """{"status":"pulling manifest"}""",
            """{"status":"pulling abc","total":1000,"completed":250}""",
            """{"status":"pulling abc","total":1000,"completed":1000}""",
            """{"status":"success"}""");
        var handler = new FakeOllama(stream);
        var progress = new ListProgress();

        await new OllamaClient("http://127.0.0.1:11434", handler: handler).PullAsync("qwen3:8b", progress, CancellationToken.None);

        Assert.Contains("\"model\":\"qwen3:8b\"", handler.RequestBody);
        Assert.Equal([-1, 0.25, 1.0, 1.0], progress.Items.Select(p => p.Fraction));
        Assert.Equal("success", progress.Items[^1].Status);
    }

    [Fact]
    public async Task Pull_UnknownModel_GivesAClearError()
    {
        var handler = new FakeOllama("""{"error":"pull model manifest: file does not exist"}""");
        var ex = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            new OllamaClient("http://127.0.0.1:11434", handler: handler).PullAsync("nope:1b", null, CancellationToken.None));
        Assert.Contains("nope:1b", ex.Message);
        Assert.Contains("file does not exist", ex.Message);
    }
    [Theory]
    [InlineData("")]
    [InlineData("{\"status\":\"pulling manifest\"}")]
    public async Task Pull_InterruptedStream_IsNotReportedAsSuccessful(string body)
    {
        var ex = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            new OllamaClient("http://localhost:11434", handler: new FakeOllama(body)).PullAsync("qwen3:8b", null, CancellationToken.None));
        Assert.True(ex.IsTransient);
        Assert.Contains("without confirming success", ex.Message);
    }
}
