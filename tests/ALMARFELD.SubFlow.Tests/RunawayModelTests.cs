using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation;
using ALMARFELD.SubFlow.Translation.Providers;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>
/// A local model can fall into a repetition loop and never finish its answer. SubFlow caps the
/// answer length and recovers by retrying with smaller requests instead of waiting or failing.
/// </summary>
public class RunawayModelTests
{
    private static readonly Language En = LanguageCatalog.Default.Get("en");
    private static readonly Language Ro = LanguageCatalog.Default.Get("ro");

    /// <summary>Loops (reports "output limit reached") on any request with more than one segment.</summary>
    private sealed class LoopsOnBigBatches : TaggingProvider
    {
        public override Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
        {
            if (request.Segments.Count > 1)
                throw new TranslationProviderException("The model kept writing without finishing the answer (output limit reached).", isTransient: true);
            return base.TranslateBatchAsync(request, ct);
        }
    }

    [Fact]
    public async Task RunawayAnswers_AreRecoveredBySplittingTheBatch()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(8));
        var translator = new DocumentTranslator(new LoopsOnBigBatches { PreferredBatchSize = 8 }, new MemoryCache(),
            new DocumentTranslatorOptions { RetryDelay = TimeSpan.FromMilliseconds(1) });

        var result = await translator.TranslateAsync(
            new DocumentTranslationRequest { Document = doc, Source = En, Target = Ro, Glossary = [] }, null, CancellationToken.None);

        Assert.Equal(8, result.Document.Cues.Count);
        Assert.All(result.Document.Cues, c => Assert.StartsWith("[ro] Line number", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void OutputBudget_ScalesWithTheTextAndStaysWithinTheContext()
    {
        static TranslationBatchRequest Batch(int segments, int chars) => new()
        {
            Segments = Enumerable.Range(1, segments).Select(i => new TranslationSegment(i, new string('a', chars))).ToList(),
            Source = En,
            Target = Ro,
        };

        var small = OllamaTranslationProvider.MaxOutputTokens(Batch(1, 10), 8192);
        var normal = OllamaTranslationProvider.MaxOutputTokens(Batch(20, 50), 8192);
        var huge = OllamaTranslationProvider.MaxOutputTokens(Batch(100, 400), 8192);

        Assert.Equal(512, small);                       // never too small for a short answer
        Assert.InRange(normal, 3000, 4096);             // a normal batch: thousands, not tens of thousands
        Assert.Equal(4096, huge);                       // never more than half the context window
    }
    [Theory]
    [InlineData("length", true)]
    [InlineData("stop", false)]
    public async Task ClientRejectsTruncatedResponses(string reason, bool truncated)
    {
        var client = new OllamaClient("http://localhost:11434", handler: new ReplyHandler(reason));
        if (truncated)
        {
            var ex = await Assert.ThrowsAsync<TranslationProviderException>(() => client.ChatAsync(new(), CancellationToken.None));
            Assert.True(ex.IsTransient);
        }
        else
            Assert.Equal("complete", await client.ChatAsync(new(), CancellationToken.None));
    }

    private sealed class ReplyHandler(string reason) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    done_reason = reason, message = new { content = "complete" },
                })),
            });
    }
}
