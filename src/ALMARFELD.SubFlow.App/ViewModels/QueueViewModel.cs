using System.Globalization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Concurrent;
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
        RetryFailedCommand = new AsyncCommand(RetryFailedAsync, () => Items.Any(i => i.Item.CanRetry));
        RetryCommand = new AsyncCommand(p => RetryOneAsync(p as JobItemViewModel ?? Selected),
            p => (p as JobItemViewModel ?? Selected) is { } vm && vm.Item.Status is not (JobStatus.Running or JobStatus.Pending));
        RemoveCommand = new RelayCommand(p => { if ((p as JobItemViewModel ?? Selected) is { } vm) Remove([vm]); },
            p => (p as JobItemViewModel ?? Selected) is not null);
        RemoveWaitingCommand = new RelayCommand(RemoveWaiting, () => Items.Any(i => i.Item.Status is JobStatus.Pending or JobStatus.Interrupted));
        ClearFinishedCommand = new RelayCommand(ClearFinished, () => Items.Any(i => i.Item.IsFinished || i.Item.Status is JobStatus.Failed or JobStatus.Cancelled));
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
    public ICommand RemoveWaitingCommand { get; }
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

    /// <summary>
    /// Adds items to the queue (persisted) and optionally starts processing. A file that is already
    /// waiting or being translated is not added twice; an older finished row of the same file is
    /// replaced by the new one. Returns how many items were added.
    /// </summary>
    public async Task<int> EnqueueAsync(IReadOnlyList<JobItem> items, bool start)
    {
        var added = 0;
        var duplicates = 0;
        foreach (var item in items)
        {
            var existing = Items.Where(i => SamePath(i.Item.SourcePath, item.SourcePath)).ToList();
            if (existing.Any(i => i.Item.Status is JobStatus.Pending or JobStatus.Running or JobStatus.Interrupted))
            {
                duplicates++;
                continue;
            }
            foreach (var old in existing)
            {
                Items.Remove(old); // finished rows stay in History
                if (old.Item.Status == JobStatus.Cancelled) _services.Store.Delete(old.Item.Id);
            }
            _services.Store.Upsert(item);
            Items.Add(new JobItemViewModel(item));
            added++;
        }
        Notice = duplicates == 0 ? "" : L.F("Queue_AlreadyQueued", duplicates);
        NotifyStats();
        CommandManager.InvalidateRequerySuggested();
        if (start && added > 0 && !IsRunning) await RunAsync();
        // While running, new Pending items are picked up at the end of the current pass.
        return added;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private string _notice = "";

    /// <summary>Short information line under the header (for example: files that were already queued).</summary>
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => Notice.Length > 0;

    private BatchRunner? _runner;
    private readonly ConcurrentDictionary<Guid, byte> _removed = new();

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
        CommandManager.InvalidateRequerySuggested();
        try
        {
          for (var pass = 0; toRun.Count > 0 && pass < 1000; pass++)
          {
            // Items remember the engine they were queued with; run each engine group in turn.
            foreach (var group in toRun.GroupBy(i => i.Item.Options.ProviderId))
            {
                if (_cts.IsCancellationRequested) break;
                var provider = _services.CreateProvider(group.Key);
                EngineText = $"{provider.DisplayName} · {provider.ModelId}";
                var processor = _services.CreateProcessor(_services.CreateTranslator(provider));
                var runner = new BatchRunner(processor, _services.Store, _services.Pause);
                _runner = runner;
                var lookup = group.ToDictionary(i => i.Item.Id);
                runner.ItemChanged += changed => Ui.Run(() => OnItemChanged(lookup, changed));
                foreach (var vm in group) { vm.Item.Status = JobStatus.Pending; vm.Refresh(); }
                await runner.RunAsync(group.Select(g => g.Item).ToList(), _services.Settings.Concurrency, _cts.Token,
                    shouldRun: item => !_removed.ContainsKey(item.Id));
            }
            // Pick up items queued or retried while this pass was running.
            toRun = only is not null || _cts.IsCancellationRequested
                ? new List<JobItemViewModel>()
                : Items.Where(i => i.Item.Status == JobStatus.Pending).ToList();
          }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, "ALMARFELD SubFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runner = null;
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

    private void OnItemChanged(Dictionary<Guid, JobItemViewModel> lookup, JobItem changed)
    {
        if (_removed.ContainsKey(changed.Id))
        {
            // Stopped because the user removed it: forget it completely once it has stopped.
            if (changed.Status != JobStatus.Running) _services.Store.Delete(changed.Id);
            return;
        }
        if (!lookup.TryGetValue(changed.Id, out var vm)) return;
        vm.Refresh();
        if (changed.Status is not JobStatus.Running and not JobStatus.Pending && _countedThisRun.Add(changed.Id)) _completedThisRun++;
        NotifyStats();
        CommandManager.InvalidateRequerySuggested();
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
        // While a batch runs, reset items are picked up at the end of the current pass.
        return IsRunning ? Task.CompletedTask : RunAsync(retry);
    }

    private Task RetryOneAsync(JobItemViewModel? vm)
    {
        if (vm is null || vm.Item.Status == JobStatus.Running) return Task.CompletedTask;
        Reset(vm);
        return IsRunning ? Task.CompletedTask : RunAsync([vm]);
    }

    private void Reset(JobItemViewModel vm)
    {
        vm.Item.Status = JobStatus.Pending;
        vm.Item.Progress = 0;
        vm.Item.Message = null;
        _services.Store.Upsert(vm.Item);
        vm.Refresh();
        NotifyStats();
    }

    /// <summary>
    /// Removes rows from the queue. A file being translated right now is stopped first (after a
    /// confirmation); the other files of the batch continue. Finished files stay in History.
    /// </summary>
    public void Remove(IReadOnlyList<JobItemViewModel> rows)
    {
        if (rows.Count == 0) return;
        var running = rows.Where(r => r.Item.Status == JobStatus.Running).ToList();
        if (running.Count > 0)
        {
            var text = running.Count == 1 ? L.F("Queue_StopOne", running[0].FileName) : L.F("Queue_StopMany", running.Count);
            if (MessageBox.Show(Application.Current.MainWindow!, text, "ALMARFELD SubFlow", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        foreach (var vm in rows)
        {
            Items.Remove(vm);
            if (vm.Item.Status == JobStatus.Running)
            {
                _removed.TryAdd(vm.Item.Id, 0);
                _runner?.CancelItem(vm.Item.Id);
            }
            else if (vm.Item.Status is JobStatus.Pending or JobStatus.Interrupted or JobStatus.Cancelled)
            {
                // Never processed: drop it entirely so it does not come back after a restart.
                _removed.TryAdd(vm.Item.Id, 0);
                _runner?.CancelItem(vm.Item.Id);
                _services.Store.Delete(vm.Item.Id);
            }
        }
        if (Selected is not null && !Items.Contains(Selected)) Selected = null;
        NotifyStats();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveWaiting()
    {
        var waiting = Items.Where(i => i.Item.Status is JobStatus.Pending or JobStatus.Interrupted).ToList();
        if (waiting.Count == 0) return;
        if (MessageBox.Show(Application.Current.MainWindow!, L.F("Queue_RemoveWaitingConfirm", waiting.Count), "ALMARFELD SubFlow",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Remove(waiting);
    }

    private void ClearFinished()
    {
        foreach (var vm in Items.Where(i => i.Item.IsFinished || i.Item.Status is JobStatus.Failed or JobStatus.Cancelled).ToList())
        {
            Items.Remove(vm);
            if (vm.Item.Status == JobStatus.Cancelled) _services.Store.Delete(vm.Item.Id);
        }
        Notice = "";
        NotifyStats();
    }
}
