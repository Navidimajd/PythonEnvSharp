using PythonEnvSharp;
using Xunit;

namespace PythonEnvSharp.Tests;

[Collection("Python")]
public sealed class PythonPipeTests
{
    private const string Connect = """
        import os, socket, sys

        def connect(name, mode):
            if os.name == "nt":
                return open(r"\\.\pipe" + "\\" + name, mode, encoding="utf-8", newline="\n")
            client = socket.socket(socket.AF_UNIX)
            client.connect(os.path.join(os.environ.get("TMPDIR", "/tmp"), "CoreFxPipe_" + name))
            return client.makefile(mode, encoding="utf-8", newline="\n")

        args = sys.argv
        requests = connect(args[args.index("--pipe-in") + 1], "r")
        replies = connect(args[args.index("--pipe-out") + 1], "w")

        """;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly PythonFixture _fixture;

    public PythonPipeTests(PythonFixture fixture) => _fixture = fixture;

    private string EchoScript(string onExit = "pass") => _fixture.WriteScript(Connect + $$"""
        for line in requests:
            line = line.rstrip("\n")
            if line == "__EXIT__":
                {{onExit}}
                break
            print("stdout noise", flush=True)
            replies.write(line.upper() + "\n")
            replies.flush()
        """);

    [Fact]
    public async Task RoundTrip_WorksWithoutExtraPackages_AndPrintIsHarmless()
    {
        await using var pipe = new PythonPipe();
        await pipe.StartAsync(_fixture.Python, EchoScript()).WaitAsync(Limit);
        Assert.True(pipe.IsConnected);
        Assert.Equal("HELLO", await pipe.SendReceiveAsync("hello").WaitAsync(Limit));
        Assert.Equal("É 中", await pipe.SendReceiveAsync("é 中").WaitAsync(Limit));
    }

    [Fact]
    public async Task Dispose_LetsPythonExitCleanly()
    {
        var marker = _fixture.NewPath("marker.txt").Replace('\\', '/');
        var pipe = new PythonPipe();
        await pipe.StartAsync(_fixture.Python, EchoScript($"open(\"{marker}\", \"w\").write(\"clean\")")).WaitAsync(Limit);

        await pipe.DisposeAsync().AsTask().WaitAsync(Limit);

        Assert.Equal("clean", File.ReadAllText(marker));
    }

    [Fact]
    public async Task Start_PythonDiesAtOnce_FailsFastWithStderr()
    {
        await using var pipe = new PythonPipe();
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            pipe.StartAsync(_fixture.Python, _fixture.WriteScript("raise RuntimeError('boom')")).WaitAsync(Limit));

        Assert.Contains("boom", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Start_NeverConnects_TimesOut()
    {
        await using var pipe = new PythonPipe { ConnectTimeout = TimeSpan.FromSeconds(1) };
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            pipe.StartAsync(_fixture.Python, _fixture.WriteScript("import time\ntime.sleep(600)")).WaitAsync(Limit));
        Assert.Contains("did not connect", ex.Message);
    }

    [Fact]
    public async Task Start_Cancelled_ThrowsCancellationNotTimeout()
    {
        await using var pipe = new PythonPipe();
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipe.StartAsync(_fixture.Python, _fixture.WriteScript("import time\ntime.sleep(600)"), cts.Token).WaitAsync(Limit));
    }

    [Fact]
    public async Task IsConnected_AfterDispose_IsFalse()
    {
        var pipe = new PythonPipe();
        await pipe.StartAsync(_fixture.Python, EchoScript()).WaitAsync(Limit);
        await pipe.DisposeAsync();
        Assert.False(pipe.IsConnected);
    }
}
