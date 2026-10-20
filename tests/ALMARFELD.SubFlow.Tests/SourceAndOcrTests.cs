using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Infrastructure.Media;
using ALMARFELD.SubFlow.Infrastructure.Tools;
using ALMARFELD.SubFlow.Ocr;
using ALMARFELD.SubFlow.Translation;

namespace ALMARFELD.SubFlow.Tests;

public class SidecarAndMarkupTests
{
    private static SidecarSubtitle Parse(string name) =>
        SidecarFinder.Parse(name, Path.GetFileNameWithoutExtension(name).Split('.').Skip(1).ToList(), LanguageCatalog.Default);

    [Theory]
    [InlineData("Movie.srt", null, false, false)]
    [InlineData("Movie.en.srt", "en", false, false)]
    [InlineData("Movie.eng.sdh.srt", "en", false, true)]
    [InlineData("Movie.English.forced.ass", "en", true, false)]
    [InlineData("Movie.hi.srt", "hi", false, false)]
    [InlineData("Movie.en.hi.srt", "en", false, true)]
    [InlineData("Movie.pt-BR.vtt", "pt", false, false)]
    public void ParsesSidecarNames(string name, string? language, bool forced, bool hearingImpaired)
    {
        var s = Parse(name);
        Assert.Equal(language, s.Language?.Code);
        Assert.Equal(forced, s.IsForced);
        Assert.Equal(hearingImpaired, s.IsHearingImpaired);
    }

    [Fact]
    public void FindsOnlyFilesOfThatVideo()
    {
        using var dir = new TempDir();
        foreach (var f in new[] { "Ep 01.mkv", "Ep 01.en.srt", "Ep 01.ro.srt", "Ep 01.Extended.en.srt", "Ep 010.en.srt", "Ep 01.en.txt" })
            File.WriteAllText(dir.File(f), "x");
        var found = SidecarFinder.Find(dir.File("Ep 01.mkv"), LanguageCatalog.Default).Select(s => s.FileName).ToList();
        Assert.Equal(["Ep 01.en.srt", "Ep 01.Extended.en.srt", "Ep 01.ro.srt"], found);
    }

    [Fact]
    public void SelectPrefersSourceLanguageAndNeverPicksATarget()
    {
        var catalog = LanguageCatalog.Default;
        var en = Parse("Movie.en.srt");
        var enForced = Parse("Movie.en.forced.srt");
        var ro = Parse("Movie.ro.srt");
        var untagged = Parse("Movie.srt");
        Assert.Same(en, SidecarFinder.Select([enForced, ro, en], catalog.Get("en"), [catalog.Get("ro")]));
        Assert.Same(en, SidecarFinder.Select([ro, en], null, [catalog.Get("ro")]));
        Assert.Same(untagged, SidecarFinder.Select([ro, untagged], catalog.Get("en"), [catalog.Get("ro")]));
        Assert.Null(SidecarFinder.Select([ro], null, [catalog.Get("ro")]));
    }

    [Fact]
    public void PerLineItalicsSurviveTranslation_Srt()
    {
        var split = SubtitleMarkup.Split("- Where are you?\n<i>- In the kitchen.</i>", SubtitleFormat.Srt);
        Assert.Equal("- Where are you?\n- In the kitchen.", split.Plain);
        Assert.Equal("- Unde ești?\n<i>- În bucătărie.</i>", split.Rebuild("- Unde ești?\n- În bucătărie."));
        // A changed line structure cannot carry per-line markup; the text is kept without it.
        Assert.Equal("Unde ești? În bucătărie.", split.Rebuild("Unde ești? În bucătărie."));
    }

    [Fact]
    public void PerLineOverridesSurviveTranslation_Ass()
    {
        var split = SubtitleMarkup.Split("{\\an8}First line\n{\\i1}Second line{\\i0}", SubtitleFormat.Ass);
        Assert.Equal("{\\an8}", split.Prefix);
        Assert.Equal("First line\nSecond line", split.Plain);
        Assert.Equal("{\\an8}Prima linie\n{\\i1}A doua linie{\\i0}", split.Rebuild("Prima linie\nA doua linie"));
    }

