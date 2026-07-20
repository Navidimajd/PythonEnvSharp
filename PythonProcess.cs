using System.Diagnostics;
using System.Text;

namespace PythonEnvSharp;

/// <summary>
/// Bidirectional communication with a Python process via stdin/stdout.
/// Simpler than named pipes, works everywhere.
/// </summary>
public sealed class PythonProcess : IAsyncDisposable
{
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Start a Python process and connect via stdin/stdout.
    /// </summary>
    public async Task StartAsync(string pythonExe, string scriptPath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _process = Process.Start(psi) ?? throw new PythonEnvException("Failed to start Python process");

        var utf8NoBom = new UTF8Encoding(false);
        _writer = new StreamWriter(_process.StandardInput.BaseStream, utf8NoBom, leaveOpen: true)
        {
            AutoFlush = true
        };
        _reader = new StreamReader(_process.StandardOutput.BaseStream, utf8NoBom, leaveOpen: true);

        // Wait for READY signal from Python
        var timeout = Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            if (_reader.EndOfStream) break;
            var line = await _reader.ReadLineAsync();
            if (line == "READY")
            {
                return;
            }
        }
    }

    /// <summary>
    /// Send a message to Python.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PythonProcess));
        if (_writer is null) throw new InvalidOperationException("Not started");

        await _writer.WriteAsync(message);
        await _writer.WriteAsync("\n");
        await _writer.FlushAsync();
    }

    /// <summary>
    /// Send a message and wait for response.
    /// </summary>
    public async Task<string> SendReceiveAsync(string message, CancellationToken ct = default)
    {
        await SendAsync(message, ct);
        return await ReceiveAsync(ct);
    }

    /// <summary>
    /// Receive a message from Python.
    /// </summary>
    public async Task<string> ReceiveAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PythonProcess));
        if (_reader is null) throw new InvalidOperationException("Not started");

        var line = await _reader.ReadLineAsync();
        return line ?? throw new PythonEnvException("Python process disconnected");
    }

    /// <summary>
    /// Send JSON-RPC style request and get response.
    /// </summary>
    public async Task<string> JsonRpcAsync(string method, string jsonParams, CancellationToken ct = default)
    {
        var request = $"{{\"method\":\"{method}\",\"params\":{jsonParams}}}";
        return await SendReceiveAsync(request, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_writer is not null)
            {
                await _writer.WriteAsync("__EXIT__\n");
                await _writer.FlushAsync();
            }
        }
        catch { }

        _writer?.Dispose();
        _reader?.Dispose();

        if (_process is { HasExited: false })
        {
            try
            {
                var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(exitCts.Token);
            }
            catch
            {
                try { _process.Kill(); } catch { }
            }
        }

        _process?.Dispose();
    }
}
