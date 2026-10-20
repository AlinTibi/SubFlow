using System.Net;
using System.Text;
using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation;
using ALMARFELD.SubFlow.Translation.Providers;

namespace ALMARFELD.SubFlow.Tests;

public class TranslatorTests
{
    private static readonly Language En = LanguageCatalog.Default.Get("en");
    private static readonly Language Ro = LanguageCatalog.Default.Get("ro");
    private static readonly DocumentTranslatorOptions Fast = new() { RetryDelay = TimeSpan.FromMilliseconds(1) };

    private static DocumentTranslationRequest Request(SubtitleDocument doc, Language? target = null, IReadOnlyList<GlossaryEntry>? glossary = null) =>
        new() { Document = doc, Source = En, Target = target ?? Ro, Glossary = glossary ?? Array.Empty<GlossaryEntry>() };

    [Fact]
    public async Task Translate_PreservesTimingCountAndMarkup()
    {
        var doc = SubtitleParser.Parse(TestMedia.EnglishSrt);
        var translator = new DocumentTranslator(new TaggingProvider(), new MemoryCache(), Fast);
        var result = await translator.TranslateAsync(Request(doc), null, CancellationToken.None);

        Assert.Equal(doc.Cues.Count, result.Document.Cues.Count);
        for (var i = 0; i < doc.Cues.Count; i++)
        {
            Assert.Equal(doc.Cues[i].Start, result.Document.Cues[i].Start);
            Assert.Equal(doc.Cues[i].End, result.Document.Cues[i].End);
        }
        Assert.Equal("[ro] Goku, are you ready?", result.Document.Cues[0].Text);
        Assert.Equal("<i>[ro] Kamehameha!</i>", result.Document.Cues[2].Text); // italics kept around translation
        Assert.Equal("[ro] I was born ready!\nLet's go.", result.Document.Cues[1].Text);
        // Long single-line text is fully preserved (no truncation).
        Assert.Contains("handled by the translator.", result.Document.Cues[3].Text);
    }

