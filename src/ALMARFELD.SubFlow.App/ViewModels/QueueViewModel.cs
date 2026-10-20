using System.Globalization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Processing;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed class JobItemViewModel(JobItem item) : ObservableObject
{
    public JobItem Item { get; } = item;
    public string FileName => Path.GetFileName(Item.SourcePath);
    public string Folder => Path.GetDirectoryName(Item.SourcePath) ?? "";
    public JobStatus Status => Item.Status;
    public double Progress => Item.Progress * 100;
    public string Message => Item.Message ?? "";
    public string Languages => string.Join(", ", Item.Options.TargetLanguages.Select(c => c.ToUpperInvariant()));
    public string Warnings => string.Join("\n", Item.Warnings);
    public bool HasWarnings => Item.Warnings.Count > 0;
    public string? OutputPath => Item.OutputPath;
    public bool IsActive => Item.Status == JobStatus.Running;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(IsActive));
    }
}

/// <summary>Queue page: runs the batch, exposes progress, pause/resume, cancel and retry.</summary>
public sealed class QueueViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private JobItemViewModel? _selected;
    private readonly Stopwatch _runClock = new();
    private int _completedThisRun;
    private readonly HashSet<Guid> _countedThisRun = new();
    private string _engineText = "";

    public QueueViewModel(AppServices services)
    {
        _services = services;
        _services.Pause.StateChanged += (_, _) => Ui.Run(() => { OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(StateText)); });

        StartCommand = new AsyncCommand(() => RunAsync(), () => !IsRunning && Items.Any(i => i.Item.Status is JobStatus.Pending or JobStatus.Interrupted));
        PauseCommand = new RelayCommand(() => { if (IsPaused) _services.Pause.Resume(); else _services.Pause.Pause(); }, () => IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync, () => !IsRunning && Items.Any(i => i.Item.CanRetry));
        RetryCommand = new AsyncCommand(p => RetryOneAsync(p as JobItemViewModel ?? Selected), _ => !IsRunning);
        RemoveCommand = new RelayCommand(p => Remove(p as JobItemViewModel ?? Selected), _ => !IsRunning);
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Items.Any(i => i.Item.IsFinished));
        OpenOutputCommand = new RelayCommand(p => Shell.ShowInExplorer((p as JobItemViewModel ?? Selected)?.OutputPath),
            p => (p as JobItemViewModel ?? Selected)?.OutputPath is not null);

        // Items interrupted by a previous session come back as resumable.
        _services.Store.MarkRunningAsInterrupted();
        foreach (var item in _services.Store.GetQueue()) Items.Add(new JobItemViewModel(item));
    }

    public ObservableCollection<JobItemViewModel> Items { get; } = new();
    public JobItemViewModel? Selected { get => _selected; set => Set(ref _selected, value); }

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand OpenOutputCommand { get; }

    public bool IsRunning { get => _isRunning; private set { Set(ref _isRunning, value); OnPropertyChanged(nameof(StateText)); } }
    public bool IsPaused => _services.Pause.IsPaused;
    public string EngineText { get => _engineText; private set => Set(ref _engineText, value); }

    public int Waiting => Items.Count(i => i.Item.Status is JobStatus.Pending or JobStatus.Interrupted);
    public int Completed => Items.Count(i => i.Item.Status is JobStatus.Completed or JobStatus.CompletedWithWarnings);
    public int Failed => Items.Count(i => i.Item.Status == JobStatus.Failed);
    public int Skipped => Items.Count(i => i.Item.Status == JobStatus.Skipped);
    public int Total => Items.Count;
    public bool HasInterrupted => Items.Any(i => i.Item.Status == JobStatus.Interrupted);
    public double OverallProgress => Items.Count == 0 ? 0 : Items.Average(i => i.Item.IsFinished || i.Item.Status == JobStatus.Failed ? 1 : i.Item.Progress) * 100;

    public string StateText => IsRunning ? (IsPaused ? L.Get("Queue_Paused") : L.Get("Queue_Running")) : L.Get("Queue_Idle");

    /// <summary>Remaining time, only shown once real throughput has been measured in this run.</summary>
    public string Eta
    {
        get
        {
            if (!IsRunning || _completedThisRun < 1 || IsPaused) return "";
            var perItem = _runClock.Elapsed / _completedThisRun;
            var remaining = Items.Count(i => i.Item.Status is JobStatus.Pending or JobStatus.Running or JobStatus.Interrupted);
            var concurrency = Math.Max(1, _services.Settings.Concurrency);
            var eta = TimeSpan.FromTicks(perItem.Ticks * remaining / concurrency);
            return L.F("Queue_Eta", eta.TotalHours >= 1 ? eta.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : eta.ToString(@"m\:ss", CultureInfo.InvariantCulture), _completedThisRun);
        }
    }

    public event Action? Changed;

    private void NotifyStats()
    {
        foreach (var name in new[] { nameof(Waiting), nameof(Completed), nameof(Failed), nameof(Skipped), nameof(Total),
                     nameof(OverallProgress), nameof(Eta), nameof(HasInterrupted) })
            OnPropertyChanged(name);
        Changed?.Invoke();
    }

    /// <summary>Adds items to the queue (persisted) and optionally starts processing.</summary>
    public async Task EnqueueAsync(IReadOnlyList<JobItem> items, bool start)
    {
        foreach (var item in items)
        {
            _services.Store.Upsert(item);
            Items.Add(new JobItemViewModel(item));
        }
        NotifyStats();
        if (start && !IsRunning) await RunAsync();
        // While running, new Pending items are picked up at the end of the current pass.
    }

    private async Task RunAsync(IReadOnlyList<JobItemViewModel>? only = null)
    {
        var toRun = (only ?? Items.Where(i => i.Item.Status is JobStatus.Pending or JobStatus.Interrupted).ToList()).ToList();
        if (toRun.Count == 0) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();
        _completedThisRun = 0;
        _countedThisRun.Clear();
        _runClock.Restart();
        _services.Pause.Resume();
        try
        {
          while (toRun.Count > 0)
          {
            // Items remember the engine they were queued with; run each engine group in turn.
            foreach (var group in toRun.GroupBy(i => i.Item.Options.ProviderId))
            {
                if (_cts.IsCancellationRequested) break;
                var provider = _services.CreateProvider(group.Key);
                EngineText = $"{provider.DisplayName} · {provider.ModelId}";
                var processor = _services.CreateProcessor(_services.CreateTranslator(provider));
                var runner = new BatchRunner(processor, _services.Store, _services.Pause);
                var lookup = group.ToDictionary(i => i.Item.Id);
                runner.ItemChanged += changed => Ui.Run(() =>
                {
                    if (!lookup.TryGetValue(changed.Id, out var vm)) return;
                    vm.Refresh();
                    if (changed.Status is not JobStatus.Running and not JobStatus.Pending && _countedThisRun.Add(changed.Id)) _completedThisRun++;
                    NotifyStats();
                });
                foreach (var vm in group) { vm.Item.Status = JobStatus.Pending; vm.Refresh(); }
                await runner.RunAsync(group.Select(g => g.Item).ToList(), _services.Settings.Concurrency, _cts.Token);
            }
            // Pick up items queued while this run was in progress.
            toRun = only is not null || _cts.IsCancellationRequested
                ? new List<JobItemViewModel>()
                : Items.Where(i => i.Item.Status == JobStatus.Pending && !toRun.Contains(i)).ToList();
          }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runClock.Stop();
            IsRunning = false;
            _services.Pause.Resume();
            _cts.Dispose();
            _cts = null;
            foreach (var vm in Items) vm.Refresh();
            NotifyStats();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _services.Pause.Resume(); // let paused workers observe the cancellation
    }

    private Task RetryFailedAsync()
    {
        var retry = Items.Where(i => i.Item.CanRetry).ToList();
        foreach (var vm in retry) Reset(vm);
        return RunAsync(retry);
    }

    private Task RetryOneAsync(JobItemViewModel? vm)
    {
        if (vm is null) return Task.CompletedTask;
        Reset(vm);
        return RunAsync([vm]);
    }

    private void Reset(JobItemViewModel vm)
    {
        vm.Item.Status = JobStatus.Pending;
        vm.Item.Progress = 0;
        vm.Item.Message = null;
        _services.Store.Upsert(vm.Item);
        vm.Refresh();
    }

    private void Remove(JobItemViewModel? vm)
    {
        if (vm is null) return;
        Items.Remove(vm);
        // Finished and failed items stay in History; never-finished ones are dropped entirely.
        if (vm.Item.Status is JobStatus.Pending or JobStatus.Interrupted or JobStatus.Cancelled)
            _services.Store.Delete(vm.Item.Id);
        NotifyStats();
    }

    private void ClearFinished()
    {
        foreach (var vm in Items.Where(i => i.Item.IsFinished).ToList()) Items.Remove(vm);
        NotifyStats();
    }
}
