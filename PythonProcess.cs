using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PythonEnvSharp;

/// <summary>
/// Bidirectional communication with a Python process via stdin/stdout.
/// The script must print "READY" on a line of its own once it can accept messages.
/// Stdout is the message channel, so scripts should send their own output to stderr.
/// Use <see cref="PythonPipe"/> when libraries the script uses might print to stdout.
/// </summary>
public sealed class PythonProcess : IAsyncDisposable
{
    private readonly ILogger _logger;
    private PythonChild? _child;
    private LineChannel? _channel;
    private bool _disposed;

    public PythonProcess(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// How long <see cref="StartAsync"/> waits for "READY". By default there's no limit, because a script may
    /// load large models first. A script that exits is still reported at once, and the token can always cancel.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>Arguments passed to the script.</summary>
    public IList<string> Arguments { get; } = new List<string>();

    /// <summary>Working directory for the script. Default: the current directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Extra environment variables for the script.</summary>
    public IDictionary<string, string?> Environment { get; } = new Dictionary<string, string?>();

    public bool IsRunning => !_disposed && _child is { HasExited: false };

    /// <summary>The most recent lines the script wrote to stderr.</summary>
    public string StandardErrorTail => _child?.StderrTail ?? "";

    /// <summary>
    /// Starts the script and waits for it to print "READY". Throws <see cref="PythonEnvException"/>,
    /// including the script's stderr, if it exits first or misses <see cref="StartupTimeout"/>.
    /// </summary>
    public async Task StartAsync(string pythonExe, string scriptPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child is not null) throw new InvalidOperationException("Already started.");
        ct.ThrowIfCancellationRequested();

        var psi = ProcessRunner.CreateStartInfo(pythonExe, new[] { scriptPath }.Concat(Arguments), WorkingDirectory);
        psi.RedirectStandardInput = true;
        foreach (var (name, value) in Environment) psi.Environment[name] = value;

        var child = new PythonChild(psi, drainStdout: false, _logger);
        var channel = new LineChannel(child.Process.StandardInput.BaseStream, child.Process.StandardOutput.BaseStream, child.Describe);

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(StartupTimeout);
        try
        {
            while (await channel.ReceiveAsync(startup.Token) != "READY") { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var description = child.Describe();
            ProcessRunner.TryKillTree(child.Process);
            await child.ShutdownAsync();
            throw new PythonEnvException($"Python didn't print READY within {StartupTimeout}: {description}");
        }
        catch
        {
            ProcessRunner.TryKillTree(child.Process);
            await child.ShutdownAsync();
            throw;
        }

        _child = child;
        _channel = channel;
    }

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
            return _channel ?? throw new InvalidOperationException("Not started");
        }
    }

    /// <summary>
    /// Asks the script to exit by sending "__EXIT__" and closing stdin. Kills it if it's still running five seconds later.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_child is null) return;

        if (_channel is not null)
        {
            try
            {
                using var send = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _channel.SendAsync("__EXIT__", send.Token);
            }
            catch (Exception ex) when (ex is PythonEnvException or OperationCanceledException) { } // already gone, or not reading
        }

        try { _child.Process.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }

        await _child.ShutdownAsync();
    }
}