    [Fact]
    public void WholeCueItalicsAreNotDuplicated()
    {
        var split = SubtitleMarkup.Split("<i>One\nTwo</i>", SubtitleFormat.Srt);
        Assert.Null(split.LineWraps);
        Assert.Equal("<i>Unu\nDoi</i>", split.Rebuild("Unu\nDoi"));
    }

    [Fact]
    public void MidLineTagsAreDropped()
    {
        var split = SubtitleMarkup.Split("I <b>really</b> mean it", SubtitleFormat.Srt);
        Assert.Equal("I really mean it", split.Plain);
        Assert.Equal("Chiar vorbesc serios", split.Rebuild("Chiar vorbesc serios"));
    }
}

public class PgsTests
{
    [Fact]
    public void RleRoundTrip()
    {
        var (w, h, indexes) = PgsTestWriter.Render("Hello");
        var rle = PgsTestWriter.EncodeRle(indexes, w, h);
        Assert.Equal(indexes, PgsReader.DecodeRle(rle, w, h));
    }

    [Fact]
    public void ReadsTimingAndImages()
    {
        using var dir = new TempDir();
        var sup = dir.File("t.sup");
        PgsTestWriter.Write(sup, [
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "One"),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "Two"),
        ]);
        var images = PgsReader.Read(sup);
        Assert.Equal(2, images.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), images[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.5), images[0].End);
        Assert.Equal(TimeSpan.FromSeconds(3), images[1].Start);
        // Text is dark on a white background.
        Assert.Equal(255, images[0].Gray[0]);
        Assert.Contains(images[0].Gray, g => g < 40);
    }
}

