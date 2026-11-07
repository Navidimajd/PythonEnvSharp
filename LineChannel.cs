using System.Text;
using System.Text.Json;

namespace PythonEnvSharp;

/// <summary>
/// Line-based messaging shared by <see cref="PythonProcess"/> and <see cref="PythonPipe"/>.
/// Waits are cancellable even on streams that can't cancel a read: the read stays pending and the
/// next call picks it up, so no message is lost. Request/reply pairs can't interleave.
/// </summary>
internal sealed class LineChannel
{
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly Func<string> _describeFailure;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private Task? _pendingWrite;
    private Task<string?>? _pendingRead;
    private int _abandonedReplies;
    private long _nextRequestId;

    public LineChannel(Stream output, Stream input, Func<string> describeFailure)
    {
        var utf8NoBom = new UTF8Encoding(false);
        _writer = new StreamWriter(output, utf8NoBom, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(input, utf8NoBom, leaveOpen: true);
        _describeFailure = describeFailure;
    }

    public async Task SendAsync(string message, CancellationToken ct)
    {
        if (message.Contains('\n') || message.Contains('\r'))
            throw new ArgumentException("A message can't contain line breaks, because each line is one message.", nameof(message));

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_pendingWrite is not null)
            {
                await _pendingWrite.WaitAsync(ct);
                _pendingWrite = null;
            }

            _pendingWrite = _writer.WriteAsync(message + "\n");
            await _pendingWrite.WaitAsync(ct);
            _pendingWrite = null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pendingWrite = null;
            throw new PythonEnvException($"Couldn't send to Python: {_describeFailure()}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string> ReceiveAsync(CancellationToken ct)
    {
        await _readLock.WaitAsync(ct);
        try
        {
            _pendingRead ??= _reader.ReadLineAsync();
            var line = await _pendingRead.WaitAsync(ct);
            _pendingRead = null;
            return line ?? throw new PythonEnvException($"Python process disconnected: {_describeFailure()}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pendingRead = null;
            throw new PythonEnvException($"Python process disconnected: {_describeFailure()}", ex);
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// Sends a request and returns its reply. If an earlier exchange was cancelled, its late reply
    /// is discarded first, so it can't be mistaken for this one.
    /// </summary>
    /// <param name="message">The request line.</param>
    /// <param name="accept">Returns false for lines that aren't the reply, which are skipped.</param>
    /// <param name="ct">Cancels the wait.</param>
    public async Task<string> ExchangeAsync(string message, Func<string, bool>? accept, CancellationToken ct)
    {
        await _exchangeLock.WaitAsync(ct);
        try
        {
            while (_abandonedReplies > 0)
            {
                await ReceiveAsync(ct);
                _abandonedReplies--;
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                await SendAsync(message, ct);
                while (true)
                {
                    var line = await ReceiveAsync(ct);
                    if (accept is null || accept(line)) return line;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _abandonedReplies++;
                throw;
            }
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    /// <summary>
    /// Sends {"id":n,"method":...,"params":...} and returns the matching reply. Lines that aren't JSON
    /// objects, such as stray print() output, are skipped, as is any reply carrying another request's id.
    /// </summary>
    public Task<string> JsonRpcAsync(string method, string jsonParams, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(method);

        JsonElement parameters;
        try
        {
            using var document = JsonDocument.Parse(jsonParams);
            parameters = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"jsonParams isn't valid JSON: {ex.Message}", nameof(jsonParams), ex);
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var request = JsonSerializer.Serialize(new { id, method, @params = parameters });
        return ExchangeAsync(request, line => IsReplyTo(line, id), ct);
    }

    private static bool IsReplyTo(string line, long id)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            return !document.RootElement.TryGetProperty("id", out var replyId)
                || (replyId.ValueKind == JsonValueKind.Number && replyId.TryGetInt64(out var value) && value == id);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
