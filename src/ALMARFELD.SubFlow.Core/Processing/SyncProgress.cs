namespace ALMARFELD.SubFlow.Core.Processing;

/// <summary>
/// IProgress that invokes the handler synchronously on the reporting thread. Unlike
/// <see cref="Progress{T}"/> it never posts late callbacks after the work has finished.
/// </summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
