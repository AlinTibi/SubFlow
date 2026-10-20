using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Infrastructure.Persistence;
using ALMARFELD.SubFlow.Infrastructure.Settings;

namespace ALMARFELD.SubFlow.Tests;

public class PersistenceTests
{
    [Fact]
    public void JobStore_RoundTripsItems_AndMarksRunningAsInterrupted()
    {
        using var dir = new TempDir();
        var store = new SqliteStore(dir.File("db.sqlite"));
        var item = new JobItem
        {
            SourcePath = @"C:\Videos\Épisode 1.mkv",
            RootPath = @"C:\Videos",
            Options = new ProcessingOptions { TargetLanguages = ["ro", "fr"], OutputMode = OutputMode.MkvAndSubtitleFiles },
            Status = JobStatus.Running,
        };
        item.Warnings.Add("note");
        store.Upsert(item);

        var reopened = new SqliteStore(dir.File("db.sqlite"));
        Assert.Equal(1, reopened.MarkRunningAsInterrupted());
        var loaded = Assert.Single(reopened.GetQueue());
        Assert.Equal(JobStatus.Interrupted, loaded.Status);
        Assert.Equal(["ro", "fr"], loaded.Options.TargetLanguages);
        Assert.Equal(OutputMode.MkvAndSubtitleFiles, loaded.Options.OutputMode);
        Assert.Equal("note", loaded.Warnings.Single());
        Assert.True(loaded.CanRetry);

        loaded.Status = JobStatus.Completed;
        reopened.Upsert(loaded);
        Assert.Empty(reopened.GetQueue());
        Assert.Single(reopened.GetHistory());
        reopened.ClearHistory();
        Assert.Empty(reopened.GetHistory());
    }

    [Fact]
    public void TranslationCache_StoresAndReturnsManyEntries()
    {
        using var dir = new TempDir();
        var store = new SqliteStore(dir.File("db.sqlite"));
        store.SetMany(Enumerable.Range(0, 1000).Select(i => new KeyValuePair<string, string>($"k{i}", $"văloare {i}")));
        Assert.Equal(1000, store.Count());
        var got = store.GetMany(Enumerable.Range(0, 1000).Select(i => $"k{i}").Append("missing"));
        Assert.Equal(1000, got.Count);
        Assert.Equal("văloare 999", got["k999"]);
        store.Clear();
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void Settings_RoundTrip_AndCorruptFileFallsBackToDefaults()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = new AppSettings { TargetLanguages = ["ro", "de"], Concurrency = 3 };
        s.Ollama.Model = "qwen3:8b";
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(["ro", "de"], loaded.TargetLanguages);
        Assert.Equal("qwen3:8b", loaded.Ollama.Model);

        File.WriteAllText(dir.File("settings.json"), "{ not json");
        Assert.Equal(1, store.Load().Concurrency);
        Assert.True(File.Exists(dir.File("settings.json.corrupt")));
    }

    [Fact]
    public void SecretProtector_EncryptsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protectedValue = SecretProtector.Protect("sk-secret-123");
        Assert.NotNull(protectedValue);
        Assert.DoesNotContain("sk-secret", protectedValue);
        Assert.Equal("sk-secret-123", SecretProtector.Unprotect(protectedValue));
        Assert.Null(SecretProtector.Unprotect("garbage"));
    }
}
