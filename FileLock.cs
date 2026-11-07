using Microsoft.Extensions.Logging;

namespace PythonEnvSharp;

/// <summary>
/// Cross-process lock held through an exclusively opened file. Unlike a named Mutex it has no
/// thread affinity, so it's safe to hold across awaits. The OS releases it if the process dies.
/// </summary>
internal sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static async Task<FileLock> AcquireAsync(string path, ILogger logger, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var logged = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException ex)
            {
                if (!logged)
                {
                    logger.LogInformation("Waiting for lock {Path}: {Reason}", path, ex.Message);
                    logged = true;
                }
                await Task.Delay(250, ct);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
