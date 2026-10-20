using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Infrastructure.Media;
using ALMARFELD.SubFlow.Translation.Llm;

namespace ALMARFELD.SubFlow.Tests;

public class CoreLogicTests
{
    [Fact]
    public void Catalog_ContainsAllRequiredLanguages_AndResolvesAllCodeForms()
    {
        var required = new[] { "English", "Romanian", "French", "German", "Spanish", "Italian", "Portuguese", "Dutch", "Polish",
            "Hungarian", "Czech", "Slovak", "Bulgarian", "Greek", "Turkish", "Ukrainian", "Russian", "Arabic", "Hebrew", "Hindi",
            "Chinese", "Japanese", "Korean", "Indonesian", "Vietnamese", "Thai", "Swedish", "Norwegian", "Danish", "Finnish",
            "Croatian", "Serbian", "Slovenian", "Lithuanian", "Latvian", "Estonian" };
        var catalog = LanguageCatalog.Default;
        foreach (var name in required) Assert.NotNull(catalog.All.SingleOrDefault(l => l.Name == name));

        Assert.Equal("ro", catalog.Find("rum")!.Code);
        Assert.Equal("ro", catalog.Find("ron")!.Code);
        Assert.Equal("de", catalog.Find("ger")!.Code);
        Assert.Equal("pt", catalog.Find("pt-BR")!.Code);
        Assert.True(catalog.Get("ar").IsRightToLeft);
        Assert.True(catalog.Get("he").IsRightToLeft);
        Assert.Contains(catalog.Search("rom"), l => l.Code == "ro");
        Assert.Contains(catalog.Search("Deutsch"), l => l.Code == "de");
    }

    [Fact]
    public void Catalog_CanBeExtendedFromJsonWithoutCodeChanges()
    {
        using var dir = new TempDir();
        var path = dir.File("languages.json");
        File.WriteAllText(path, """[{"Name":"Icelandic","NativeName":"Íslenska","Iso6391":"is","Iso6392B":"ice","Iso6392T":"isl"}]""");
        var extended = LanguageCatalog.Default.LoadExtensions(path);
        Assert.Equal("ice", extended.Get("is").ContainerCode);
        Assert.Equal(LanguageCatalog.Default.All.Count + 1, extended.All.Count);
    }

    [Theory]
    [InlineData("en", "What are you doing here? I don't know what you want. This is not the way to do it, and you know that.")]
    [InlineData("ro", "Ce faci aici? Nu știu ce vrei de la mine. Asta nu este calea, și tu știi asta foarte bine.")]
    [InlineData("ru", "Что ты здесь делаешь? Я не знаю, что тебе нужно. Это не так, и ты это знаешь.")]
    [InlineData("ja", "ここで何をしているの？私は何も知らない。それは違うよ、わかっているでしょう。")]
    [InlineData("ar", "ماذا تفعل هنا؟ لا أعرف ماذا تريد. هذه ليست الطريقة الصحيحة وأنت تعرف ذلك.")]
    public void Detector_RecognisesCommonLanguages(string expected, string text)
    {
        var guess = LanguageDetector.Detect([text, text]);
        Assert.NotNull(guess);
        Assert.Equal(expected, guess!.Code);
        Assert.True(LanguageDetector.IsReliable(guess), $"confidence {guess.Confidence}");
    }

    [Fact]
    public void Detector_ReturnsNothingForTooLittleText()
    {
        Assert.Null(LanguageDetector.Detect(["Hi."]));
    }

