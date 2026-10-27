using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Translation.Providers;

namespace ALMARFELD.SubFlow.App.ViewModels;

/// <summary>One thing SubFlow needs (FFmpeg, Ollama, …) with its state and the button that fixes it.</summary>
public sealed class SetupStep : ObservableObject
{
    private bool _ok;
    private string _status = "";
    private string? _actionLabel;

    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool Optional { get; init; }
    public string? ManualUrl { get; init; }

    public bool Ok { get => _ok; set { if (Set(ref _ok, value)) OnPropertyChanged(nameof(State)); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string? ActionLabel { get => _actionLabel; set { if (Set(ref _actionLabel, value)) OnPropertyChanged(nameof(HasAction)); } }
    public object? ActionParameter { get; set; }
    public bool HasAction => ActionLabel is not null;
    public string State => Ok ? "ok" : Optional ? "optional" : "missing";
}

/// <summary>A recommended AI model with its download state.</summary>
public sealed class ModelChoice : ObservableObject
{
    private bool _installed;
    private bool _downloading;
    private double _progress;
    private string _progressText = "";
    private bool _selected;

    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Size { get; init; }
    public required string Hardware { get; init; }
    public bool Recommended { get; init; }

    public bool Installed { get => _installed; set { if (Set(ref _installed, value)) Notify(); } }
    public bool Downloading { get => _downloading; set { if (Set(ref _downloading, value)) Notify(); } }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public bool Selected { get => _selected; set { if (Set(ref _selected, value)) Notify(); } }

    public bool CanDownload => !Installed && !Downloading;
    public bool CanUse => Installed && !Selected;

    private void Notify()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanUse));
    }
}