[Collection("media")]
public class SourceAndOcrIntegrationTests
{
    private static readonly PgsTestWriter.Cue[] Cues =
    [
        new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2), "Where did you put the keys?"),
        new(TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(4), "I think they are in the kitchen."),
        new(TimeSpan.FromSeconds(4.2), TimeSpan.FromSeconds(5.5), "Thank you very much!"),
    ];

    private static FileProcessor Processor(string work) =>
        new(new FfprobeMediaProbe(TestMedia.Tools), new FfmpegSubtitleExtractor(TestMedia.Tools),
            [new FfmpegMuxer(TestMedia.Tools)],
            new DocumentTranslator(new TaggingProvider(), new MemoryCache(), new DocumentTranslatorOptions { RetryDelay = TimeSpan.FromMilliseconds(1) }),
            LanguageCatalog.Default, work, ocr: new WindowsPgsOcrEngine(TestMedia.Tools));

    private static bool EnglishOcrInstalled => new WindowsPgsOcrEngine(TestMedia.Tools).SupportsLanguage(LanguageCatalog.Default.Get("en"));

    [SkippableFact]
    public async Task SidecarSubtitleFile_IsTranslatedAndAddedWithTheOriginal()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var video = await TestMedia.CreateVideoAsync(dir.File("Ep 01.mkv"), []);
        File.WriteAllText(dir.File("Ep 01.en.srt"), TestMedia.EnglishSrt);

        var item = new JobItem { SourcePath = video, RootPath = dir.Path, Options = new ProcessingOptions { TargetLanguages = ["ro"] } };
        await Processor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        Assert.True(item.Status == JobStatus.Completed, item.Message + " | " + string.Join(" | ", item.Warnings));
        var info = await new FfprobeMediaProbe(TestMedia.Tools).ProbeAsync(item.OutputPath!, CancellationToken.None);
        Assert.Equal(["eng", "rum"], info.Subtitles.Select(s => s.Language).ToList());
        Assert.Equal(["English", "Romanian (AI)"], info.Subtitles.Select(s => s.Title).ToList());
        Assert.Contains("[ro] Goku, are you ready?", await TestMedia.ReadSubtitleAsync(item.OutputPath!, 1));
    }

    private const string SongsOnly = "1\n00:00:05,000 --> 00:00:05,800\n<i>La la la, the song</i>\n\n";

    [SkippableTheory]
    [InlineData("English-Forced")] // recognised by its title
    [InlineData(null)]             // untitled: recognised by counting subtitles
    public async Task DefaultForcedTrack_DoesNotBeatTheFullDialogueTrack(string? partialTitle)
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        // FFmpeg marks the first subtitle track as default, as in many Blu-ray rips.
        var video = await TestMedia.CreateVideoAsync(dir.File("Ep.mkv"),
            [new(SongsOnly, "eng", Title: partialTitle), new(TestMedia.EnglishSrt, "eng", Title: partialTitle is null ? null : "English")]);
        var media = await new FfprobeMediaProbe(TestMedia.Tools).ProbeAsync(video, CancellationToken.None);
        Assert.True(media.Subtitles.First().IsDefault);

        var item = new JobItem { SourcePath = video, RootPath = dir.Path, Options = new ProcessingOptions { TargetLanguages = ["ro"] } };
        await Processor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        Assert.True(item.Status == JobStatus.Completed, item.Message + " | " + string.Join(" | ", item.Warnings));
        var ro = SubtitleParser.Parse(await TestMedia.ReadSubtitleAsync(item.OutputPath!, 2));
        Assert.Equal(4, ro.Cues.Count);
        Assert.Equal("[ro] Goku, are you ready?", ro.Cues[0].Text);
    }

    [SkippableFact]
    public async Task PgsTrack_IsReadWithWindowsOcr_AndTranslated()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        Skip.IfNot(EnglishOcrInstalled, "Windows English OCR not installed");
        using var dir = new TempDir();
        var plain = await TestMedia.CreateVideoAsync(dir.File("plain.mkv"), []);
        var sup = dir.File("subs.sup");
        PgsTestWriter.Write(sup, Cues);
        var video = dir.File("Movie.mkv");
        var mux = await ProcessRunner.RunAsync(TestMedia.Tools.FfmpegPath!,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-copyts", "-i", plain, "-f", "sup", "-i", sup,
             "-map", "0", "-map", "1", "-c", "copy", "-metadata:s:s:0", "language=eng", video], CancellationToken.None);
        Assert.True(mux.ExitCode == 0, mux.StdErr);
        File.Delete(plain);

        var media = await new FfprobeMediaProbe(TestMedia.Tools).ProbeAsync(video, CancellationToken.None);
        Assert.Equal("hdmv_pgs_subtitle", Assert.Single(media.Subtitles).CodecName);

        var item = new JobItem { SourcePath = video, RootPath = dir.Path, Options = new ProcessingOptions { TargetLanguages = ["ro"] } };
        await Processor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        // OCR always adds a warning (recognition may be imperfect).
        Assert.True(item.Status == JobStatus.CompletedWithWarnings, item.Message + " | " + string.Join(" | ", item.Warnings));
        var info = await new FfprobeMediaProbe(TestMedia.Tools).ProbeAsync(item.OutputPath!, CancellationToken.None);
        Assert.Equal(["hdmv_pgs_subtitle", "subrip", "subrip"], info.Subtitles.Select(s => s.CodecName).ToList());
        Assert.Equal(["eng", "eng", "rum"], info.Subtitles.Select(s => s.Language).ToList());
        Assert.Equal("English (OCR)", info.Subtitles.ElementAt(1).Title);

        var ocr = SubtitleParser.Parse(await TestMedia.ReadSubtitleAsync(item.OutputPath!, 1));
        Assert.Equal(Cues.Select(c => c.Text), ocr.Cues.Select(c => c.Text));
        Assert.Equal(Cues.Select(c => c.Start), ocr.Cues.Select(c => c.Start));
        var ro = SubtitleParser.Parse(await TestMedia.ReadSubtitleAsync(item.OutputPath!, 2));
        Assert.Equal("[ro] Where did you put the keys?", ro.Cues[0].Text);
    }
}
