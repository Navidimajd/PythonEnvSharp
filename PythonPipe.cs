using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace PythonEnvSharp;

/// <summary>
/// Bidirectional communication with a Python process via named pipes.
/// </summary>
public sealed class PythonPipe : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _serverIn;
    private NamedPipeServerStream? _serverOut;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private Process? _pythonProcess;
    private bool _disposed;

    public bool IsConnected => _serverIn?.IsConnected == true && _serverOut?.IsConnected == true;

    public PythonPipe(string? pipeName = null)
    {
        _pipeName = pipeName ?? $"python_pipe_{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Start a Python process and connect via named pipes.
    /// </summary>
    public async Task StartAsync(string pythonExe, string scriptPath, CancellationToken ct = default)
    {
        // Create inbound pipe (C# → Python)
        _serverIn = new NamedPipeServerStream(
            $"{_pipeName}_in", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Create outbound pipe (Python → C#)
        _serverOut = new NamedPipeServerStream(
            $"{_pipeName}_out", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Start Python process
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" --pipe-in \"{_pipeName}_in\" --pipe-out \"{_pipeName}_out\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _pythonProcess = Process.Start(psi)
            ?? throw new PythonEnvException("Failed to start Python process");

        // Wait for connections
        var connectTask = Task.WhenAll(
            _serverIn.WaitForConnectionAsync(cancellationToken: ct),
            _serverOut.WaitForConnectionAsync(cancellationToken: ct));

        var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        if (await Task.WhenAny(connectTask, timeout) == timeout)
        {
            throw new PythonEnvException("Python process did not connect to pipes within 30s");
        }

        var utf8NoBom = new UTF8Encoding(false);
        _writer = new StreamWriter(_serverIn, utf8NoBom, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_serverOut, utf8NoBom, leaveOpen: true);
    }

    /// <summary>
    /// Send a message to Python.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PythonPipe));
        if (_writer is null) throw new InvalidOperationException("Not connected");

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
        if (_disposed) throw new ObjectDisposedException(nameof(PythonPipe));
        if (_reader is null) throw new InvalidOperationException("Not connected");

        var line = await _reader.ReadLineAsync();
        return line ?? throw new PythonEnvException("Python process disconnected");
    }

    /// <summary>
    /// Send JSON and receive JSON response.
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

        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _serverIn?.Dispose(); } catch { }
        try { _serverOut?.Dispose(); } catch { }

        if (_pythonProcess is { HasExited: false })
        {
            try
            {
                // Send exit command
                if (_writer is not null)
                {
                    await _writer.WriteAsync("__EXIT__\n");
                    await _writer.FlushAsync();
                    var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _pythonProcess.WaitForExitAsync(exitCts.Token);
                }
            }
            catch { }

            try { _pythonProcess.Kill(); } catch { }
        }

        _pythonProcess?.Dispose();
    }
}
