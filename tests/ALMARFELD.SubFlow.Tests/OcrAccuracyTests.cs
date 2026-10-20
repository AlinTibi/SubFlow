using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Ocr;
using Windows.Media.Ocr;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>OCR accuracy on rendered Blu-ray-sized lines with punctuation, apostrophes and two-line dialogue.</summary>
public class OcrAccuracyTests
{
    [SkippableTheory]
    [InlineData("Thank you very much!")]
    [InlineData("Wait! Is that you, Sarah?")]
    [InlineData("I'll be there at 10:30, okay?")]
    [InlineData("- Run!\n- Where?")]
    [InlineData("Don't worry, it's fine.\nWe'll find them.")]
    [InlineData("\"Quick,\" she said. \"Hide!\"")]
    public async Task RecognisesSubtitleLinesExactly(string text)
    {
        Skip.IfNot(new WindowsPgsOcrEngine(TestMedia.Tools).SupportsLanguage(LanguageCatalog.Default.Get("en")), "Windows English OCR not installed");
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(
            OcrEngine.AvailableRecognizerLanguages.First(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)).LanguageTag))!;
        using var dir = new TempDir();
        PgsTestWriter.Write(dir.File("a.sup"), [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), text)]);
        var image = Assert.Single(PgsReader.Read(dir.File("a.sup")));
        Assert.Equal(text, await WindowsPgsOcrEngine.RecognizeAsync(engine, image));
    }
}