/// <summary>
/// "Setup" page: everything SubFlow needs, in order, each with a one-click install or download
/// (always after a confirmation), so people who have never used Ollama can get started.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly TranslationViewModel _translation;
    private CancellationTokenSource? _download;
    private bool _busy;
    private bool _ollamaRunning;

    public SetupViewModel(AppServices services, TranslationViewModel translation)
    {
        _services = services;
        _translation = translation;

        Ffmpeg = new SetupStep { Number = "1", Title = "FFmpeg", Description = L.Get("Setup_FfmpegText"), ManualUrl = "https://ffmpeg.org/download.html" };
        Ollama = new SetupStep { Number = "2", Title = "Ollama", Description = L.Get("Setup_OllamaText"), ManualUrl = "https://ollama.com/download" };
        MkvToolNix = new SetupStep { Number = "+", Title = "MKVToolNix", Description = L.Get("Setup_MkvText"), Optional = true, ManualUrl = "https://mkvtoolnix.download/downloads.html" };
        Ocr = new SetupStep { Number = "+", Title = L.Get("Dash_Ocr"), Description = L.Get("Setup_OcrText"), Optional = true };
        Steps = [Ffmpeg, Ollama];
        OptionalSteps = [MkvToolNix, Ocr];

        Models =
        [
            new ModelChoice { Name = "qwen3:8b", Title = "Qwen3 8B", Recommended = true, Size = "≈ 5.2 GB", Hardware = L.Get("Setup_Hw8"), Description = L.Get("Setup_ModelQwen8") },
            new ModelChoice { Name = "gemma3:4b", Title = "Gemma 3 4B", Size = "≈ 3.3 GB", Hardware = L.Get("Setup_Hw4"), Description = L.Get("Setup_ModelGemma4") },
            new ModelChoice { Name = "gemma3:12b", Title = "Gemma 3 12B", Size = "≈ 8.1 GB", Hardware = L.Get("Setup_Hw12"), Description = L.Get("Setup_ModelGemma12") },
            new ModelChoice { Name = "qwen3:14b", Title = "Qwen3 14B", Size = "≈ 9.3 GB", Hardware = L.Get("Setup_Hw16"), Description = L.Get("Setup_ModelQwen14") },
        ];

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !_busy);
        FixCommand = new AsyncCommand(FixAsync, _ => !_busy);
        OpenUrlCommand = new RelayCommand(p => Shell.OpenUrl(p as string ?? ""));
        DownloadCommand = new AsyncCommand(p => DownloadAsync(p as ModelChoice), p => p is ModelChoice { CanDownload: true } && _download is null);
        CancelDownloadCommand = new RelayCommand(() => _download?.Cancel(), () => _download is not null);
        UseCommand = new AsyncCommand(p => UseAsync(p as ModelChoice), p => p is ModelChoice { CanUse: true });
        _ = RefreshAsync();
    }

    public SetupStep Ffmpeg { get; }
    public SetupStep Ollama { get; }
    public SetupStep MkvToolNix { get; }
    public SetupStep Ocr { get; }
    public IReadOnlyList<SetupStep> Steps { get; }
    public IReadOnlyList<SetupStep> OptionalSteps { get; }
    public ObservableCollection<ModelChoice> Models { get; }

    public ICommand RefreshCommand { get; }
    public ICommand FixCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand UseCommand { get; }

    /// <summary>All required parts are present and at least one model is installed.</summary>
    public bool IsReady => Ffmpeg.Ok && Ollama.Ok && HasModel;
    public bool HasModel => _otherModels > 0;
    public bool OllamaRunning { get => _ollamaRunning; private set => Set(ref _ollamaRunning, value); }
    public string ModelsHint => OllamaRunning ? L.Get("Setup_ModelsHint") : L.Get("Setup_ModelsNeedOllama");

    private int _otherModels;

    public event Action? Changed;

    public async Task RefreshAsync()
    {
        _services.Tools.Refresh();
        var tools = await Task.Run(() => _services.Tools.GetStatus());
        var ffmpeg = tools.First(t => t.Name.StartsWith("FFmpeg", StringComparison.Ordinal));
        var ffprobe = tools.First(t => t.Name.StartsWith("FFprobe", StringComparison.Ordinal));
        var mkv = tools.First(t => t.Name.StartsWith("MKVToolNix", StringComparison.Ordinal));
        var winget = DependencyInstaller.WingetAvailable;

        Ffmpeg.Ok = ffmpeg.IsAvailable && ffprobe.IsAvailable;
        Ffmpeg.Status = Ffmpeg.Ok ? L.F("Setup_Installed", ffmpeg.Version) : L.Get("Setup_NotInstalled");
        SetAction(Ffmpeg, Ffmpeg.Ok, winget ? L.F("Dash_Install", "FFmpeg") : null, DependencyInstaller.Ffmpeg);

        MkvToolNix.Ok = mkv.IsAvailable;
        MkvToolNix.Status = mkv.IsAvailable ? L.F("Setup_Installed", mkv.Version) : L.Get("Setup_NotInstalledOptional");
        SetAction(MkvToolNix, mkv.IsAvailable, winget ? L.F("Dash_Install", "MKVToolNix") : null, DependencyInstaller.MkvToolNix);

        var status = await _services.CreateOllamaClient().GetStatusAsync(CancellationToken.None);
        OllamaRunning = status.IsRunning;
        Ollama.Ok = status.IsRunning;
        if (status.IsRunning)
        {
            Ollama.Status = L.F("Setup_OllamaRunning", status.Version);
            Ollama.ActionLabel = null;
        }
        else if (DependencyInstaller.OllamaInstalled)
        {
            Ollama.Status = L.Get("Setup_OllamaStopped");
            Ollama.ActionLabel = L.Get("Dash_StartOllama");
            Ollama.ActionParameter = StartOllama;
        }
        else
        {
            Ollama.Status = L.Get("Setup_NotInstalled");
            SetAction(Ollama, false, winget ? L.F("Dash_Install", "Ollama") : null, DependencyInstaller.Ollama);
        }

        var installed = status.Models.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Models)
        {
            m.Installed = installed.Contains(m.Name) || installed.Contains(m.Name + ":latest");
            m.Selected = m.Installed && string.Equals(_services.Settings.Ollama.Model, m.Name, StringComparison.OrdinalIgnoreCase);
        }
        _otherModels = status.Models.Count(ModelPicker.IsSuitable);

        var ocr = await Task.Run(() => ALMARFELD.SubFlow.Ocr.WindowsPgsOcrEngine.InstalledLanguages);
        this.Ocr.Ok = ocr.Count > 0;
        this.Ocr.Status = ocr.Count > 0 ? string.Join(", ", ocr) : L.Get("Dash_OcrMissing");
        this.Ocr.ActionLabel = L.Get("Setup_OpenLanguageSettings");
        this.Ocr.ActionParameter = OpenLanguageSettings;

        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(ModelsHint));
        CommandManager.InvalidateRequerySuggested();
        Changed?.Invoke();
    }

    private static void SetAction(SetupStep step, bool ok, string? label, object parameter)
    {
        step.ActionLabel = ok ? null : label;
        step.ActionParameter = parameter;
    }

    private static readonly object StartOllama = new();
    private static readonly object OpenLanguageSettings = new();

    private async Task FixAsync(object? parameter)
    {
        var owner = Application.Current.MainWindow;
        if (ReferenceEquals(parameter, OpenLanguageSettings))
        {
            Shell.OpenWindowsSettings("ms-settings:regionlanguage");
            return;
        }
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (ReferenceEquals(parameter, StartOllama))
            {
                DependencyInstaller.StartOllama();
                await Task.Delay(TimeSpan.FromSeconds(4));
            }
            else if (parameter is InstallablePackage package && owner is not null)
            {
                if (MessageBox.Show(owner, L.F("Dash_InstallConfirm", package.Name, package.WingetId, package.LicenseNote), AppInfo.ProductName,
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var exit = await DependencyInstaller.InstallAsync(package);
                if (exit != 0)
                    MessageBox.Show(owner, L.F("Dash_FixFailed", exit), AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
                // A freshly installed Ollama starts its server by itself; give it a moment.
                if (package == DependencyInstaller.Ollama) await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            if (owner is not null) MessageBox.Show(owner, ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            _services.SaveSettings(); // refreshes tool detection everywhere
            await RefreshAsync();
        }
    }

    private async Task DownloadAsync(ModelChoice? model)
    {
        var owner = Application.Current.MainWindow;
        if (model is null || owner is null) return;
        if (!OllamaRunning)
        {
            MessageBox.Show(owner, L.Get("Setup_ModelsNeedOllama"), AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(owner, L.F("Setup_DownloadConfirm", model.Title, model.Size), AppInfo.ProductName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _download = new CancellationTokenSource();
        model.Downloading = true;
        model.Progress = 0;
        model.ProgressText = L.Get("Setup_Starting");
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var progress = new Progress<(double Fraction, string Status)>(p =>
            {
                if (p.Fraction >= 0)
                {
                    model.Progress = p.Fraction * 100;
                    model.ProgressText = $"{p.Fraction:P0}";
                }
                else model.ProgressText = p.Status;
            });
            await _services.CreateOllamaClient().PullAsync(model.Name, progress, _download.Token);
            await UseAsync(model);
        }
        catch (OperationCanceledException)
        {
            model.ProgressText = L.Get("Setup_DownloadCancelled");
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            model.Downloading = false;
            _download.Dispose();
            _download = null;
            await RefreshAsync();
        }
    }

    /// <summary>Makes the model the one used for translation.</summary>
    private async Task UseAsync(ModelChoice? model)
    {
        if (model is null) return;
        _services.Settings.Ollama.Model = model.Name;
        _translation.EngineId = "ollama";
        _services.SaveSettings();
        await _translation.RefreshModelsAsync();
        foreach (var m in Models) m.Selected = m == model;
        CommandManager.InvalidateRequerySuggested();
    }
}
