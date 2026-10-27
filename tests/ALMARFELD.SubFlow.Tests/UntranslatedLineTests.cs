using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>
/// Lines a model returns unchanged: names, interjections and lyrics in a third language are fine;
/// ordinary source-language sentences are not (they must be retried, not silently kept).
/// </summary>
public class UntranslatedLineTests
{
    private static readonly Language En = LanguageCatalog.Default.Get("en");
    private static readonly Language Ro = LanguageCatalog.Default.Get("ro");

    [Theory]
    [InlineData("So, we finally meet.")]
    [InlineData("What was his objective?")]
    [InlineData("You've grown, huh?")]
    [InlineData("It's here!")]
    [InlineData("Th-That was me?")]
    [InlineData("I wonder if it's Yamucha.")]
    public void CopiedSentence_IsUntranslated(string line) =>
        Assert.Equal(SegmentVerdict.Untranslated, TranslationQuality.Check(line, line, En, Ro));

    [Theory]
    [InlineData("Gohan!")]
    [InlineData("Kakarrot?!")]
    [InlineData("Huh?")]
    [InlineData("Mm-hmm.")]
    [InlineData("CHA-LA-HEAD-CHA-LA")]
    [InlineData("Karadajuu ni hirogaru panorama")]
    [InlineData("Fly Away (Fly Away)")]
    public void CopiedNameOrLyrics_IsAccepted(string line) =>
        Assert.Equal(SegmentVerdict.Ok, TranslationQuality.Check(line, line, En, Ro));

    /// <summary>Copies the English text back when asked for several lines at once.</summary>
    private sealed class CopiesBigBatches : TaggingProvider
    {
        public override async Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct)
        {
            if (request.Segments.Count > 2)
                return request.Segments.ToDictionary(s => s.Id, s => s.Text);
            return await base.TranslateBatchAsync(request, ct);
        }
    }

    [Fact]
    public async Task ModelCopyingEnglish_IsRetriedInSmallerRequests_AndNothingStaysEnglish()
    {
        var doc = SubtitleParser.Parse(TestMedia.SampleSrt(12));
        var translator = new DocumentTranslator(new CopiesBigBatches { PreferredBatchSize = 12 }, new MemoryCache(),
            new DocumentTranslatorOptions { RetryDelay = TimeSpan.FromMilliseconds(1) });

        var result = await translator.TranslateAsync(
            new DocumentTranslationRequest { Document = doc, Source = En, Target = Ro, Glossary = [] }, null, CancellationToken.None);

        Assert.All(result.Document.Cues, c => Assert.StartsWith("[ro] ", c.Text, StringComparison.Ordinal));
        Assert.Empty(result.Warnings);
    }
}
