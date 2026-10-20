using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Infrastructure.Media;
using ALMARFELD.SubFlow.Infrastructure.Persistence;
using ALMARFELD.SubFlow.Translation;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>
/// End-to-end tests with real FFmpeg/FFprobe on synthetic videos. Output tracks are verified by
/// probing the produced files; stream hashes prove video/audio were copied, not re-encoded.
/// </summary>
[Collection("media")]
public class PipelineIntegrationTests
{
    private static readonly string[] FourLanguages = ["ro", "fr", "de", "es"];

    private static FileProcessor CreateProcessor(string workRoot, ITranslationProvider? provider = null, ITranslationCache? cache = null, PauseGate? pause = null)
    {
        var tools = TestMedia.Tools;
        return new FileProcessor(new FfprobeMediaProbe(tools), new FfmpegSubtitleExtractor(tools),
            [new MkvMergeMuxer(tools), new FfmpegMuxer(tools)],
            new DocumentTranslator(provider ?? new TaggingProvider(), cache ?? new MemoryCache(),
                new DocumentTranslatorOptions { RetryDelay = TimeSpan.FromMilliseconds(1) }),
            LanguageCatalog.Default, workRoot, pause: pause);
    }

    private static JobItem Item(string source, string root, ProcessingOptions options) =>
        new() { SourcePath = source, RootPath = root, Options = options };

    private static Task<MediaInfo> Probe(string path) => new FfprobeMediaProbe(TestMedia.Tools).ProbeAsync(path, CancellationToken.None);

    [SkippableFact]
    public async Task ExtractsEmbeddedEnglishSubtitles()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var video = await TestMedia.CreateVideoAsync(dir.File("in.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var media = await Probe(video);
        var stream = Assert.Single(media.Subtitles);
        Assert.Equal("eng", stream.Language);

        var path = await new FfmpegSubtitleExtractor(TestMedia.Tools).ExtractAsync(media, stream, dir.File("work"), CancellationToken.None);
        var doc = ALMARFELD.SubFlow.Core.Subtitles.SubtitleParser.ParseFile(path);
        Assert.Equal(4, doc.Cues.Count);
        Assert.Equal("Goku, are you ready?", doc.Cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(500), doc.Cues[0].Start);
    }

    [SkippableFact]
    public async Task TranslatesIntoFourLanguages_PreservingOriginalTracksVideoAndAudio()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("Show - Episode 001.mkv"), [new(TestMedia.EnglishSrt, "eng", Title: "English")]);
        var sourceBytesBefore = await File.ReadAllBytesAsync(source);

        var item = Item(source, dir.Path, new ProcessingOptions { SourceLanguage = "en", TargetLanguages = FourLanguages });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        Assert.True(item.Status == JobStatus.Completed, item.Message + " | " + string.Join(" | ", item.Warnings));
        var output = item.OutputPath!;
        Assert.Equal(dir.File(Path.Combine("SubFlow Output", "Show - Episode 001.mkv")), output);

        var info = await Probe(output);
        var langs = info.Subtitles.Select(s => s.Language).ToList();
        Assert.Equal(["eng", "rum", "fre", "ger", "spa"], langs);
        Assert.Equal("English", info.Subtitles.First().Title);
        Assert.Equal("Romanian (AI)", info.Subtitles.ElementAt(1).Title);

