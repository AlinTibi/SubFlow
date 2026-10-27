using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed class NavItem(string key, string glyph, object page) : ObservableObject
{
    private bool _isSelected;
    public string Key { get; } = key;
    public string Title => L.Get("Nav_" + Key);
    public string Glyph { get; } = glyph;
    public object Page { get; } = page;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class MainViewModel : ObservableObject
{
    private object _currentPage;

    public MainViewModel(AppServices services)
    {
        Services = services;
        Videos = new VideosViewModel(services);
        Queue = new QueueViewModel(services);
        Translation = new TranslationViewModel(services, Videos, Queue, () => Navigate("Queue"));
        Setup = new SetupViewModel(services, Translation);
        Dashboard = new DashboardViewModel(services, Videos, Translation, Queue, Navigate, Setup);
        Editor = new EditorViewModel(services, Translation);
        History = new HistoryViewModel(services);
        Glossary = new GlossaryViewModel(services);
        Settings = new SettingsViewModel(services);

        Nav =
        [
            new NavItem("Dashboard", "", Dashboard),
            new NavItem("Setup", "", Setup),
            new NavItem("Videos", "", Videos),
            new NavItem("Translation", "", Translation),
            new NavItem("Editor", "", Editor),
            new NavItem("Queue", "", Queue),
            new NavItem("History", "", History),
            new NavItem("Glossary", "", Glossary),
            new NavItem("Settings", "", Settings),
        ];
        _currentPage = Dashboard;
        Nav[0].IsSelected = true;

        Queue.Changed += () =>
        {
            OnPropertyChanged(nameof(QueueBadge));
            if (!Queue.IsRunning) History.Refresh();
        };
        OpenUpdateCommand = new RelayCommand(() => { if (_update is not null) Shell.OpenUrl(_update.PageUrl); });
        if (services.Settings.CheckForUpdates) _ = CheckForUpdateAsync();
    }

    private UpdateInfo? _update;

    /// <summary>Set when the opt-in startup check found a newer release.</summary>
    public string? UpdateText => _update is null ? null : L.F("Main_UpdateAvailable", _update.Tag);
    public bool HasUpdate => _update is not null;
    public System.Windows.Input.ICommand OpenUpdateCommand { get; }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var latest = await UpdateChecker.GetLatestAsync(CancellationToken.None);
            if (latest is not { IsNewer: true }) return;
            _update = latest;
            OnPropertyChanged(nameof(UpdateText));
            OnPropertyChanged(nameof(HasUpdate));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Offline or rate-limited: try again next start.
        }
    }

    public AppServices Services { get; }
    public DashboardViewModel Dashboard { get; }
    public SetupViewModel Setup { get; }
    public VideosViewModel Videos { get; }
    public TranslationViewModel Translation { get; }
    public EditorViewModel Editor { get; }
    public QueueViewModel Queue { get; }
    public HistoryViewModel History { get; }
    public GlossaryViewModel Glossary { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<NavItem> Nav { get; }

    public string QueueBadge => Queue.IsRunning ? $"{Queue.Completed + Queue.Failed + Queue.Skipped}/{Queue.Total}" : "";

    public object CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public void Navigate(string key)
    {
        var item = Nav.FirstOrDefault(n => n.Key == key) ?? Nav[0];
        foreach (var n in Nav) n.IsSelected = n == item;
        CurrentPage = item.Page;
        if (item.Page == History) History.Refresh();
        if (item.Page == Setup) _ = Setup.RefreshAsync();
    }

    /// <summary>Files dropped anywhere on the window go to the Videos page.</summary>
    public async Task DropAsync(IEnumerable<string> paths)
    {
        Navigate("Videos");
        await Videos.AddPathsAsync(paths);
    }
}
