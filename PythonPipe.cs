using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PythonEnvSharp;

/// <summary>
/// Bidirectional communication with a Python process via named pipes. Stdout stays free for the
/// script's own output, so print() can't disturb messages. The script receives the pipe names as
/// "--pipe-in NAME" and "--pipe-out NAME". On Windows it opens them with open(r"\\.\pipe\NAME");
/// on Linux and macOS it connects a Unix socket to "$TMPDIR/CoreFxPipe_NAME" (or /tmp when TMPDIR is unset). No extra packages are needed.
/// Only the current user can connect.
/// </summary>
public sealed class PythonPipe : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private NamedPipeServerStream? _serverIn;
    private NamedPipeServerStream? _serverOut;
    private PythonChild? _child;
    private LineChannel? _channel;
    private bool _disposed;

    public bool IsConnected => !_disposed && _channel is not null && _serverIn?.IsConnected == true && _serverOut?.IsConnected == true;

    public PythonPipe(string? pipeName = null, ILogger? logger = null)
    {
        _pipeName = pipeName ?? $"python_pipe_{Guid.NewGuid():N}";
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>How long <see cref="StartAsync"/> waits for Python to connect. Default: 30 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The most recent lines the script wrote to stderr.</summary>
    public string StandardErrorTail => _child?.StderrTail ?? "";

    /// <summary>
    /// Start a Python process and connect via named pipes. Throws <see cref="PythonEnvException"/>,
    /// including the script's stderr, if it exits first or doesn't connect in time.
    /// </summary>
    public async Task StartAsync(string pythonExe, string scriptPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child is not null) throw new InvalidOperationException("Already started.");
        ct.ThrowIfCancellationRequested();

        var serverIn = CreateServer($"{_pipeName}_in");
        var serverOut = CreateServer($"{_pipeName}_out");
        PythonChild? child = null;

        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var psi = ProcessRunner.CreateStartInfo(pythonExe, new[]
            {
                scriptPath, "--pipe-in", $"{_pipeName}_in", "--pipe-out", $"{_pipeName}_out",
            }, workingDirectory: null);

            child = new PythonChild(psi, drainStdout: true, _logger);

            var connected = Task.WhenAll(
                serverIn.WaitForConnectionAsync(connect.Token),
                serverOut.WaitForConnectionAsync(connect.Token));
            var exited = child.Process.WaitForExitAsync(connect.Token);
            var timeout = Task.Delay(ConnectTimeout, connect.Token);

            var first = await Task.WhenAny(connected, exited, timeout);
            ct.ThrowIfCancellationRequested();

            if (first == exited)
                throw new PythonEnvException($"Python exited before connecting to the pipes: {child.Describe()}");
            if (first == timeout)
                throw new PythonEnvException($"Python did not connect to the pipes within {ConnectTimeout}: {child.Describe()}");
            await connected;

            _serverIn = serverIn;
            _serverOut = serverOut;
            _child = child;
            _channel = new LineChannel(serverIn, serverOut, child.Describe);
        }
        catch
        {
            connect.Cancel();
            if (child is not null)
            {
                ProcessRunner.TryKillTree(child.Process);
                await child.ShutdownAsync();
            }
            await serverIn.DisposeAsync();
            await serverOut.DisposeAsync();
            throw;
        }
    }

    private static NamedPipeServerStream CreateServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// Send a message to Python. The message can't contain line breaks.
    /// </summary>
    public Task SendAsync(string message, CancellationToken ct = default) => Channel.SendAsync(message, ct);

    /// <summary>
    /// Send a message and wait for response. Safe to call concurrently.
    /// </summary>
    public Task<string> SendReceiveAsync(string message, CancellationToken ct = default) =>
        Channel.ExchangeAsync(message, accept: null, ct);

    /// <summary>
    /// Receive a message from Python.
    /// </summary>
    public Task<string> ReceiveAsync(CancellationToken ct = default) => Channel.ReceiveAsync(ct);

    /// <summary>
    /// Send JSON-RPC style request {"id":n,"method":...,"params":...} and get response.
    /// Output that isn't a JSON object is skipped. If the script echoes "id", replies to other requests are skipped too.
    /// </summary>
    public Task<string> JsonRpcAsync(string method, string jsonParams, CancellationToken ct = default) =>
        Channel.JsonRpcAsync(method, jsonParams, ct);

    private LineChannel Channel
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _channel ?? throw new InvalidOperationException("Not connected");
        }
    }

    /// <summary>
    /// Asks the script to exit by sending "__EXIT__", then closes the pipes. Kills it if it's still running five seconds later.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_channel is not null)
        {
            try
            {
                using var send = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _channel.SendAsync("__EXIT__", send.Token);
            }
            catch (Exception ex) when (ex is PythonEnvException or OperationCanceledException) { } // already gone, or not reading
        }

        // Python may still be flushing a final reply, and the pipes must outlive that.
        if (_child is not null) await _child.ShutdownAsync();

        if (_serverIn is not null) await _serverIn.DisposeAsync();
        if (_serverOut is not null) await _serverOut.DisposeAsync();
    }
}
