using ALMARFELD.SubFlow.Core.Jobs;

namespace ALMARFELD.SubFlow.Core.Processing;

/// <summary>
/// Runs queued items with bounded concurrency, persisting every state change so that an
/// interrupted session can be resumed.
/// </summary>
public sealed class BatchRunner
{
    private readonly FileProcessor _processor;
    private readonly IJobStore _store;
    private readonly PauseGate _pause;

    public BatchRunner(FileProcessor processor, IJobStore store, PauseGate pause)
    {
        _processor = processor;
        _store = store;
        _pause = pause;
    }

    /// <summary>Raised (from worker threads) whenever an item changes. Progress events are throttled.</summary>
    public event Action<JobItem>? ItemChanged;

    public async Task RunAsync(IReadOnlyList<JobItem> items, int concurrency, CancellationToken ct)
    {
        concurrency = Math.Clamp(concurrency, 1, 8);
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>();
        foreach (var item in items)
        {
            try
            {
                await gate.WaitAsync(ct);
                await _pause.WaitIfPausedAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            tasks.Add(Task.Run(async () =>
            {
                try { await RunOneAsync(item, ct); }
                finally { gate.Release(); }
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        // Items never started because of cancellation stay Pending and remain in the queue.
    }

    private async Task RunOneAsync(JobItem item, CancellationToken ct)
    {
        var lastPersist = DateTime.MinValue;
        var progress = new SyncProgress<FileProgress>(p =>
        {
            ItemChanged?.Invoke(p.Item);
            if ((DateTime.UtcNow - lastPersist).TotalSeconds >= 2)
            {
                lastPersist = DateTime.UtcNow;
                SafePersist(p.Item);
            }
        });

        item.Status = JobStatus.Running;
        SafePersist(item);
        ItemChanged?.Invoke(item);

        await _processor.ProcessAsync(item, progress, ct);

        SafePersist(item);
        ItemChanged?.Invoke(item);
    }

    private void SafePersist(JobItem item)
    {
        try { _store.Upsert(item); }
        catch { /* persistence problems must not abort media processing */ }
    }
}
