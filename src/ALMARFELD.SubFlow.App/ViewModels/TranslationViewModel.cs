using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Translation.Providers;
using Microsoft.Win32;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed class LanguageOption(Language language, Action changed) : ObservableObject
{
    private bool _isSelected;
    public Language Language { get; } = language;
    public string Code => Language.Code;
    public string Display => Language.DisplayName;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) changed(); }
    }
}

public sealed record SourceOption(string Code, string Display);

/// <summary>Translation page: languages, engine and output options; starts the batch.</summary>
public sealed class TranslationViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly VideosViewModel _videos;
    private readonly QueueViewModel _queue;
    private readonly Action _showQueue;
    private string _search = "";
    private string _engineId;
    private OllamaModel? _selectedModel;
    private string _ollamaStatus = "";
    private bool _ollamaRunning;
    private bool _suppressSave;

    public TranslationViewModel(AppServices services, VideosViewModel videos, QueueViewModel queue, Action showQueue)
    {
        _services = services;
        _videos = videos;
        _queue = queue;
        _showQueue = showQueue;
        var s = services.Settings;

        _suppressSave = true;
        foreach (var l in services.Languages.All)
            Targets.Add(new LanguageOption(l, OnTargetsChanged) { IsSelected = s.TargetLanguages.Contains(l.Code, StringComparer.OrdinalIgnoreCase) });
        _suppressSave = false;
        TargetsView = CollectionViewSource.GetDefaultView(Targets);
        TargetsView.Filter = o => o is LanguageOption opt && (string.IsNullOrWhiteSpace(Search) || services.Languages.Search(Search).Contains(opt.Language));

        SourceOptions = new[] { new SourceOption("auto", L.Get("Tr_AutoDetect")) }
            .Concat(services.Languages.All.Select(l => new SourceOption(l.Code, l.DisplayName))).ToList();

        _engineId = s.ProviderId;
        StartCommand = new AsyncCommand(StartAsync, CanStart);
        RefreshModelsCommand = new AsyncCommand(RefreshModelsAsync);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        ClearOutputCommand = new RelayCommand(() => OutputFolder = null);
        ClearTargetsCommand = new RelayCommand(() => { foreach (var t in Targets) t.IsSelected = false; });
        RemoveTargetCommand = new RelayCommand(p => { if (p is LanguageOption o) o.IsSelected = false; });
        _videos.Changed += () => OnPropertyChanged(nameof(VideoSummary));
        _services.SettingsChanged += () => { OnPropertyChanged(nameof(EngineNote)); _ = RefreshModelsAsync(); };
        _ = RefreshModelsAsync();
    }

    // ---------------------------------------------------------------- languages

    public ObservableCollection<LanguageOption> Targets { get; } = new();
    public ICollectionView TargetsView { get; }
    public IReadOnlyList<LanguageOption> SelectedTargets => Targets.Where(t => t.IsSelected).ToList();
    public IReadOnlyList<SourceOption> SourceOptions { get; }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) TargetsView.Refresh(); }
    }

    public string SourceLanguage
    {
        get => _services.Settings.SourceLanguage;
        set { _services.Settings.SourceLanguage = value; OnPropertyChanged(); Save(); }
    }

    private void OnTargetsChanged()
    {
        OnPropertyChanged(nameof(SelectedTargets));
        OnPropertyChanged(nameof(TargetsSummary));
        if (_suppressSave) return;
        _services.Settings.TargetLanguages = SelectedTargets.Select(t => t.Code).ToList();
        Save();
    }

    public string TargetsSummary => SelectedTargets.Count == 0 ? L.Get("Tr_NoTargets") : L.F("Tr_TargetsCount", SelectedTargets.Count);
    public string VideoSummary => L.F("Tr_VideoSummary", _videos.ReadyCount, _videos.Count);

    // ---------------------------------------------------------------- engine

    public IReadOnlyList<EngineInfo> Engines => AppServices.Engines;

    public string EngineId
    {
        get => _engineId;
        set
        {
            if (!Set(ref _engineId, value)) return;
            _services.Settings.CloudConsentGivenThisSession = false;
            _services.Settings.ProviderId = value;
            Save();
            OnPropertyChanged(nameof(IsOllama));
            OnPropertyChanged(nameof(EngineNote));
            OnPropertyChanged(nameof(IsCloudEngine));
        }
    }

    public bool IsOllama => EngineId == "ollama";
    public bool IsCloudEngine => AppServices.Engines.FirstOrDefault(e => e.Id == EngineId)?.IsCloud == true && !IsLocalOpenAi;
    private bool IsLocalOpenAi => EngineId == "openai" &&
        Uri.TryCreate(_services.Settings.OpenAi.Endpoint, UriKind.Absolute, out var u) && u.IsLoopback;

    public ObservableCollection<OllamaModel> OllamaModels { get; } = new();

    public OllamaModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!Set(ref _selectedModel, value) || value is null) return;
            _services.Settings.Ollama.Model = value.Name;
            Save();
        }
    }

    public string OllamaStatus { get => _ollamaStatus; private set => Set(ref _ollamaStatus, value); }
    public bool OllamaRunning { get => _ollamaRunning; private set => Set(ref _ollamaRunning, value); }
    public string ModelSuggestions => string.Join(", ", ModelPicker.Suggestions);

    public string EngineNote => EngineId switch
    {
        "ollama" => L.Get("Tr_NoteOllama"),
        "openai" when IsLocalOpenAi => L.F("Tr_NoteOpenAiLocal", _services.Settings.OpenAi.Endpoint, _services.Settings.OpenAi.Model),
        "openai" => L.F("Tr_NoteCloud", "OpenAI-compatible API", _services.Settings.OpenAi.Endpoint) + KeyState(_services.Settings.OpenAi.ProtectedApiKey),
        "deepl" => L.F("Tr_NoteCloud", "DeepL", "api.deepl.com") + KeyState(_services.Settings.DeepL.ProtectedApiKey),
        "google" => L.F("Tr_NoteCloud", "Google Cloud Translation", "translation.googleapis.com") + KeyState(_services.Settings.Google.ProtectedApiKey),
        _ => "",
    };

    private static string KeyState(string? protectedKey) =>
        "\n" + (string.IsNullOrWhiteSpace(protectedKey) ? L.Get("Tr_KeyMissing") : L.Get("Tr_KeyStored"));

    public async Task RefreshModelsAsync()
    {
        OllamaStatus = L.Get("Tr_OllamaChecking");
        var status = await _services.CreateOllamaClient().GetStatusAsync(CancellationToken.None);
        OllamaRunning = status.IsRunning;
        OllamaModels.Clear();
        foreach (var m in status.Models.Where(ModelPicker.IsSuitable)) OllamaModels.Add(m);

        if (!status.IsRunning) OllamaStatus = status.Error ?? L.Get("Tr_OllamaOffline");
        else if (OllamaModels.Count == 0) OllamaStatus = L.F("Tr_OllamaNoModels", ModelSuggestions);
        else OllamaStatus = L.F("Tr_OllamaReady", status.Version, OllamaModels.Count);

        var wanted = _services.Settings.Ollama.Model;
        var pick = OllamaModels.FirstOrDefault(m => m.Name == wanted) ?? ModelPicker.PickDefault(OllamaModels);
        _selectedModel = null;
        SelectedModel = pick;
        OnPropertyChanged(nameof(SelectedModel));
    }

    // ---------------------------------------------------------------- output options

    public OutputMode OutputMode
    {
        get => _services.Settings.OutputMode;
        set { _services.Settings.OutputMode = value; OnPropertyChanged(); Save(); }
    }

    public ExistingTrackPolicy ExistingTrackPolicy
    {
        get => _services.Settings.ExistingTrackPolicy;
        set { _services.Settings.ExistingTrackPolicy = value; OnPropertyChanged(); Save(); }
    }

    public ExistingOutputPolicy ExistingOutputPolicy
    {
        get => _services.Settings.ExistingOutputPolicy;
        set { _services.Settings.ExistingOutputPolicy = value; OnPropertyChanged(); Save(); }
    }

    public string? OutputFolder
    {
        get => _services.Settings.OutputFolder;
        set { _services.Settings.OutputFolder = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); OnPropertyChanged(nameof(OutputFolderDisplay)); Save(); }
    }

    public string OutputFolderDisplay => OutputFolder ?? L.Get("Tr_OutputDefault");

    public bool PreserveFolderStructure
    {
        get => _services.Settings.PreserveFolderStructure;
        set { _services.Settings.PreserveFolderStructure = value; OnPropertyChanged(); Save(); }
    }

    public bool MakeFirstNewTrackDefault
    {
        get => _services.Settings.MakeFirstNewTrackDefault;
        set { _services.Settings.MakeFirstNewTrackDefault = value; OnPropertyChanged(); Save(); }
    }

    public int Concurrency
    {
        get => _services.Settings.Concurrency;
        set { _services.Settings.Concurrency = Math.Clamp(value, 1, 8); OnPropertyChanged(); Save(); }
    }

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4, 6, 8];

    public ICommand StartCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ClearOutputCommand { get; }
    public ICommand ClearTargetsCommand { get; }
    public ICommand RemoveTargetCommand { get; }

    private void BrowseOutput()
    {
        var dlg = new OpenFolderDialog { Title = L.Get("Tr_ChooseOutput") };
        if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
    }

    private void Save()
    {
        if (_suppressSave) return;
        try { _services.SettingsStore.Save(_services.Settings); } catch { /* settings are best effort */ }
    }

    private bool CanStart() => SelectedTargets.Count > 0 && _videos.ReadyCount > 0 && !_videos.IsScanning;

    private async Task StartAsync()
    {
        var owner = Application.Current.MainWindow!;
        // Validate the engine before queuing anything.
        var provider = _services.CreateProvider(EngineId);

        if (provider.SendsTextOffMachine && !_services.Settings.CloudConsentGivenThisSession)
        {
            var paid = provider.Cost == Core.Translation.ProviderCost.PaidCloud;
            var text = L.F("Tr_CloudConsent", provider.DisplayName) + (paid ? "\n\n" + L.Get("Tr_CloudPaid") : "");
            if (MessageBox.Show(owner, text, L.Get("Tr_CloudConsentTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _services.Settings.CloudConsentGivenThisSession = true;
        }

        var videos = _videos.Entries.Where(e => e.IsReady && e.CanTranslate).ToList();
        var skipped = _videos.Entries.Count - videos.Count;
        var options = new ProcessingOptions
        {
            SourceLanguage = SourceLanguage,
            TargetLanguages = SelectedTargets.Select(t => t.Code).ToList(),
            ExistingTrackPolicy = ExistingTrackPolicy,
            ExistingOutputPolicy = ExistingOutputPolicy,
            OutputMode = OutputMode,
            OutputFolder = OutputFolder,
            PreserveFolderStructure = PreserveFolderStructure,
            MakeFirstNewTrackDefault = MakeFirstNewTrackDefault,
            ProviderId = EngineId,
        };

        if (ExistingTrackPolicy == ExistingTrackPolicy.Replace &&
            MessageBox.Show(owner, L.Get("Tr_ReplaceConfirm"), "ALMARFELD SubFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var batch = Guid.NewGuid();
        var items = videos.Select(v => new JobItem { BatchId = batch, SourcePath = v.Path, RootPath = v.Root, Options = options }).ToList();
        if (skipped > 0)
            MessageBox.Show(owner, L.F("Tr_SkippedVideos", skipped), "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Information);

        _showQueue();
        // Items added while a batch is running are picked up automatically when it reaches them.
        _ = _queue.EnqueueAsync(items, start: true);
        await Task.CompletedTask;
    }
}
