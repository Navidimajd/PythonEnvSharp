using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PythonEnvSharp;

/// <summary>
/// A running Python process with stderr always being read, so it can't fill up and block Python,
/// and with the end of stderr kept for error messages.
/// </summary>
internal sealed class PythonChild
{
    private const int StderrLinesKept = 50;

    private readonly ConcurrentQueue<string> _stderrTail = new();
    private readonly ILogger _logger;
    private readonly Task _stderrPump;
    private readonly Task _stdoutPump;

    public Process Process { get; }

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public string StderrTail => string.Join(Environment.NewLine, _stderrTail);

    /// <param name="psi">How to start it.</param>
    /// <param name="drainStdout">Read and discard stdout, for callers that don't use it as a message channel.</param>
    /// <param name="logger">Receives stderr lines at debug level.</param>
    public PythonChild(ProcessStartInfo psi, bool drainStdout, ILogger logger)
    {
        _logger = logger;
        Process = ProcessRunner.Start(psi);
        _stderrPump = PumpStderrAsync();
        _stdoutPump = drainStdout ? PumpStdoutAsync() : Task.CompletedTask;
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await Process.StandardError.ReadLineAsync() is { } line)
            {
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > StderrLinesKept) _stderrTail.TryDequeue(out _);
                _logger.LogDebug("python stderr: {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (await Process.StandardOutput.ReadLineAsync() is { } line)
            {
                _logger.LogDebug("python stdout: {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Explains the process's state for an error message.</summary>
    public string Describe()
    {
        var state = "the process is still running.";
        if (HasExited)
        {
            // Stderr arrives just after exit. Give it a moment so the traceback makes it in.
            _stderrPump.Wait(TimeSpan.FromSeconds(1));
            try { state = $"the process exited with code {Process.ExitCode}."; }
            catch (InvalidOperationException) { state = "the process has exited."; }
        }

        var stderr = StderrTail;
        return stderr.Length == 0 ? state : $"{state} Stderr:{Environment.NewLine}{stderr}";
    }

    /// <summary>Waits for a clean exit for up to five seconds, then kills the process and anything it started.</summary>
    public async Task ShutdownAsync(TimeSpan? gracePeriod = null)
    {
        try
        {
            using var grace = new CancellationTokenSource(gracePeriod ?? TimeSpan.FromSeconds(5));
            await Process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKillTree(Process);
        }
        catch (InvalidOperationException) { }

        // A grandchild can keep the pipes open after the kill, so don't wait for these indefinitely.
        await Task.WhenAny(Task.WhenAll(_stderrPump, _stdoutPump), Task.Delay(TimeSpan.FromSeconds(1)));
        Process.Dispose();
    }
}