        // Original English track unchanged, translated tracks present with the same timing.
        var english = await TestMedia.ReadSubtitleAsync(output, 0);
        Assert.Contains("Goku, are you ready?", english);
        var romanian = ALMARFELD.SubFlow.Core.Subtitles.SubtitleParser.Parse(await TestMedia.ReadSubtitleAsync(output, 1));
        Assert.Equal(4, romanian.Cues.Count);
        Assert.Equal("[ro] Goku, are you ready?", romanian.Cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), romanian.Cues[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3250), romanian.Cues[1].End);

        // No re-encoding: packet hashes identical.
        Assert.Equal(await TestMedia.StreamHashAsync(source, "v"), await TestMedia.StreamHashAsync(output, "v"));
        Assert.Equal(await TestMedia.StreamHashAsync(source, "a"), await TestMedia.StreamHashAsync(output, "a"));

        // Source untouched; no temp files left behind.
        Assert.Equal(sourceBytesBefore, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*subflow-tmp*"));
        Assert.False(Directory.Exists(dir.File(Path.Combine("work", item.Id.ToString("N")))));
    }

    [SkippableFact]
    public async Task Mp4WithTimedText_IsTranslatedIntoMkv()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("clip.mp4"), [new(TestMedia.EnglishSrt, "eng")], container: "mp4", subtitleCodec: "mov_text");
        var item = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["it"], OutputMode = OutputMode.MkvAndSubtitleFiles });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        Assert.True(item.Status is JobStatus.Completed or JobStatus.CompletedWithWarnings, item.Message);
        var info = await Probe(item.OutputPath!);
        Assert.True(info.IsMatroska);
        Assert.Equal(["eng", "ita"], info.Subtitles.Select(s => s.Language));
        Assert.Equal("subrip", info.Subtitles.First().CodecName); // mov_text converted, as Matroska requires
        Assert.Equal(await TestMedia.StreamHashAsync(source, "v"), await TestMedia.StreamHashAsync(item.OutputPath!, "v"));
        var sidecar = Assert.Single(item.SubtitleFiles);
        Assert.EndsWith("clip.it.srt", sidecar);
        Assert.Contains("[it] Goku", await File.ReadAllTextAsync(sidecar));
    }

    [SkippableFact]
    public async Task AssSource_KeepsStylingInTranslatedTrack()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var ass = "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Sign,Verdana,30,&H0000FFFF,&H000000FF,&H00000000,&H80000000,1,0,0,0,100,100,0,0,1,2,1,8,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Sign,,0,0,0,,{\\an8}Welcome to the tournament\n";
        var source = await TestMedia.CreateVideoAsync(dir.File("styled.mkv"), [new(ass, "eng", ".ass")]);
        var item = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["fr"] });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, item.Status);

        var info = await Probe(item.OutputPath!);
        Assert.Equal(["ass", "ass"], info.Subtitles.Select(s => s.CodecName));
        var tmp = dir.File("check");
        var extracted = await new FfmpegSubtitleExtractor(TestMedia.Tools).ExtractAsync(info, info.Subtitles.Last(), tmp, CancellationToken.None);
        var text = await File.ReadAllTextAsync(extracted);
        Assert.Contains("Style: Sign,Verdana,30", text);
        Assert.Contains("{\\an8}[fr] Welcome to the tournament", text);
    }

    [SkippableFact]
    public async Task ExistingLanguage_IsSkipped_NoDuplicateTracks()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("dup.mkv"),
            [new(TestMedia.EnglishSrt, "eng"), new(TestMedia.SampleSrt(3, "Linia"), "rum")]);
        var item = Item(source, dir.Path, new ProcessingOptions { SourceLanguage = "en", TargetLanguages = ["ro", "fr"] });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        Assert.Equal(JobStatus.CompletedWithWarnings, item.Status);
        Assert.Contains("ro", item.SkippedLanguages);
        var info = await Probe(item.OutputPath!);
        Assert.Equal(["eng", "rum", "fre"], info.Subtitles.Select(s => s.Language));
        Assert.Single(info.Subtitles, s => s.Language == "rum");
    }

    [SkippableFact]
    public async Task ExistingLanguage_ReplacePolicy_SwapsTrackOnlyInOutput()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("replace.mkv"),
            [new(TestMedia.EnglishSrt, "eng"), new(TestMedia.SampleSrt(3, "Vechi"), "rum")]);
        var item = Item(source, dir.Path, new ProcessingOptions { SourceLanguage = "en", TargetLanguages = ["ro"], ExistingTrackPolicy = ExistingTrackPolicy.Replace });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);

        var info = await Probe(item.OutputPath!);
        Assert.Equal(["eng", "rum"], info.Subtitles.Select(s => s.Language));
        Assert.Contains("[ro] Goku", await TestMedia.ReadSubtitleAsync(item.OutputPath!, 1));
        Assert.Equal(2, (await Probe(source)).Subtitles.Count()); // source still has both
    }

    [SkippableFact]
    public async Task VideoWithoutSubtitles_FailsWithClearMessage()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("nosubs.mkv"), []);
        var item = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"] });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, item.Status);
        Assert.Contains("no subtitle tracks", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(dir.File("SubFlow Output")) && Directory.GetFiles(dir.File("SubFlow Output")).Length > 0);
    }

    [SkippableFact]
    public async Task CorruptFile_FailsGracefully()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var path = dir.File("broken.mkv");
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        var item = Item(path, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"] });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, item.Status);
        Assert.Contains("could not be read", item.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task TranslationFailure_ProducesNoOutputAndKeepsSourceSafe()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("aifail.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var item = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"] });
        await CreateProcessor(dir.File("work"), new ThrowingProvider(transient: false)).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal(JobStatus.Failed, item.Status);
        Assert.Contains("Engine unavailable", item.Message);
        Assert.False(File.Exists(dir.File(Path.Combine("SubFlow Output", "aifail.mkv"))));
        Assert.True(File.Exists(source));
    }

    [SkippableFact]
    public async Task OutputFolderEqualToSourceFolder_DoesNotOverwriteSource()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("same.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var before = await File.ReadAllBytesAsync(source);
        var item = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"], OutputFolder = dir.Path, ExistingOutputPolicy = ExistingOutputPolicy.Overwrite });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal(JobStatus.Completed, item.Status);
        Assert.Equal(dir.File("same.subflow.mkv"), item.OutputPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
    }

    [SkippableFact]
    public async Task UnicodeAndSpacesInPaths_AreHandled()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var folder = dir.File("Séries & 'Anime' (2024)");
        var source = await TestMedia.CreateVideoAsync(Path.Combine(folder, "Épisode 01 — 日本語 ünï $x.mkv"),
            [new("1\n00:00:01,000 --> 00:00:02,000\nCafé — naïve façade, 東京!\n", "eng")]);
        var item = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ja"] });
        await CreateProcessor(dir.File("work"), new UnicodeProvider()).ProcessAsync(item, null, CancellationToken.None);
        Assert.True(item.Status == JobStatus.Completed, item.Message);
        Assert.Contains("Séries & 'Anime' (2024)", item.OutputPath);
        Assert.Contains("こんにちは", await TestMedia.ReadSubtitleAsync(item.OutputPath!, 1));
    }

    [SkippableFact]
    public async Task BatchOfManyFiles_RunsConcurrently_PersistsState_AndResumesAfterInterruption()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var files = new List<string>();
        for (var i = 1; i <= 6; i++)
            files.Add(await TestMedia.CreateVideoAsync(dir.File(Path.Combine("Season 1", $"Episode {i:000}.mkv")),
                [new(TestMedia.SampleSrt(12, $"Episode {i}"), "eng")], seconds: 3));

        var store = new SqliteStore(dir.File("subflow.db"));
        var options = new ProcessingOptions { SourceLanguage = "en", TargetLanguages = ["ro", "fr"] };
        var items = OutputPlanner.ExpandInputs([dir.File("Season 1")])
            .Select(f => Item(f.File, f.Root, options)).ToList();
        Assert.Equal(6, items.Count);
        foreach (var i in items) store.Upsert(i);

        // First run: cancel after a few translation calls (simulates closing the app).
        using var cts = new CancellationTokenSource();
        var provider = new TaggingProvider { PreferredBatchSize = 6 };
        provider.BeforeEachCall = ct => { if (provider.Calls == 7) cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var cache = new SqliteStore(dir.File("subflow.db"));
        var pause = new PauseGate();
        var runner = new BatchRunner(CreateProcessor(dir.File("work"), provider, cache, pause), store, pause);
        await runner.RunAsync(items, concurrency: 2, cts.Token);

        var afterFirst = store.GetQueue();
        Assert.NotEmpty(afterFirst); // some items not finished
        Assert.True(cache.Count() > 0);

        // Resume: reload unfinished items from the database and run again.
        var resumeProvider = new TaggingProvider { PreferredBatchSize = 6 };
        var runner2 = new BatchRunner(CreateProcessor(dir.File("work"), resumeProvider, cache, pause), store, pause);
        await runner2.RunAsync(store.GetQueue(), concurrency: 2, CancellationToken.None);

        Assert.Empty(store.GetQueue());
        var history = store.GetHistory();
        Assert.Equal(6, history.Count);
        Assert.All(history, h => Assert.Equal(JobStatus.Completed, h.Status));
        // Resume reused cached segments: fewer calls than a full run (6 files × 2 languages × 2 batches = 24).
        Assert.True(resumeProvider.Calls < 24, $"resume made {resumeProvider.Calls} calls");

        foreach (var h in history)
        {
            Assert.Contains(Path.Combine("Season 1", "SubFlow Output", "Episode"), h.OutputPath); // root = dropped folder
            var info = await Probe(h.OutputPath!);
            Assert.Equal(["eng", "rum", "fre"], info.Subtitles.Select(s => s.Language));
        }
    }

    [SkippableFact]
    public async Task ExistingOutput_IsSkippedByDefault()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("again.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var first = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"] });
        await CreateProcessor(dir.File("work")).ProcessAsync(first, null, CancellationToken.None);
        var stamp = File.GetLastWriteTimeUtc(first.OutputPath!);

        var second = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"] });
        await CreateProcessor(dir.File("work")).ProcessAsync(second, null, CancellationToken.None);
        Assert.Equal(JobStatus.Skipped, second.Status);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first.OutputPath!));

        var renamed = Item(source, dir.Path, new ProcessingOptions { TargetLanguages = ["ro"], ExistingOutputPolicy = ExistingOutputPolicy.Rename });
        await CreateProcessor(dir.File("work")).ProcessAsync(renamed, null, CancellationToken.None);
        Assert.EndsWith("again (2).mkv", renamed.OutputPath);
    }

    [SkippableTheory]
    [InlineData(OutputMode.SubtitleFiles)]
    [InlineData(OutputMode.MkvAndSubtitleFiles)]
    public async Task SkipExisting_PreservesSubtitleFilesInEveryOutputMode(OutputMode mode)
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("preserve.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var originalVideo = await File.ReadAllBytesAsync(source);
        var subtitle = dir.File("preserve.ro.srt");
        const string original = "1\n00:00:01,000 --> 00:00:02,000\nExisting subtitle.\n";
        await File.WriteAllTextAsync(subtitle, original);
        var item = Item(source, dir.Path, new ProcessingOptions {
            SourceLanguage = "en", TargetLanguages = ["ro"], OutputMode = mode,
            OutputFolder = dir.Path, ExistingOutputPolicy = ExistingOutputPolicy.Skip
        });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal(original, await File.ReadAllTextAsync(subtitle));
        Assert.Equal(originalVideo, await File.ReadAllBytesAsync(source));
        Assert.Contains(item.Warnings, w => w.Contains("not replaced", StringComparison.Ordinal));
        Assert.NotEqual(JobStatus.Failed, item.Status);
        Assert.Empty(Directory.GetFiles(dir.Path, "*subflow-tmp*"));
    }

    [SkippableTheory]
    [InlineData(ExistingOutputPolicy.Rename)]
    [InlineData(ExistingOutputPolicy.Overwrite)]
    public async Task SourceFolderSubtitle_IsNeverOverwritten(ExistingOutputPolicy policy)
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("preserve.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var subtitle = dir.File("preserve.ro.srt");
        await File.WriteAllTextAsync(subtitle, "Original subtitle");
        var item = Item(source, dir.Path, new ProcessingOptions {
            SourceLanguage = "en", TargetLanguages = ["ro"], OutputMode = OutputMode.MkvAndSubtitleFiles,
            OutputFolder = dir.Path, ExistingOutputPolicy = policy
        });
        await CreateProcessor(dir.File("work")).ProcessAsync(item, null, CancellationToken.None);
        Assert.Equal("Original subtitle", await File.ReadAllTextAsync(subtitle));
        Assert.Contains("[ro]", await File.ReadAllTextAsync(dir.File("preserve.ro (2).srt")));
        Assert.NotEqual(JobStatus.Failed, item.Status);
    }

    [Fact]
    public void MkvMergeArguments_AddLanguageNameAndCharset_WithoutReencoding()
    {
        var media = new MediaInfo
        {
            Path = @"C:\in\a b.mkv",
            FormatName = "matroska,webm",
            Streams =
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, CodecName = "h264" },
                new MediaStream { Index = 1, Kind = StreamKind.Subtitle, CodecName = "subrip", Language = "eng", IsDefault = true },
                new MediaStream { Index = 2, Kind = StreamKind.Subtitle, CodecName = "subrip", Language = "rum" },
            ],
        };
        var request = new MuxRequest
        {
            Source = media,
            OutputPath = @"C:\out\a b.mkv",
            NewTracks = [new NewSubtitleTrack(@"C:\w\ro.srt", LanguageCatalog.Default.Get("ro"), "Romanian (AI)", IsDefault: true)],
            ReplacedSubtitleStreams = [2],
            ClearOriginalDefaultSubtitle = true,
        };
        var args = MkvMergeMuxer.BuildArguments(request, new Dictionary<int, int> { [0] = 0, [1] = 1, [2] = 2 });
        Assert.Equal(["--ui-language", "en", "-o", @"C:\out\a b.mkv", "--subtitle-tracks", "1", "--default-track-flag", "1:no", @"C:\in\a b.mkv",
            "--language", "0:ro", "--track-name", "0:Romanian (AI)", "--default-track-flag", "0:yes", "--sub-charset", "0:UTF-8", @"C:\w\ro.srt"], args);
    }

    private sealed class UnicodeProvider : TaggingProvider
    {
        public override Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(request.Segments.ToDictionary(s => s.Id, _ => "こんにちは、東京のカフェです！"));
    }
}
