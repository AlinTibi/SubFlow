using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Infrastructure.Settings;
using Microsoft.Win32;

namespace ALMARFELD.SubFlow.App.ViewModels;

/// <summary>Settings page. Changes are applied with "Save"; API keys are stored with Windows DPAPI.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _toolStatus = "";
    private long _cacheCount;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        var s = services.Settings;
        OllamaEndpoint = s.Ollama.Endpoint;
        OllamaTemperature = s.Ollama.Temperature;
        OllamaContext = s.Ollama.ContextLength;
        OllamaBatch = s.Ollama.BatchSize;
        OllamaTimeout = s.Ollama.TimeoutSeconds;
        OpenAiEndpoint = s.OpenAi.Endpoint;
        OpenAiModel = s.OpenAi.Model;
        DeepLFree = s.DeepL.UseFreeEndpoint;
        FfmpegPath = s.Tools.Ffmpeg ?? "";
        FfprobePath = s.Tools.Ffprobe ?? "";
        MkvMergePath = s.Tools.MkvMerge ?? "";
        UiLanguage = s.UiLanguage;
        RecursiveScan = s.RecursiveScan;
        _checkForUpdates = s.CheckForUpdates;

        SaveCommand = new RelayCommand(Save);
        BrowseToolCommand = new RelayCommand(BrowseTool);
        ClearCacheCommand = new RelayCommand(ClearCache);
        OpenDataFolderCommand = new RelayCommand(() => Shell.ShowInExplorer(_services.Paths.LocalRoot));
        OpenSettingsFolderCommand = new RelayCommand(() => Shell.ShowInExplorer(_services.Paths.RoamingRoot));
        OpenUrlCommand = new RelayCommand(p => Shell.OpenUrl(p as string ?? ""));
        RefreshToolsCommand = new AsyncCommand(RefreshToolsAsync);
        CheckUpdatesCommand = new AsyncCommand(CheckUpdatesAsync);
        OpenNoticesCommand = new RelayCommand(OpenNotices);
        _ = RefreshToolsAsync();
    }

    public string OllamaEndpoint { get; set; }
    public double OllamaTemperature { get; set; }
    public int OllamaContext { get; set; }
    public int OllamaBatch { get; set; }
    public int OllamaTimeout { get; set; }
    public string OpenAiEndpoint { get; set; }
    public string OpenAiModel { get; set; }
    public bool DeepLFree { get; set; }
    public string FfmpegPath { get; set; }
    public string FfprobePath { get; set; }
    public string MkvMergePath { get; set; }
    public string UiLanguage { get; set; }
    public bool RecursiveScan { get; set; }
    public IReadOnlyList<KeyValuePair<string, string>> UiLanguages { get; } =
        L.Available.Select(a => new KeyValuePair<string, string>(a.Culture, a.Name)).ToList();

    // API keys are write-only in the UI: an empty box keeps the stored key.
    public string NewOpenAiKey { get; set; } = "";
    public string NewDeepLKey { get; set; } = "";
    public string NewGoogleKey { get; set; } = "";
    public string OpenAiKeyState => KeyState(_services.Settings.OpenAi.ProtectedApiKey);
    public string DeepLKeyState => KeyState(_services.Settings.DeepL.ProtectedApiKey);
    public string GoogleKeyState => KeyState(_services.Settings.Google.ProtectedApiKey);
    private static string KeyState(string? k) => string.IsNullOrWhiteSpace(k) ? L.Get("Set_KeyNone") : L.Get("Set_KeyStored");

    public string ToolStatus { get => _toolStatus; private set => Set(ref _toolStatus, value); }
    public long CacheCount { get => _cacheCount; private set => Set(ref _cacheCount, value); }
    public string DataFolder => _services.Paths.LocalRoot;
    public string SettingsFolder => _services.Paths.RoamingRoot;
    public string Version => AppInfo.VersionText;
    public string Copyright => AppInfo.Copyright;
    public string Website => AppInfo.Website;
    public string RepositoryUrl => AppInfo.HasRepository ? AppInfo.RepositoryUrl : "";
    public bool HasRepository => AppInfo.HasRepository;
    public string ReleasesUrl => AppInfo.ReleasesUrl;
    public string IssuesUrl => AppInfo.IssuesUrl;

    private bool _checkForUpdates;
    private string _updateStatus = "";
    private string? _updatePage;

    /// <summary>Saved immediately: it only decides whether SubFlow contacts GitHub at startup.</summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (!Set(ref _checkForUpdates, value)) return;
            _services.Settings.CheckForUpdates = value;
            try { _services.SettingsStore.Save(_services.Settings); } catch { /* best effort */ }
        }
    }

    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenNoticesCommand { get; }

    private async Task CheckUpdatesAsync()
    {
        if (_updatePage is not null) { Shell.OpenUrl(_updatePage); return; }
        UpdateStatus = L.Get("Set_UpdateChecking");
        try
        {
            var latest = await UpdateChecker.GetLatestAsync(CancellationToken.None);
            if (latest is null) UpdateStatus = L.Get("Set_UpdateUnknown");
            else if (latest.IsNewer)
            {
                _updatePage = latest.PageUrl;
                UpdateStatus = L.F("Set_UpdateAvailable", latest.Tag);
            }
            else UpdateStatus = L.F("Set_UpdateLatest", AppInfo.VersionText);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UpdateStatus = L.Get("Set_UpdateUnknown");
        }
    }

    /// <summary>Opens THIRD-PARTY-NOTICES.md shipped next to the program, or the copy online.</summary>
    private static void OpenNotices()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(local))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = false })?.Dispose(); }
            catch { /* Notepad unavailable */ }
        }
        else Shell.OpenUrl(AppInfo.HasRepository ? AppInfo.RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md" : AppInfo.Website);
    }

    public ICommand SaveCommand { get; }
    public ICommand BrowseToolCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenSettingsFolderCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand RefreshToolsCommand { get; }

    /// <summary>Clears key fields on the view after saving (PasswordBox is not bindable).</summary>
    public event Action? KeysSaved;

    private void Save()
    {
        var s = _services.Settings;
        if (!Uri.TryCreate(OllamaEndpoint, UriKind.Absolute, out var ollamaUri) || ollamaUri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(Application.Current.MainWindow!, L.Get("Set_InvalidEndpoint"), "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        s.Ollama.Endpoint = OllamaEndpoint.Trim();
        s.Ollama.Temperature = Math.Clamp(OllamaTemperature, 0, 1.5);
        s.Ollama.ContextLength = Math.Clamp(OllamaContext, 2048, 131072);
        s.Ollama.BatchSize = Math.Clamp(OllamaBatch, 1, 100);
        s.Ollama.TimeoutSeconds = Math.Clamp(OllamaTimeout, 30, 7200);
        s.OpenAi.Endpoint = OpenAiEndpoint.Trim();
        s.OpenAi.Model = OpenAiModel.Trim();
        s.DeepL.UseFreeEndpoint = DeepLFree;
        if (!string.IsNullOrWhiteSpace(NewOpenAiKey)) s.OpenAi.ProtectedApiKey = SecretProtector.Protect(NewOpenAiKey);
        if (!string.IsNullOrWhiteSpace(NewDeepLKey)) s.DeepL.ProtectedApiKey = SecretProtector.Protect(NewDeepLKey);
        if (!string.IsNullOrWhiteSpace(NewGoogleKey)) s.Google.ProtectedApiKey = SecretProtector.Protect(NewGoogleKey);
        NewOpenAiKey = NewDeepLKey = NewGoogleKey = "";
        s.Tools.Ffmpeg = NullIfEmpty(FfmpegPath);
        s.Tools.Ffprobe = NullIfEmpty(FfprobePath);
        s.Tools.MkvMerge = NullIfEmpty(MkvMergePath);
        s.RecursiveScan = RecursiveScan;
        var languageChanged = s.UiLanguage != UiLanguage;
        s.UiLanguage = UiLanguage;
        _services.SaveSettings();
        KeysSaved?.Invoke();
        OnPropertyChanged(nameof(OpenAiKeyState));
        OnPropertyChanged(nameof(DeepLKeyState));
        OnPropertyChanged(nameof(GoogleKeyState));
        _ = RefreshToolsAsync();
        MessageBox.Show(Application.Current.MainWindow!, L.Get(languageChanged ? "Set_SavedRestart" : "Set_Saved"), "ALMARFELD SubFlow",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void RemoveKey(string provider)
    {
        var s = _services.Settings;
        switch (provider)
        {
            case "openai": s.OpenAi.ProtectedApiKey = null; break;
            case "deepl": s.DeepL.ProtectedApiKey = null; break;
            case "google": s.Google.ProtectedApiKey = null; break;
        }
        _services.SaveSettings();
        OnPropertyChanged(nameof(OpenAiKeyState));
        OnPropertyChanged(nameof(DeepLKeyState));
        OnPropertyChanged(nameof(GoogleKeyState));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void BrowseTool(object? which)
    {
        var dlg = new OpenFileDialog { Filter = "Programs|*.exe", Title = L.Get("Set_BrowseTool") };
        if (dlg.ShowDialog() != true) return;
        switch (which as string)
        {
            case "ffmpeg": FfmpegPath = dlg.FileName; OnPropertyChanged(nameof(FfmpegPath)); break;
            case "ffprobe": FfprobePath = dlg.FileName; OnPropertyChanged(nameof(FfprobePath)); break;
            case "mkvmerge": MkvMergePath = dlg.FileName; OnPropertyChanged(nameof(MkvMergePath)); break;
        }
    }

    private void ClearCache()
    {
        if (MessageBox.Show(Application.Current.MainWindow!, L.Get("Set_ClearCacheConfirm"), "ALMARFELD SubFlow",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _services.Store.Clear();
        CacheCount = _services.Store.Count();
    }

    private async Task RefreshToolsAsync()
    {
        var status = await Task.Run(() => _services.Tools.GetStatus());
        ToolStatus = string.Join("\n\n", status.Select(t => t.IsAvailable
            ? $"✔  {t.Name} {t.Version}\n    {t.Path}"
            : $"✖  {t.Name}\n    {t.Message}"));
        CacheCount = await Task.Run(() => _services.Store.Count());
    }
}