    [Fact]
    public void Glossary_DetectsViolations_RespectsCaseAndLanguage_AndRoundTripsCsvJson()
    {
        var entries = new List<GlossaryEntry>
        {
            new() { Source = "Kamehameha", Protected = true },
            new() { Source = "Dragon Balls", Target = "Bilele Dragonului", TargetLanguage = "ro" },
            new() { Source = "Ki", Target = "Ki", CaseSensitive = true },
        };
        var relevant = GlossaryService.Relevant(entries, ["Use the Kamehameha to find the Dragon Balls"], "ro");
        Assert.Equal(2, relevant.Count);
        Assert.Empty(GlossaryService.FindViolations("Kamehameha and Dragon Balls", "Kamehameha și Bilele Dragonului", relevant));
        Assert.Single(GlossaryService.FindViolations("Kamehameha!", "Undă Kame!", relevant));
        Assert.Empty(GlossaryService.Relevant(entries, ["Dragon Balls"], "fr")); // ro-only rule
        Assert.Empty(GlossaryService.Relevant(entries, ["the kid"], "fr")); // whole-word, case-sensitive "Ki"

        Assert.Equal("Caută Bilele Dragonului", GlossaryService.Apply("Caută Dragon Balls", entries, "ro"));

        using var dir = new TempDir();
        foreach (var file in new[] { dir.File("g.csv"), dir.File("g.json") })
        {
            GlossaryService.Save(new GlossaryDocument { Name = "Test", Entries = entries.Append(new GlossaryEntry { Source = "Comma, \"quoted\"", Target = "x" }).ToList() }, file);
            var loaded = GlossaryService.Load(file).Entries;
            Assert.Equal(4, loaded.Count);
            Assert.Equal("Comma, \"quoted\"", loaded[3].Source);
            Assert.True(loaded[0].Protected);
            Assert.Equal("ro", loaded[1].TargetLanguage);
            Assert.True(loaded[2].CaseSensitive);
        }
    }

    [Fact]
    public void Glossary_ProjectEntriesOverrideGlobal()
    {
        var merged = GlossaryService.Merge(
            [new GlossaryEntry { Source = "Ki", Target = "Energie" }],
            [new GlossaryEntry { Source = "ki", Target = "Ki" }]);
        Assert.Single(merged);
        Assert.Equal("Ki", merged[0].Target);
    }

    [Theory]
    [InlineData("{\"translations\":[{\"id\":1,\"text\":\"Salut\"},{\"id\":2,\"text\":\"Lume\"}]}")]
    [InlineData("<think>reasoning</think>```json\n{\"translations\":[{\"id\":\"1\",\"text\":\"Salut\"},{\"id\":2,\"text\":\"Lume\"}]}\n```")]
    [InlineData("Here you go: [{\"id\":1,\"translation\":\"Salut\"},{\"id\":2,\"text\":[\"Lume\"]}]")]
    [InlineData("{\"1\":\"Salut\",\"2\":\"Lume\"}")]
    public void LlmParser_AcceptsCommonResponseShapes(string content)
    {
        var parsed = LlmResponseParser.Parse(content);
        Assert.Equal("Salut", parsed[1]);
        Assert.Equal("Lume", parsed[2]);
    }

    [Fact]
    public void LlmParser_ReturnsEmptyForGarbage()
    {
        Assert.Empty(LlmResponseParser.Parse("I'm sorry, I cannot help with that."));
    }

    [Fact]
    public void OutputPlanner_PreservesStructure_AndNeverTargetsTheSource()
    {
        using var dir = new TempDir();
        var root = dir.Path;
        var source = Path.Combine(root, "Season 1", "Episode 01.mkv");
        var options = new ProcessingOptions();
        var output = OutputPlanner.GetOutputVideoPath(source, root, options);
        Assert.Equal(Path.Combine(root, "SubFlow Output", "Season 1", "Episode 01.mkv"), output);

        // Output folder == source folder → must not overwrite the original.
        var sameFolder = options with { OutputFolder = Path.Combine(root, "Season 1"), PreserveFolderStructure = false };
        var safe = OutputPlanner.GetOutputVideoPath(source, root, sameFolder);
        Assert.False(OutputPlanner.PathsEqual(safe, source));
        Assert.EndsWith(".subflow.mkv", safe);

        // MP4 in its own folder → MKV next to it is allowed (different file).
        var mp4 = Path.Combine(root, "clip.mp4");
        Assert.EndsWith("clip.mkv", OutputPlanner.GetOutputVideoPath(mp4, root, options with { OutputFolder = root }));
    }

