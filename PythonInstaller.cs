using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace PythonEnvSharp;

/// <summary>
/// Downloads, verifies and unpacks a Python archive. The final folder appears through a single
/// rename, so an interrupted install never looks finished. Nothing outside it is touched: no
/// installer runs and nothing is registered with Windows. PythonManager handles PATH separately.
/// </summary>
internal static class PythonInstaller
{
    public static async Task InstallAsync(
        PythonDistribution distribution,
        string targetDir,
        string workRoot,
        TimeSpan downloadTimeout,
        TimeSpan stallTimeout,
        IProgress<PythonDownloadProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        var archive = await DownloadVerifiedAsync(distribution, workRoot, downloadTimeout, stallTimeout, progress, logger, ct);

        logger.LogInformation("Unpacking Python to {Target}", targetDir);
        var unpackDir = Path.Combine(workRoot, $".install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(unpackDir);
        try
        {
            await ExtractAsync(archive, unpackDir, ct);

            var unpackedHome = Path.Combine(unpackDir, "python");
            if (!File.Exists(PythonLayout.PythonExecutableIn(unpackedHome)))
                throw new PythonEnvException("The Python archive has no interpreter under its top-level \"python\" folder.");

            // Callers only get here when the target has no interpreter, so what's left is a failed install.
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            await MoveWithRetryAsync(unpackedHome, targetDir, ct);
        }
        finally
        {
            try { Directory.Delete(unpackDir, recursive: true); }
            catch (IOException ex) { logger.LogWarning("Couldn't remove {Dir}: {Reason}", unpackDir, ex.Message); }
            catch (UnauthorizedAccessException ex) { logger.LogWarning("Couldn't remove {Dir}: {Reason}", unpackDir, ex.Message); }
        }

        File.Delete(archive);
    }

    /// <summary>
    /// Extracts entry by entry instead of using TarFile.ExtractToDirectoryAsync. Some
    /// python-build-standalone archives have stale bytes after the NUL that ends a name, which
    /// TarFile keeps, turning "python.exe" into "python.exe_hon.exe".
    /// </summary>
    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);

        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            var path = ResolveInside(root, CleanName(entry.Name), entry.Name);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;

                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var output = File.Create(path))
                    {
                        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, ct);
                    }
                    // Compiled .pyc files record their source's timestamp, so keeping it keeps them valid.
                    File.SetLastWriteTimeUtc(path, entry.ModificationTime.UtcDateTime);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode);
                    break;

                case TarEntryType.SymbolicLink:
                    var linkTarget = CleanName(entry.LinkName);
                    ResolveInside(root, Path.Combine(Path.GetDirectoryName(CleanName(entry.Name)) ?? "", linkTarget), entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.CreateSymbolicLink(path, linkTarget);
                    break;

                case TarEntryType.HardLink:
                    var source = ResolveInside(root, CleanName(entry.LinkName), entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(source, path);
                    break;

                // Metadata-only entries, and types a Python install has no use for.
                default:
                    break;
            }
        }
    }

    private static string CleanName(string name)
    {
        var end = name.IndexOf('\0');
        return end < 0 ? name : name[..end];
    }

    /// <summary>Rejects any entry that would land outside the destination.</summary>
    private static string ResolveInside(string root, string relative, string entryName)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PythonEnvException($"The Python archive has an entry that points outside its folder: {entryName}");
        return path;
    }

    private static async Task<string> DownloadVerifiedAsync(
        PythonDistribution distribution,
        string workRoot,
        TimeSpan timeout,
        TimeSpan stallTimeout,
        IProgress<PythonDownloadProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        var expected = distribution.Sha256.Trim().ToLowerInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new PythonEnvException("PythonDistribution.Sha256 must be a 64-character hex SHA-256.");
        if (distribution.Urls.Count == 0)
            throw new PythonEnvException("PythonDistribution.Urls is empty.");

        // Named by hash, so a partial file from any mirror or earlier run can be resumed anywhere.
        var downloads = Path.Combine(workRoot, ".downloads");
        Directory.CreateDirectory(downloads);
        var partial = Path.Combine(downloads, $"{expected}.partial");

        if (File.Exists(partial) && await HashAsync(partial, ct) == expected) return partial;

        using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            // Time limits are applied per attempt and per read. HttpClient.Timeout wouldn't cover the response body.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var errors = new List<string>();
        foreach (var url in distribution.Urls)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(timeout);
            try
            {
                logger.LogInformation("Downloading Python from {Url}", url);
                await DownloadAsync(client, url, partial, stallTimeout, progress, logger, attempt.Token);

                var actual = await HashAsync(partial, ct);
                if (actual == expected) return partial;

                File.Delete(partial);
                throw new InvalidDataException($"SHA-256 mismatch: expected {expected}, got {actual}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var reason = attempt.IsCancellationRequested ? $"timed out after {timeout}" : ex.Message;
                logger.LogWarning("Download from {Url} failed: {Reason}", url, reason);
                errors.Add($"{url}: {reason}");
            }
        }

        throw new PythonEnvException("Failed to download Python: " + string.Join("; ", errors));
    }

    /// <summary>
    /// Appends to the file at <paramref name="path"/>. Throws <see cref="TimeoutException"/> if no data
    /// arrives for <paramref name="stallTimeout"/>, and what arrived so far stays there to resume from.
    /// </summary>
    internal static async Task DownloadAsync(
        HttpClient client,
        string url,
        string path,
        TimeSpan stallTimeout,
        IProgress<PythonDownloadProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await DownloadCoreAsync(client, url, path, stallTimeout, stall, progress, logger, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no data received for {stallTimeout}");
        }
    }

    private static async Task DownloadCoreAsync(
        HttpClient client,
        string url,
        string path,
        TimeSpan stallTimeout,
        CancellationTokenSource stall,
        IProgress<PythonDownloadProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        stall.CancelAfter(stallTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

        // The partial file can't be continued (for example it's already too long): start over.
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(path);
            await DownloadCoreAsync(client, url, path, stallTimeout, stall, progress, logger, ct);
            return;
        }
        response.EnsureSuccessStatusCode();

        var resuming = response.StatusCode == HttpStatusCode.PartialContent;
        if (resuming && response.Content.Headers.ContentRange?.From != existing)
            throw new InvalidDataException("Server returned a different range than requested.");
        if (resuming) logger.LogInformation("Resuming download at {Bytes} bytes", existing);

        var downloaded = resuming ? existing : 0;
        var total = response.Content.Headers.ContentLength + downloaded;
        var nextLogAt = 10;

        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var target = new FileStream(path, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        while (true)
        {
            stall.CancelAfter(stallTimeout);
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            progress?.Report(new PythonDownloadProgress(downloaded, total, url));

            if (total > 0 && downloaded * 100 / total >= nextLogAt)
            {
                logger.LogInformation("Downloaded {Percent}% of {Megabytes} MB", downloaded * 100 / total, total / 1024 / 1024);
                nextLogAt = (int)(downloaded * 100 / total / 10 + 1) * 10;
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken ct)
    {
        // Antivirus scanners often hold freshly unpacked files open for a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
