using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PythonEnvSharp;
using Xunit;

namespace PythonEnvSharp.Tests;

/// <summary>Runs against a local server and a small fake archive, so no network is needed.</summary>
public sealed class PythonInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"PythonEnvSharpInstaller_{Guid.NewGuid():N}");
    private readonly byte[] _archive = BuildArchive();

    public PythonInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Target => Path.Combine(_dir, "python-1.2.3");

    private string Interpreter => OperatingSystem.IsWindows()
        ? Path.Combine(Target, "python.exe")
        : Path.Combine(Target, "bin", "python3");

    private string Hash => Convert.ToHexString(SHA256.HashData(_archive)).ToLowerInvariant();

    private Task Install(string sha256, params string[] urls) => InstallWith(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), sha256, urls);

    private Task InstallWith(TimeSpan total, TimeSpan stall, string sha256, params string[] urls) => PythonInstaller.InstallAsync(
        new PythonDistribution(sha256, urls), Target, _dir, total, stall, null, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task SlowDownloadThatKeepsProgressing_IsNotInterrupted()
    {
        // Takes about 2.5 seconds in total, but no gap comes near the 1.5 second stall limit
        using var slow = new TestServer(_archive) { ChunkSize = 512, ChunkDelay = TimeSpan.FromMilliseconds(250) };

        await InstallWith(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1.5), Hash, slow.Url);

        Assert.True(File.Exists(Interpreter));
        Assert.Equal(1, slow.Requests);
    }

    [Fact]
    public async Task StalledSource_MovesOnAndResumesFromWhatArrived()
    {
        using var stalled = new TestServer(_archive) { StallAfterBytes = _archive.Length / 2 };
        using var good = new TestServer(_archive);

        await InstallWith(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), Hash, stalled.Url, good.Url);

        Assert.True(File.Exists(Interpreter));
        Assert.NotNull(good.LastRangeStart);
        Assert.True(good.LastRangeStart > 0);
    }

    [Fact]
    public async Task AllSourcesStalled_ReportsWhy()
    {
        using var stalled = new TestServer(_archive) { StallAfterBytes = 100 };

        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            InstallWith(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), Hash, stalled.Url));

        Assert.Contains("no data received", ex.Message);
    }

    [Fact]
    public async Task OptionalTotalLimit_IsStillHonored()
    {
        using var slow = new TestServer(_archive) { ChunkSize = 256, ChunkDelay = TimeSpan.FromMilliseconds(250) };

        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            InstallWith(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), Hash, slow.Url));

        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public void Defaults_HaveAStallLimitButNoTotalLimit()
    {
        var options = new PythonManagerOptions();
        Assert.Equal(TimeSpan.FromSeconds(60), options.DownloadStallTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, options.DownloadTimeout);
    }

    [Fact]
    public async Task ValidArchive_IsInstalled_AndLeavesNothingBehind()
    {
        using var server = new TestServer(_archive);
        await Install(Hash, server.Url);

        Assert.True(File.Exists(Interpreter));
        Assert.Empty(Directory.GetDirectories(_dir, ".install-*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, ".downloads")));
    }

    [Fact]
    public async Task WrongHash_IsRejected_AndNothingIsInstalled()
    {
        using var server = new TestServer(_archive);
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => Install(new string('a', 64), server.Url));

        Assert.Contains("SHA-256 mismatch", ex.Message);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task TamperedMirror_FallsThroughToAGoodOne()
    {
        var tampered = _archive.ToArray();
        tampered[^1] ^= 0xFF;
        using var bad = new TestServer(tampered);
        using var good = new TestServer(_archive);

        await Install(Hash, bad.Url, good.Url);

        Assert.True(File.Exists(Interpreter));
        Assert.Equal(1, good.Requests);
    }

    [Fact]
    public async Task FailingMirror_FallsThroughToAGoodOne()
    {
        using var failing = new TestServer(_archive) { Status = HttpStatusCode.NotFound };
        using var good = new TestServer(_archive);

        await Install(Hash, failing.Url, good.Url);

        Assert.True(File.Exists(Interpreter));
    }

    [Fact]
    public async Task AllMirrorsFailing_ReportsEachOne()
    {
        using var a = new TestServer(_archive) { Status = HttpStatusCode.NotFound };
        using var b = new TestServer(_archive) { Status = HttpStatusCode.InternalServerError };

        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => Install(Hash, a.Url, b.Url));

        Assert.Contains(a.Url, ex.Message);
        Assert.Contains(b.Url, ex.Message);
    }

    [Fact]
    public async Task PartialDownload_IsResumed()
    {
        var half = _archive.Length / 2;
        var downloads = Directory.CreateDirectory(Path.Combine(_dir, ".downloads"));
        File.WriteAllBytes(Path.Combine(downloads.FullName, $"{Hash}.partial"), _archive[..half]);

        using var server = new TestServer(_archive);
        await Install(Hash, server.Url);

        Assert.Equal(half, server.LastRangeStart);
        Assert.True(File.Exists(Interpreter));
    }

    [Fact]
    public async Task CompleteDownload_IsNotFetchedAgain()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_dir, ".downloads"));
        File.WriteAllBytes(Path.Combine(downloads.FullName, $"{Hash}.partial"), _archive);

        using var server = new TestServer(_archive);
        await Install(Hash, server.Url);

        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task ArchiveWithoutInterpreter_IsRejected_AndNothingIsInstalled()
    {
        var empty = BuildArchive("python/readme.txt");
        using var server = new TestServer(empty);
        var hash = Convert.ToHexString(SHA256.HashData(empty)).ToLowerInvariant();

        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => Install(hash, server.Url));

        Assert.Contains("no interpreter", ex.Message);
        Assert.False(Directory.Exists(Target));
    }

    [Theory]
    [InlineData("python/../../outside.txt")]
    [InlineData("../outside.txt")]
    public async Task EntryOutsideTheFolder_IsRejected(string entry)
    {
        var malicious = BuildArchive(entry);
        using var server = new TestServer(malicious);
        var hash = Convert.ToHexString(SHA256.HashData(malicious)).ToLowerInvariant();

        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => Install(hash, server.Url));

        Assert.Contains("outside", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
    }

    [Fact]
    public async Task StaleBytesAfterTheEndOfAName_AreIgnored()
    {
        // The real python-build-standalone 3.11.9 archive has names like "python.exe\0hon.exe"
        var archivePath = Path.Combine(_dir, "stale.tar.gz");
        File.WriteAllBytes(archivePath, BuildArchive(OperatingSystem.IsWindows() ? "python/python.exe" : "python/bin/python3", staleNameBytes: "hon.exe"));
        var output = Path.Combine(_dir, "stale-output");
        Directory.CreateDirectory(output);

        await PythonInstaller.ExtractAsync(archivePath, output, CancellationToken.None);

        var extracted = Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.Equal(OperatingSystem.IsWindows() ? "python.exe" : "python3", Path.GetFileName(extracted));
    }

    [Fact]
    public void Catalog_DefaultVersion_ResolvesWithAPinnedHash()
    {
        var distribution = PythonCatalog.Resolve(PythonManagerOptions.DefaultPythonVersion);
        Assert.Equal(64, distribution.Sha256.Length);
        Assert.All(distribution.Urls, url => Assert.StartsWith("https://", url));
        Assert.Contains("npmmirror", distribution.Urls[0]);
        Assert.Contains("github.com", distribution.Urls[1]);
    }

    private static byte[] BuildArchive(string? entry = null, string? staleNameBytes = null)
    {
        entry ??= OperatingSystem.IsWindows() ? "python/python.exe" : "python/bin/python3";
        var name = staleNameBytes is null ? entry : $"{entry}\0{staleNameBytes}";

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            // Padding makes the archive long enough to split in half for the resume test.
            var data = RandomNumberGenerator.GetBytes(4096);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        }
        return output.ToArray();
    }

    /// <summary>A minimal HTTP server that understands "Range: bytes=N-".</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _content;
        private int _requests;

        public TestServer(byte[] content)
        {
            _content = content;
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public int ChunkSize { get; init; } = int.MaxValue;

        public TimeSpan ChunkDelay { get; init; } = TimeSpan.Zero;

        /// <summary>Sends this many bytes of the body, then goes silent with the connection open.</summary>
        public int? StallAfterBytes { get; init; }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/python.tar.gz";

        public int Requests => _requests;

        public long? LastRangeStart { get; private set; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    await HandleAsync(client);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            Interlocked.Increment(ref _requests);
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            long start = 0;
            while (await reader.ReadLineAsync() is { Length: > 0 } header)
            {
                if (header.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    start = long.Parse(header["Range: bytes=".Length..].TrimEnd('-'));
                    LastRangeStart = start;
                }
            }

            string head;
            byte[] body;
            if (Status != HttpStatusCode.OK)
            {
                head = $"HTTP/1.1 {(int)Status} Error\r\nContent-Length: 0\r\n";
                body = Array.Empty<byte>();
            }
            else if (start > 0)
            {
                body = _content[(int)start..];
                head = $"HTTP/1.1 206 Partial Content\r\nContent-Length: {body.Length}\r\nContent-Range: bytes {start}-{_content.Length - 1}/{_content.Length}\r\n";
            }
            else
            {
                body = _content;
                head = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n";
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head + "Connection: close\r\n\r\n"));
            await stream.FlushAsync();

            var length = StallAfterBytes is { } limit ? Math.Min(limit, body.Length) : body.Length;
            for (var sent = 0; sent < length;)
            {
                var size = Math.Min(ChunkSize, length - sent);
                await stream.WriteAsync(body.AsMemory(sent, size));
                await stream.FlushAsync();
                sent += size;
                if (ChunkDelay > TimeSpan.Zero) await Task.Delay(ChunkDelay);
            }

            if (StallAfterBytes is not null) await Task.Delay(TimeSpan.FromSeconds(30));
        }

        public void Dispose() => _listener.Stop();
    }
}
