namespace ALMARFELD.SubFlow.Core.Processing;

/// <summary>
/// Cooperative pause. Work checks the gate at safe points (between translation batches and
/// before starting a new file); running FFmpeg/mkvmerge processes are never suspended mid-write.
/// </summary>
public sealed class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _released = CreateReleased();

    public bool IsPaused { get; private set; }

    public event EventHandler? StateChanged;

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused) return;
            IsPaused = true;
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        TaskCompletionSource toRelease;
        lock (_lock)
        {
            if (!IsPaused) return;
            IsPaused = false;
            toRelease = _released;
        }
        toRelease.TrySetResult();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task task;
        lock (_lock) task = _released.Task;
        return task.IsCompleted ? Task.CompletedTask : task.WaitAsync(ct);
    }

    private static TaskCompletionSource CreateReleased()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
