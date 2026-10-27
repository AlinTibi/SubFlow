using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed record HealthItem(string Name, bool Ok, string Detail, bool Optional = false)
{
    /// <summary>Optional one-click fix (install a program, download a model).</summary>
    public string? ActionLabel { get; init; }
    public object? ActionParameter { get; init; }
    public ICommand? Action { get; init; }
    public bool HasAction => Action is not null;
    public string Glyph => Ok ? "" : Optional ? "" : ""; // check / info / error
    public string State => Ok ? "ok" : Optional ? "optional" : "missing";
}

/// <summary>Overview: queue numbers, selected languages, engine and dependency health.</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private IReadOnlyList<HealthItem> _health = Array.Empty<HealthItem>();

    public DashboardViewModel(AppServices services, VideosViewModel videos, TranslationViewModel translation, QueueViewModel queue, Action<string> navigate, SetupViewModel setup)
    {
        Setup = setup;
        setup.Changed += () => OnPropertyChanged(nameof(ShowSetupNotice));
        _services = services;
        Videos = videos;
        Translation = translation;
        Queue = queue;
        GoCommand = new RelayCommand(p => navigate(p as string ?? "Videos"));
        RefreshHealthCommand = new AsyncCommand(RefreshHealthAsync);
        FixCommand = new AsyncCommand(FixAsync, _ => !_fixing);

        queue.Changed += Notify;
        videos.Changed += Notify;
        translation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TranslationViewModel.SelectedTargets) or nameof(TranslationViewModel.EngineId)
                or nameof(TranslationViewModel.SelectedModel)) Notify();
        };
        services.SettingsChanged += () => _ = RefreshHealthAsync();
        _ = RefreshHealthAsync();
    }

    public SetupViewModel Setup { get; }

    /// <summary>FFmpeg, Ollama or an AI model is still missing: point first-time users to Setup.</summary>
    public bool ShowSetupNotice => !Setup.IsReady;

    public VideosViewModel Videos { get; }
    public TranslationViewModel Translation { get; }
    public QueueViewModel Queue { get; }
    public ICommand GoCommand { get; }
    public ICommand RefreshHealthCommand { get; }
    public ICommand FixCommand { get; }
    private bool _fixing;

    public string TargetLanguages => Translation.SelectedTargets.Count == 0
        ? L.Get("Dash_NoLanguages")
        : string.Join(", ", Translation.SelectedTargets.Select(t => t.Language.Name));

    public string ActiveEngine
    {
        get
        {
            var engine = AppServices.Engines.FirstOrDefault(e => e.Id == Translation.EngineId);
            var name = engine?.Name ?? Translation.EngineId;
            return Translation.IsOllama && Translation.SelectedModel is { } m ? $"{name} · {m.Name}" : name;
        }
    }

    public string PrivacyNote => Translation.IsCloudEngine ? L.Get("Dash_PrivacyCloud") : L.Get("Dash_PrivacyLocal");
    public bool IsCloud => Translation.IsCloudEngine;

    public IReadOnlyList<HealthItem> Health { get => _health; private set => Set(ref _health, value); }
    public bool HasHealthProblems => Health.Any(h => !h.Ok && !h.Optional);

    private void Notify()
    {
        OnPropertyChanged(nameof(TargetLanguages));
        OnPropertyChanged(nameof(ActiveEngine));
        OnPropertyChanged(nameof(PrivacyNote));
        OnPropertyChanged(nameof(IsCloud));
        OnPropertyChanged(nameof(ShowWaitingNotice));
        OnPropertyChanged(nameof(WaitingNotice));
    }

    /// <summary>Files left in the queue (e.g. from an earlier session) while nothing is running.</summary>
    public bool ShowWaitingNotice => Queue.Waiting > 0 && !Queue.IsRunning;
    public string WaitingNotice => L.F("Dash_WaitingNotice", Queue.Waiting);

    public async Task RefreshHealthAsync()
    {
        var tools = await Task.Run(() => _services.Tools.GetStatus());
        var winget = DependencyInstaller.WingetAvailable;
        var list = tools.Select(t =>
        {
            var optional = t.Name.StartsWith("MKVToolNix", StringComparison.Ordinal);
            var item = new HealthItem(t.Name, t.IsAvailable, t.IsAvailable ? (t.Version ?? t.Path ?? "") : t.Message ?? "", optional);
            if (t.IsAvailable || !winget) return item;
            // FFprobe ships with FFmpeg, so only the FFmpeg row offers the install.
            var package = t.Name.StartsWith("FFmpeg", StringComparison.Ordinal) ? DependencyInstaller.Ffmpeg
                : optional ? DependencyInstaller.MkvToolNix : null;
            return package is null ? item : item with { ActionLabel = L.F("Dash_Install", package.Name), Action = FixCommand, ActionParameter = package };
        }).ToList();

        var ollama = await _services.CreateOllamaClient().GetStatusAsync(CancellationToken.None);
        var ollamaItem = new HealthItem("Ollama", ollama.IsRunning,
            ollama.IsRunning ? L.F("Tr_OllamaReady", ollama.Version, ollama.Models.Count) : ollama.Error ?? "");
        if (!ollama.IsRunning && DependencyInstaller.OllamaInstalled)
            ollamaItem = ollamaItem with { ActionLabel = L.Get("Dash_StartOllama"), Action = FixCommand, ActionParameter = StartOllamaAction };
        else if (!ollama.IsRunning && winget)
            ollamaItem = ollamaItem with { ActionLabel = L.F("Dash_Install", "Ollama"), Action = FixCommand, ActionParameter = DependencyInstaller.Ollama };
        else if (ollama.IsRunning && ollama.Models.Count == 0)
            ollamaItem = ollamaItem with { Ok = false, Detail = L.Get("Dash_NoModels"), ActionLabel = L.F("Dash_PullModel", RecommendedModel), Action = FixCommand, ActionParameter = RecommendedModel };
        list.Add(ollamaItem);

        var ocr = await Task.Run(() => Ocr.WindowsPgsOcrEngine.InstalledLanguages);
        list.Add(new HealthItem(L.Get("Dash_Ocr"), ocr.Count > 0,
            ocr.Count > 0 ? string.Join(", ", ocr) : L.Get("Dash_OcrMissing"), Optional: true));
        Health = list;
        OnPropertyChanged(nameof(HasHealthProblems));
    }

    private const string RecommendedModel = "qwen3:8b";
    private static readonly object StartOllamaAction = new();

    /// <summary>Installs a missing program with winget or pulls a model, after the user confirms.</summary>
    private async Task FixAsync(object? parameter)
    {
        var owner = System.Windows.Application.Current.MainWindow;
        if (ReferenceEquals(parameter, StartOllamaAction))
        {
            try { DependencyInstaller.StartOllama(); } catch (Exception ex) { System.Windows.MessageBox.Show(owner!, ex.Message, AppInfo.ProductName); }
            await Task.Delay(TimeSpan.FromSeconds(4));
            await RefreshHealthAsync();
            return;
        }
        var (question, run) = parameter switch
        {
            InstallablePackage p => (L.F("Dash_InstallConfirm", p.Name, p.WingetId, p.LicenseNote), (Func<Task<int>>)(() => DependencyInstaller.InstallAsync(p))),
            string model => (L.F("Dash_PullConfirm", model), () => DependencyInstaller.PullModelAsync(model)),
            _ => (null, null!),
        };
        if (question is null || owner is null) return;
        if (System.Windows.MessageBox.Show(owner, question, AppInfo.ProductName, System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;

        _fixing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var exit = await run();
            if (exit != 0)
                System.Windows.MessageBox.Show(owner, L.F("Dash_FixFailed", exit), AppInfo.ProductName,
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(owner, ex.Message, AppInfo.ProductName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _fixing = false;
            _services.Tools.Refresh();
            await RefreshHealthAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
