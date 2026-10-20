using ALMARFELD.SubFlow.Ocr;
using System.Collections.Concurrent;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Infrastructure.Media;
using ALMARFELD.SubFlow.Infrastructure.Persistence;
using ALMARFELD.SubFlow.Infrastructure.Settings;
using ALMARFELD.SubFlow.Infrastructure.Tools;
using ALMARFELD.SubFlow.Translation;
using ALMARFELD.SubFlow.Translation.Providers;

namespace ALMARFELD.SubFlow.App.Services;

public sealed record EngineInfo(string Id, string Name, string Description, bool IsCloud);

/// <summary>Composition root: creates and owns the application's services.</summary>
public sealed class AppServices
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, IReadOnlyList<GlossaryEntry> Entries)> _projectGlossaries = new(StringComparer.OrdinalIgnoreCase);

    public AppServices(AppPaths? paths = null)
    {
        Paths = paths ?? new AppPaths();
        SettingsStore = new SettingsStore(Paths.SettingsFile);
        Settings = SettingsStore.Load();
        Tools = new ToolLocator(() => Settings.Tools);
        Store = new SqliteStore(Paths.DatabaseFile);
        Languages = LanguageCatalog.Default.LoadExtensions(Paths.LanguagesExtensionFile);
        GlobalGlossary = GlossaryService.Load(Paths.GlobalGlossaryFile);
        Probe = new FfprobeMediaProbe(Tools);
        Extractor = new FfmpegSubtitleExtractor(Tools);
        // mkvmerge is preferred for MKV sources when installed; FFmpeg handles everything else.
        Muxers = [new MkvMergeMuxer(Tools), new FfmpegMuxer(Tools)];
        Ocr = new WindowsPgsOcrEngine(Tools);
    }

    public AppPaths Paths { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public ToolLocator Tools { get; }
    public SqliteStore Store { get; }
    public LanguageCatalog Languages { get; }
    public GlossaryDocument GlobalGlossary { get; private set; }
    public IMediaProbe Probe { get; }
    public ISubtitleExtractor Extractor { get; }
    public IReadOnlyList<IMuxer> Muxers { get; }
    /// <summary>Local OCR for PGS image subtitles (Windows built-in engine).</summary>
    public ISubtitleOcrEngine Ocr { get; }
    public PauseGate Pause { get; } = new();

    public event Action? SettingsChanged;

    public void SaveSettings()
    {
        // Consent for one endpoint must not authorize a newly configured recipient.
        Settings.CloudConsentGivenThisSession = false;
        SettingsStore.Save(Settings);
        Tools.Refresh();
        SettingsChanged?.Invoke();
    }

    public static IReadOnlyList<EngineInfo> Engines { get; } =
    [
        new("ollama", "Ollama", "Free · runs locally on this PC", false),
        new("openai", "OpenAI-compatible", "Paid cloud API, or a free local server (LM Studio, llama.cpp)", true),
        new("deepl", "DeepL", "Paid cloud API (DeepL Free/Pro key)", true),
        new("google", "Google Cloud Translation", "Paid cloud API", true),
    ];

    public OllamaClient CreateOllamaClient() =>
        new(Settings.Ollama.Endpoint, TimeSpan.FromSeconds(Math.Max(30, Settings.Ollama.TimeoutSeconds)));

    /// <summary>Creates the configured translation engine or throws with a message for the user.</summary>
    public ITranslationProvider CreateProvider(string id)
    {
        switch (id)
        {
            case "ollama":
                if (string.IsNullOrWhiteSpace(Settings.Ollama.Model))
                    throw new InvalidOperationException(L.Get("Err_NoOllamaModel"));
                return new OllamaTranslationProvider(CreateOllamaClient(), new OllamaOptions
                {
                    Model = Settings.Ollama.Model!,
                    Temperature = Settings.Ollama.Temperature,
                    ContextLength = Settings.Ollama.ContextLength,
                    BatchSize = Settings.Ollama.BatchSize,
                });
            case "openai":
                var key = SecretProtector.Unprotect(Settings.OpenAi.ProtectedApiKey);
                return new OpenAiCompatibleProvider(Settings.OpenAi.Endpoint, Settings.OpenAi.Model, key, Settings.OpenAi.BatchSize);
            case "deepl":
                return new DeepLProvider(SecretProtector.Unprotect(Settings.DeepL.ProtectedApiKey)
                    ?? throw new InvalidOperationException(L.Get("Err_NoApiKey")), Settings.DeepL.UseFreeEndpoint);
            case "google":
                return new GoogleTranslateProvider(SecretProtector.Unprotect(Settings.Google.ProtectedApiKey)
                    ?? throw new InvalidOperationException(L.Get("Err_NoApiKey")));
            default:
                throw new InvalidOperationException($"Unknown engine '{id}'.");
        }
    }

    public ISubtitleTranslator CreateTranslator(ITranslationProvider provider) =>
        new DocumentTranslator(provider, Store);

    public FileProcessor CreateProcessor(ISubtitleTranslator translator) =>
        new(Probe, Extractor, Muxers, translator, Languages, Paths.WorkDirectory, GlossaryFor, Pause, Ocr);

    // ------------------------------------------------------------------ glossary

    public void SaveGlobalGlossary(GlossaryDocument doc)
    {
        GlossaryService.Save(doc, Paths.GlobalGlossaryFile);
        GlobalGlossary = doc;
    }

    public static string ProjectGlossaryPath(string folder) => Path.Combine(folder, AppPaths.ProjectGlossaryFileName);

    /// <summary>Global glossary merged with the per-project glossary of the item's import folder (project wins).</summary>
    public IReadOnlyList<GlossaryEntry> GlossaryFor(JobItem item)
    {
        var project = Array.Empty<GlossaryEntry>() as IReadOnlyList<GlossaryEntry>;
        try
        {
            var path = ProjectGlossaryPath(item.RootPath);
            if (File.Exists(path))
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!_projectGlossaries.TryGetValue(path, out var cached) || cached.Stamp != stamp)
                {
                    cached = (stamp, GlossaryService.Load(path).Entries);
                    _projectGlossaries[path] = cached;
                }
                project = cached.Entries;
            }
        }
        catch { /* an unreadable project glossary must not stop processing */ }
        return GlossaryService.Merge(GlobalGlossary.Entries, project);
    }
}