    [Fact]
    public async Task Translate_RetriesMissingSegments()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(10));
        var provider = new FlakyProvider(1, r => r.Segments.Where(s => s.Id % 3 == 0).Select(s => s.Id));
        var result = await new DocumentTranslator(provider, null, Fast).TranslateAsync(Request(doc), null, CancellationToken.None);
        Assert.All(result.Document.Cues, c => Assert.StartsWith("[ro] ", c.Text));
        Assert.Equal(2, provider.Calls); // first call + one retry for missing ids only
    }

    [Fact]
    public async Task Translate_NeverInventsText_WhenEngineKeepsFailing()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(6) + "7\n00:00:09,000 --> 00:00:10,000\nPOISON line\n");
        var translator = new DocumentTranslator(new StubbornProvider("POISON"), null, Fast);
        var ex = await Assert.ThrowsAsync<IncompleteTranslationException>(() =>
            translator.TranslateAsync(Request(doc), null, CancellationToken.None));
        Assert.Equal([7], ex.MissingIds);
    }

    [Fact]
    public async Task Translate_NonTransientProviderError_FailsWithoutEndlessRetries()
    {
        var doc = SubtitleParser.Parse(TestMedia.EnglishSrt);
        var provider = new ThrowingProvider(transient: false);
        await Assert.ThrowsAsync<TranslationProviderException>(() =>
            new DocumentTranslator(provider, null, Fast).TranslateAsync(Request(doc), null, CancellationToken.None));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Translate_UsesCacheOnSecondRun()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(25));
        var cache = new MemoryCache();
        var first = new TaggingProvider();
        await new DocumentTranslator(first, cache, Fast).TranslateAsync(Request(doc), null, CancellationToken.None);
        Assert.True(first.Calls >= 3);

        var second = new TaggingProvider();
        var result = await new DocumentTranslator(second, cache, Fast).TranslateAsync(Request(doc), null, CancellationToken.None);
        Assert.Equal(0, second.Calls);
        Assert.Equal(25, result.CachedSegments);
    }

    [Fact]
    public async Task Translate_InterruptedRun_ResumesFromCache()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(40));
        var cache = new MemoryCache();
        using var cts = new CancellationTokenSource();
        var provider = new TaggingProvider { PreferredBatchSize = 10 };
        provider.BeforeEachCall = ct => { if (provider.Calls == 3) cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DocumentTranslator(provider, cache, Fast).TranslateAsync(Request(doc), null, cts.Token));
        Assert.Equal(20, cache.Data.Count); // two complete batches survived the interruption

        var resume = new TaggingProvider { PreferredBatchSize = 10 };
        var result = await new DocumentTranslator(resume, cache, Fast).TranslateAsync(Request(doc), null, CancellationToken.None);
        Assert.Equal(2, resume.Calls);
        Assert.Equal(20, result.CachedSegments);
        Assert.All(result.Document.Cues, c => Assert.StartsWith("[ro] ", c.Text));
    }

    [Fact]
    public async Task Translate_RejectsWrongScript()
    {
        var doc = SubtitleParser.Parse(TestMedia.EnglishSrt);
        // The tagging engine returns Latin text; Russian output must be rejected.
        var translator = new DocumentTranslator(new TaggingProvider(), null, Fast);
        await Assert.ThrowsAsync<IncompleteTranslationException>(() =>
            translator.TranslateAsync(Request(doc, LanguageCatalog.Default.Get("ru")), null, CancellationToken.None));
    }

    [Fact]
    public async Task Translate_ContextAndGlossaryAreSentToEngine()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(12) + "13\n00:00:09,000 --> 00:00:10,000\nKamehameha!\n");
        var seen = new List<TranslationBatchRequest>();
        var provider = new RecordingProvider(seen) { PreferredBatchSize = 5 };
        var glossary = new[] { new GlossaryEntry { Source = "Kamehameha", Protected = true } };
        await new DocumentTranslator(provider, null, Fast).TranslateAsync(Request(doc, glossary: glossary), null, CancellationToken.None);
        Assert.Empty(seen[0].PrecedingContext);
        Assert.Equal(4, seen[1].PrecedingContext.Count);
        Assert.NotNull(seen[1].PrecedingContext[0].Translation);
        Assert.Contains(seen, r => r.Glossary.Any(g => g.Source == "Kamehameha"));
    }

    [Fact]
    public async Task Translate_MultipleTargets_AreIndependentDirectTranslations()
    {
        var doc = SubtitleParser.Parse(TestMedia.EnglishSrt);
        var translator = new DocumentTranslator(new TaggingProvider(), new MemoryCache(), Fast);
        foreach (var code in new[] { "ro", "fr", "de", "es", "it" })
        {
            var result = await translator.TranslateAsync(Request(doc, LanguageCatalog.Default.Get(code)), null, CancellationToken.None);
            Assert.StartsWith($"[{code}] ", result.Document.Cues[0].Text);
        }
    }

    [Fact]
    public async Task Translate_DroppedSecondLine_IsRetried()
    {
        var doc = SubtitleParser.Parse("1\n00:00:01,000 --> 00:00:02,000\nI was born ready!\nLet's go, everyone.\n");
        var provider = new FlakyLinesProvider();
        var result = await new DocumentTranslator(provider, null, Fast).TranslateAsync(Request(doc), null, CancellationToken.None);
        Assert.Equal(2, provider.Calls);
        Assert.Equal("[ro] I was born ready!\nLet's go, everyone.", result.Document.Cues[0].Text);
    }

    private sealed class FlakyLinesProvider : TaggingProvider
    {
        public override async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
        {
            var r = await base.TranslateBatchAsync(request, ct);
            // First answer drops the second line, like the real model did.
            return Calls == 1 ? r.ToDictionary(kv => kv.Key, kv => kv.Value.Split('\n')[0]) : r;
        }
    }

    [Fact]
    public void LineBalancer_SplitsLongSingleLineWhenSourceHadTwoLines()
    {
        var balanced = TranslationQuality.BalanceLines("Short one\nShort two", "Aceasta este o propoziție lungă care ar trebui să fie împărțită");
        Assert.Contains('\n', balanced);
        Assert.Equal("Aceasta este o propoziție lungă care ar trebui să fie împărțită", balanced.Replace("\n", " "));
    }

    [Fact]
    public async Task OpenAiProvider_SendsBearerKeyAndParsesJson()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal("Bearer sk-test", req.Headers.Authorization!.ToString());
            Assert.Equal("https://api.example.com/v1/chat/completions", req.RequestUri!.ToString());
            return """{"choices":[{"message":{"content":"{\"translations\":[{\"id\":1,\"text\":\"Salut\"}]}"}}]}""";
        });
        var provider = new OpenAiCompatibleProvider("https://api.example.com/v1", "m", "sk-test", handler: handler);
        Assert.Equal(ProviderCost.PaidCloud, provider.Cost);
        var result = await provider.TranslateBatchAsync(new TranslationBatchRequest { Segments = [new(1, "Hello")], Source = En, Target = Ro }, CancellationToken.None);
        Assert.Equal("Salut", result[1]);
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleProvider("http://api.example.com/v1", "m", "k"));
        Assert.Equal(ProviderCost.FreeLocal, new OpenAiCompatibleProvider("http://localhost:1234/v1", "m", null).Cost);
    }

    [Fact]
    public async Task DeepLProvider_UsesFreeHostForFxKeys_AndMapsResults()
    {
        var handler = new StubHandler(req =>
        {
            Assert.Equal("api-free.deepl.com", req.RequestUri!.Host);
            Assert.Equal("DeepL-Auth-Key abc:fx", req.Headers.GetValues("Authorization").Single());
            var body = req.Content!.ReadAsStringAsync().Result;
            Assert.Contains("\"target_lang\":\"RO\"", body);
            return """{"translations":[{"text":"Unu"},{"text":"Doi"}]}""";
        });
        var provider = new DeepLProvider("abc:fx", handler: handler);
        var result = await provider.TranslateBatchAsync(new TranslationBatchRequest { Segments = [new(1, "One"), new(2, "Two")], Source = En, Target = Ro }, CancellationToken.None);
        Assert.Equal("Doi", result[2]);
        Assert.False(provider.SupportsLanguage(LanguageCatalog.Default.Get("hi"), false));
    }

    [Fact]
    public async Task GoogleProvider_SendsKeyInHeaderNotUrl()
    {
        var handler = new StubHandler(req =>
        {
            Assert.DoesNotContain("key=", req.RequestUri!.Query);
            Assert.Equal("gkey", req.Headers.GetValues("X-Goog-Api-Key").Single());
            return """{"data":{"translations":[{"translatedText":"Bun &amp; frumos"}]}}""";
        });
        var result = await new GoogleTranslateProvider("gkey", handler).TranslateBatchAsync(
            new TranslationBatchRequest { Segments = [new(1, "Good & nice")], Source = En, Target = Ro }, CancellationToken.None);
        Assert.Equal("Bun & frumos", result[1]);
    }

    private sealed class RecordingProvider(List<TranslationBatchRequest> seen) : TaggingProvider
    {
        public override Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
        {
            seen.Add(request);
            return base.TranslateBatchAsync(request, ct);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
    }
}
