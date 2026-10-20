using System.Diagnostics;
using System.Text;

namespace ALMARFELD.SubFlow.Infrastructure.Tools;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs external tools without a shell. Arguments are passed through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so file names containing spaces, quotes or
/// shell metacharacters are never interpreted.
/// </summary>
public static class ProcessRunner
{
    private const int MaxCapturedChars = 4 * 1024 * 1024;

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken ct, Action<string>? onStdErrLine = null, Action<string>? onStdOutLine = null,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stdout.Length < MaxCapturedChars) stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (stderr.Length < MaxCapturedChars) stderr.AppendLine(e.Data);
            else { stderr.Remove(0, stderr.Length / 2); stderr.AppendLine(e.Data); } // keep the tail
            onStdErrLine?.Invoke(e.Data);
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}: {ex.Message}", ex);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.GetValueOrDefault().TotalSeconds:0} seconds.");
        }
        // Ensure async readers have flushed.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
