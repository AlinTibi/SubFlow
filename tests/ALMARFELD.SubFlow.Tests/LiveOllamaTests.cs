using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Infrastructure.Media;
using ALMARFELD.SubFlow.Translation;
using ALMARFELD.SubFlow.Translation.Providers;
using Xunit.Abstractions;

namespace ALMARFELD.SubFlow.Tests;

/// <summary>
/// Real translation with a local Ollama model. Skipped automatically when Ollama is not running
/// or no suitable model is installed. Set SUBFLOW_TEST_MODEL to choose the model.
/// </summary>
[Collection("media")]
public class LiveOllamaTests(ITestOutputHelper output)
{
    private static async Task<string?> FindModelAsync(OllamaClient client)
    {
        var status = await client.GetStatusAsync(CancellationToken.None);
        if (!status.IsRunning) return null;
        var wanted = Environment.GetEnvironmentVariable("SUBFLOW_TEST_MODEL");
        if (wanted is not null) return status.Models.Any(m => m.Name == wanted) ? wanted : null;
        return ModelPicker.PickDefault(status.Models)?.Name;
    }

    [SkippableFact]
    public async Task TranslatesSyntheticVideoEnglishToRomanianAndFrench_WithRealModel()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        var client = new OllamaClient("http://127.0.0.1:11434");
        var model = await FindModelAsync(client);
        Skip.If(model is null, "Ollama not running or no chat model installed");

        using var dir = new TempDir();
        var source = await TestMedia.CreateVideoAsync(dir.File("Live Test.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var provider = new OllamaTranslationProvider(client, new OllamaOptions { Model = model!, BatchSize = 10 });
        var tools = TestMedia.Tools;
        var processor = new FileProcessor(new FfprobeMediaProbe(tools), new FfmpegSubtitleExtractor(tools),
            [new MkvMergeMuxer(tools), new FfmpegMuxer(tools)],
            new DocumentTranslator(provider, new MemoryCache()), LanguageCatalog.Default, dir.File("work"));

        var item = new JobItem
        {
            SourcePath = source,
            RootPath = dir.Path,
            Options = new ProcessingOptions { SourceLanguage = "en", TargetLanguages = ["ro", "fr"] },
        };
        await processor.ProcessAsync(item, null, CancellationToken.None);
        output.WriteLine($"Model: {model}; status {item.Status}: {item.Message}");
        foreach (var w in item.Warnings) output.WriteLine("warning: " + w);
        Assert.True(item.Status is JobStatus.Completed or JobStatus.CompletedWithWarnings, item.Message);

        var info = await new FfprobeMediaProbe(tools).ProbeAsync(item.OutputPath!, CancellationToken.None);
        Assert.Equal(["eng", "rum", "fre"], info.Subtitles.Select(s => s.Language));

        var ro = ALMARFELD.SubFlow.Core.Subtitles.SubtitleParser.Parse(await TestMedia.ReadSubtitleAsync(item.OutputPath!, 1));
        var fr = ALMARFELD.SubFlow.Core.Subtitles.SubtitleParser.Parse(await TestMedia.ReadSubtitleAsync(item.OutputPath!, 2));
        foreach (var c in ro.Cues) output.WriteLine($"ro {c.Index}: {c.Text}");
        foreach (var c in fr.Cues) output.WriteLine($"fr {c.Index}: {c.Text}");
        Assert.Equal(4, ro.Cues.Count);
        Assert.Equal(4, fr.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ro.Cues[0].Start);
        Assert.NotEqual("Goku, are you ready?", ro.Cues[0].Text);
        Assert.Contains("Goku", ro.Cues[0].Text);
        Assert.Contains("Kamehameha", ro.Cues[2].Text);
        Assert.Equal(await TestMedia.StreamHashAsync(source, "v"), await TestMedia.StreamHashAsync(item.OutputPath!, "v"));
    }
}