    [Fact]
    public void OutputPlanner_ExpandsFoldersRecursively_AndSkipsOwnOutputs()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("A/B"));
        Directory.CreateDirectory(dir.File("SubFlow Output"));
        File.WriteAllText(dir.File("A/one.mkv"), "");
        File.WriteAllText(dir.File("A/B/two.MP4"), "");
        File.WriteAllText(dir.File("A/notes.txt"), "");
        File.WriteAllText(dir.File("SubFlow Output/one.mkv"), "");
        var found = OutputPlanner.ExpandInputs([dir.Path]).ToList();
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal(Path.GetFullPath(dir.Path), f.Root));
        Assert.Empty(OutputPlanner.ExpandInputs([dir.Path], recursive: false)); // videos are only in subfolders
    }

    [Fact]
    public void TrackSelection_PrefersFullDialogueTrackInRequestedLanguage()
    {
        var media = new MediaInfo
        {
            Path = "x.mkv",
            Streams =
            [
                new MediaStream { Index = 0, Kind = StreamKind.Video, CodecName = "h264" },
                new MediaStream { Index = 1, Kind = StreamKind.Subtitle, CodecName = "subrip", Language = "eng", IsForced = true },
                new MediaStream { Index = 2, Kind = StreamKind.Subtitle, CodecName = "hdmv_pgs_subtitle", Language = "eng" },
                new MediaStream { Index = 3, Kind = StreamKind.Subtitle, CodecName = "ass", Language = "eng" },
                new MediaStream { Index = 4, Kind = StreamKind.Subtitle, CodecName = "subrip", Language = "fre" },
            ],
        };
        Assert.Equal(3, TrackSelection.SelectSource(media, LanguageCatalog.Default.Get("en"))!.Index);
        Assert.Equal(4, TrackSelection.SelectSource(media, LanguageCatalog.Default.Get("fr"))!.Index);
        Assert.Null(TrackSelection.SelectSource(media, LanguageCatalog.Default.Get("de")));
        Assert.Equal(SubtitleCodecKind.Image, media.Streams[2].SubtitleKind);
    }

    [Fact]
    public void FfprobeParser_ReadsStreamsTagsAndDispositions()
    {
        var json = """
            {"streams":[
              {"index":0,"codec_name":"h264","codec_type":"video","width":1920,"height":1080,"disposition":{"default":1,"attached_pic":0}},
              {"index":1,"codec_name":"aac","codec_type":"audio","channels":2,"tags":{"language":"jpn"}},
              {"index":2,"codec_name":"subrip","codec_type":"subtitle","disposition":{"default":1,"forced":0},"tags":{"language":"eng","title":"Full","NUMBER_OF_FRAMES-eng":"812"}},
              {"index":3,"codec_name":"hdmv_pgs_subtitle","codec_type":"subtitle","tags":{"language":"ger"}},
              {"index":4,"codec_name":"ttf","codec_type":"attachment"}
            ],"format":{"format_name":"matroska,webm","duration":"1420.5","size":"123456"}}
            """;
        var info = FfprobeMediaProbe.Parse("x.mkv", json);
        Assert.True(info.IsMatroska);
        Assert.Equal(TimeSpan.FromSeconds(1420.5), info.Duration);
        var subs = info.Subtitles.ToList();
        Assert.Equal(2, subs.Count);
        Assert.Equal("eng", subs[0].Language);
        Assert.Equal("Full", subs[0].Title);
        Assert.Equal(812, subs[0].FrameCount);
        Assert.True(subs[0].IsDefault);
        Assert.Equal(1, subs[1].KindIndex);
        Assert.Equal(SubtitleCodecKind.Image, subs[1].SubtitleKind);
        Assert.Single(info.Streams, s => s.Kind == StreamKind.Attachment);
    }

    [Fact]
    public async Task PauseGate_BlocksUntilResumed()
    {
        var gate = new PauseGate();
        gate.Pause();
        var wait = gate.WaitIfPausedAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        gate.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gate.WaitIfPausedAsync(CancellationToken.None).IsCompleted);
    }
}
