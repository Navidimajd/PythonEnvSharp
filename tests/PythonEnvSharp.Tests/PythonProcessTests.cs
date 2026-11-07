using System.Text.Json;
using PythonEnvSharp;
using Xunit;

namespace PythonEnvSharp.Tests;

[Collection("Python")]
public sealed class PythonProcessTests
{
    private const string Echo = """
        import sys
        print("READY", flush=True)
        for line in sys.stdin:
            line = line.rstrip("\n")
            if line == "__EXIT__":
                break
            print(line, flush=True)
        """;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly PythonFixture _fixture;

    public PythonProcessTests(PythonFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Start_MissingScript_ThrowsWithPythonsError()
    {
        await using var process = new PythonProcess();
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            process.StartAsync(_fixture.Python, _fixture.NewPath("missing.py")).WaitAsync(Limit));
        Assert.Contains("can't open file", ex.Message);
    }

    [Fact]
    public async Task Start_ScriptCrashes_ThrowsWithTraceback()
    {
        await using var process = new PythonProcess();
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            process.StartAsync(_fixture.Python, _fixture.WriteScript("raise RuntimeError('boom')")).WaitAsync(Limit));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Start_NeverReady_TimesOut()
    {
        await using var process = new PythonProcess { StartupTimeout = TimeSpan.FromSeconds(1) };
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            process.StartAsync(_fixture.Python, _fixture.WriteScript("import time\ntime.sleep(600)")).WaitAsync(Limit));
        Assert.Contains("READY", ex.Message);
    }

    [Fact]
    public async Task Start_SlowScript_HasNoDefaultLimit()
    {
        await using var process = new PythonProcess();
        Assert.Equal(Timeout.InfiniteTimeSpan, process.StartupTimeout);

        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""
            import sys, time
            time.sleep(2)
            print("READY", flush=True)
            sys.stdin.read()
            """)).WaitAsync(Limit);
        Assert.True(process.IsRunning);
    }

    [Fact]
    public async Task Start_Cancelled_ThrowsCancellation()
    {
        await using var process = new PythonProcess();
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            process.StartAsync(_fixture.Python, _fixture.WriteScript("import time\ntime.sleep(600)"), cts.Token).WaitAsync(Limit));
    }

    [Fact]
    public async Task Start_DoesNotBlockTheCaller()
    {
        await using var process = new PythonProcess { StartupTimeout = TimeSpan.FromSeconds(2) };
        var started = DateTime.UtcNow;
        var task = process.StartAsync(_fixture.Python, _fixture.WriteScript("import time\ntime.sleep(600)"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAsync<PythonEnvException>(() => task.WaitAsync(Limit));
    }

    [Fact]
    public async Task LargeStderr_DoesNotDeadlock()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""
            import sys
            print("READY", flush=True)
            for line in sys.stdin:
                if line.strip() == "__EXIT__":
                    break
                sys.stderr.write("x" * 1048576 + "\n")
                sys.stderr.write("last stderr line\n")
                sys.stderr.flush()
                print("reply", flush=True)
            """));
        Assert.Equal("reply", await process.SendReceiveAsync("go").WaitAsync(Limit));
    }

    [Fact]
    public async Task NonAscii_RoundTrips()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));
        const string text = "é 中 سلام 🐍";
        Assert.Equal(text, await process.SendReceiveAsync(text).WaitAsync(Limit));
    }

    [Fact]
    public async Task Receive_HonorsToken_AndKeepsTheLaterMessage()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));

        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReceiveAsync(cts.Token).WaitAsync(Limit));

        await process.SendAsync("later");
        Assert.Equal("later", await process.ReceiveAsync().WaitAsync(Limit));
    }

    [Fact]
    public async Task CancelledExchange_LateReplyIsNotGivenToTheNextOne()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""
            import sys, time
            print("READY", flush=True)
            for line in sys.stdin:
                line = line.strip()
                if line == "__EXIT__":
                    break
                if line == "slow":
                    time.sleep(1)
                print(line, flush=True)
            """));

        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.SendReceiveAsync("slow", cts.Token).WaitAsync(Limit));
        Assert.Equal("fast", await process.SendReceiveAsync("fast").WaitAsync(Limit));
    }

    [Fact]
    public async Task IsRunning_AfterDispose_IsFalse()
    {
        var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));
        Assert.True(process.IsRunning);
        await process.DisposeAsync();
        Assert.False(process.IsRunning);
    }

    [Fact]
    public async Task ConcurrentExchanges_EachGetTheirOwnReply()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));

        var replies = await Task.WhenAll(Enumerable.Range(0, 200).Select(async i =>
            (Sent: $"m{i}", Got: await process.SendReceiveAsync($"m{i}")))).WaitAsync(Limit);

        Assert.All(replies, r => Assert.Equal(r.Sent, r.Got));
    }

    [Fact]
    public async Task Send_RejectsLineBreaks()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));
        await Assert.ThrowsAsync<ArgumentException>(() => process.SendAsync("two\nlines"));
    }

    [Fact]
    public async Task Send_AfterPythonExits_ThrowsPythonEnvException()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""print("READY", flush=True)"""));
        await Assert.ThrowsAsync<PythonEnvException>(async () =>
        {
            await process.SendAsync("a");
            await process.ReceiveAsync();
        }).WaitAsync(Limit);
    }

    [Fact]
    public async Task JsonRpc_EscapesMethod_AndCompactsMultilineParams()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));

        const string method = "a\",\"admin\":true,\"x\":\"";
        var reply = await process.JsonRpcAsync(method, "{\n  \"n\": 1\n}").WaitAsync(Limit);

        using var document = JsonDocument.Parse(reply);
        Assert.Equal(method, document.RootElement.GetProperty("method").GetString());
        Assert.False(document.RootElement.TryGetProperty("admin", out _));
        Assert.Equal(1, document.RootElement.GetProperty("params").GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task JsonRpc_SkipsStrayOutput()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""
            import json, sys
            print("READY", flush=True)
            for line in sys.stdin:
                if line.strip() == "__EXIT__":
                    break
                print("library noise", flush=True)
                print(json.dumps({"id": json.loads(line)["id"], "ok": True}), flush=True)
            """));

        var first = await process.JsonRpcAsync("a", "{}").WaitAsync(Limit);
        var second = await process.JsonRpcAsync("b", "{}").WaitAsync(Limit);
        Assert.Contains("\"id\": 1", first);
        Assert.Contains("\"id\": 2", second);
    }

    [Fact]
    public async Task JsonRpc_RejectsInvalidParams()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));
        await Assert.ThrowsAsync<ArgumentException>(() => process.JsonRpcAsync("a", "not json"));
    }

    [Fact]
    public async Task Dispose_AfterExplicitExit_IsClean()
    {
        var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript(Echo));
        await process.SendAsync("__EXIT__");
        await process.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task ArgumentsWithSpacesAndQuotes_ArriveIntact()
    {
        await using var process = new PythonProcess();
        process.Arguments.Add("a b\"c");
        process.Environment["MY_TEST_VALUE"] = "from-csharp";
        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""
            import os, sys
            print("READY", flush=True)
            print(sys.argv[1] + "|" + os.environ["MY_TEST_VALUE"], flush=True)
            sys.stdin.read()
            """));
        Assert.Equal("a b\"c|from-csharp", await process.ReceiveAsync().WaitAsync(Limit));
    }
}
