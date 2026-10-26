using System.Collections.Concurrent;
using ALMARFELD.SubFlow.Core.Jobs;

namespace ALMARFELD.SubFlow.Core.Processing;

/// <summary>
/// Runs queued items with bounded concurrency, persisting every state change so that an
/// interrupted session can be resumed. Single items can be stopped while the rest continue.
/// </summary>
public sealed class BatchRunner
{
    private readonly FileProcessor _processor;
    private readonly IJobStore _store;
    private readonly PauseGate _pause;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public BatchRunner(FileProcessor processor, IJobStore store, PauseGate pause)
    {
        _processor = processor;
        _store = store;
        _pause = pause;
    }

    /// <summary>Raised (from worker threads) whenever an item changes. Progress events are throttled.</summary>
    public event Action<JobItem>? ItemChanged;

    /// <summary>
    /// Runs the items in order. <paramref name="shouldRun"/> is asked just before each item starts,
    /// so items removed from the queue (or already handled) while the batch runs are skipped.
    /// </summary>
    public async Task RunAsync(IReadOnlyList<JobItem> items, int concurrency, CancellationToken ct, Func<JobItem, bool>? shouldRun = null)
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
            // Already running elsewhere, already done, or removed by the user meanwhile.
            if (item.Status == JobStatus.Running || item.IsFinished || shouldRun?.Invoke(item) == false)
            {
                gate.Release();
                continue;
            }
            tasks.Add(Task.Run(async () =>
            {
                try { await RunOneAsync(item, shouldRun, ct); }
                finally { gate.Release(); }
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        // Items never started because of cancellation stay Pending and remain in the queue.
    }

    /// <summary>Stops one running item; it ends as Cancelled and the batch continues with the next one.</summary>
    public bool CancelItem(Guid id)
    {
        if (!_running.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    private async Task RunOneAsync(JobItem item, Func<JobItem, bool>? shouldRun, CancellationToken ct)
    {
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[item.Id] = itemCts;
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

        try
        {
            // Recheck after registering cancellation: removal can race with worker scheduling.
            if (shouldRun?.Invoke(item) == false) return;
            item.Status = JobStatus.Running;
            SafePersist(item);
            ItemChanged?.Invoke(item);

            await _processor.ProcessAsync(item, progress, itemCts.Token);
        }
        finally
        {
            _running.TryRemove(item.Id, out _);
        }

        SafePersist(item);
        ItemChanged?.Invoke(item);
    }

    private void SafePersist(JobItem item)
    {
        try { _store.Upsert(item); }
        catch { /* persistence problems must not abort media processing */ }
    }
}
