using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Infrastructure.Media;
using ALMARFELD.SubFlow.Infrastructure.Persistence;
using ALMARFELD.SubFlow.Translation;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>Queue behaviour the UI relies on: removed items are skipped, one item can be stopped.</summary>
[Collection("media")]
public class QueueRunnerTests
{
    private static (BatchRunner Runner, SqliteStore Store) Runner(TempDir dir, TaggingProvider provider)
    {
        var tools = TestMedia.Tools;
        var processor = new FileProcessor(new FfprobeMediaProbe(tools), new FfmpegSubtitleExtractor(tools), [new FfmpegMuxer(tools)],
            new DocumentTranslator(provider, new MemoryCache(), new DocumentTranslatorOptions { RetryDelay = TimeSpan.FromMilliseconds(1) }),
            LanguageCatalog.Default, dir.File("work"));
        var store = new SqliteStore(dir.File("subflow.db"));
        return (new BatchRunner(processor, store, new PauseGate()), store);
    }

    private static JobItem Item(string video, string root) =>
        new() { SourcePath = video, RootPath = root, Options = new ProcessingOptions { SourceLanguage = "en", TargetLanguages = ["ro"] } };

    [SkippableFact]
    public async Task ItemsRemovedBeforeTheirTurn_AreSkipped()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var a = await TestMedia.CreateVideoAsync(dir.File("a.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var b = await TestMedia.CreateVideoAsync(dir.File("b.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var (runner, _) = Runner(dir, new TaggingProvider());
        var first = Item(a, dir.Path);
        var removed = Item(b, dir.Path);

        await runner.RunAsync([first, removed], 1, CancellationToken.None, shouldRun: i => i.Id != removed.Id);

        Assert.Equal(JobStatus.Completed, first.Status);
        Assert.Equal(JobStatus.Pending, removed.Status);
        Assert.False(File.Exists(dir.File(Path.Combine("SubFlow Output", "b.mkv"))));
    }

    [SkippableFact]
    public async Task RemovalDuringScheduling_IsCheckedAgainBeforeProcessing()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("scheduled.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var original = await File.ReadAllBytesAsync(source);
        var (runner, store) = Runner(dir, new TaggingProvider());
        var item = Item(source, dir.Path);
        var checks = 0;
        await runner.RunAsync([item], 1, CancellationToken.None,
            shouldRun: _ => Interlocked.Increment(ref checks) == 1);
        Assert.Equal(2, checks);
        Assert.Equal(JobStatus.Pending, item.Status);
        Assert.Null(store.Get(item.Id));
        Assert.False(Directory.Exists(dir.File("SubFlow Output")));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [SkippableFact]
    public async Task StoppingOneItem_LetsTheOthersFinish()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var slow = await TestMedia.CreateVideoAsync(dir.File("slow.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var next = await TestMedia.CreateVideoAsync(dir.File("next.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var slowItem = Item(slow, dir.Path);
        var nextItem = Item(next, dir.Path);

        var started = new TaskCompletionSource();
        var provider = new TaggingProvider();
        provider.BeforeEachCall = async ct =>
        {
            if (slowItem.Status != JobStatus.Running) return;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // "translating" until stopped
        };
        var (runner, _) = Runner(dir, provider);

        var run = runner.RunAsync([slowItem, nextItem], 1, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(runner.CancelItem(slowItem.Id));
        await run.WaitAsync(TimeSpan.FromSeconds(120));

        Assert.Equal(JobStatus.Cancelled, slowItem.Status);
        Assert.Equal(JobStatus.Completed, nextItem.Status);
        Assert.False(runner.CancelItem(slowItem.Id)); // no longer running
    }
}
