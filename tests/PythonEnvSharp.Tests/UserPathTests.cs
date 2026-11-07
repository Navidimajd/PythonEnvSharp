using Microsoft.Win32;
using PythonEnvSharp;
using Xunit;

namespace PythonEnvSharp.Tests;

/// <summary>
/// Registry tests use a throwaway key, so the real user PATH is never read or changed.
/// </summary>
[Collection("Python")]
public sealed class UserPathTests : IDisposable
{
    private readonly PythonFixture _fixture;
    private readonly string _key = $@"Software\PythonEnvSharpTests\{Guid.NewGuid():N}";
    private readonly string? _processPath = Environment.GetEnvironmentVariable("PATH");

    public UserPathTests(PythonFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _processPath);
        if (OperatingSystem.IsWindows()) Registry.CurrentUser.DeleteSubKeyTree(@"Software\PythonEnvSharpTests", throwOnMissingSubKey: false);
    }

    private static string Home => OperatingSystem.IsWindows() ? @"C:\py\home" : "/py/home";

    private static string First => OperatingSystem.IsWindows() ? @"C:\py\home\Scripts" : "/py/home/bin";

    private static string Sep => Path.PathSeparator.ToString();

    [Fact]
    public void Prepend_PutsPythonFirst_AndKeepsExistingEntriesAsWritten()
    {
        var existing = OperatingSystem.IsWindows() ? @"%USERPROFILE%\bin;C:\tools" : "$HOME/bin:/tools";
        var updated = UserPathEditor.Prepend(existing, Home)!;

        Assert.StartsWith(First + Sep, updated);
        Assert.EndsWith(Sep + existing, updated);
    }

    [Fact]
    public void Prepend_EmptyPath_HasOnlyPython()
    {
        var updated = UserPathEditor.Prepend(null, Home)!;
        Assert.StartsWith(First, updated);
        Assert.False(updated.EndsWith(Sep));
    }

    [Fact]
    public void Prepend_AlreadyPresent_ChangesNothing()
    {
        var once = UserPathEditor.Prepend("/other", Home)!;
        Assert.Null(UserPathEditor.Prepend(once, Home));
    }

    [Fact]
    public void Prepend_RecognizesDifferentSpellings()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(UserPathEditor.Prepend(@"C:\PY\HOME\;""c:\py\home\scripts""", Home));
    }

    [Fact]
    public void Prepend_RecognizesVariablesInExistingEntries()
    {
        if (!OperatingSystem.IsWindows()) return;

        var profile = Environment.GetEnvironmentVariable("USERPROFILE")!;
        var home = Path.Combine(profile, "pyhome");
        Assert.Null(UserPathEditor.Prepend(@"%USERPROFILE%\pyhome;%USERPROFILE%\pyhome\Scripts", home));
    }

    [Fact]
    public void Prepend_OnePresent_AddsOnlyTheOther()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(@"C:\py\home\Scripts;C:\py\home", UserPathEditor.Prepend(@"C:\py\home", Home));
    }

    [Fact]
    public void Prepend_LegacyInstallFolderOnPath_ChangesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(UserPathEditor.Prepend(@"C:\py\home\install\Scripts\;C:\py\home\install\;C:\tools", Home));
    }

    [Fact]
    public void Prepend_FolderThatOnlyStartsTheSame_DoesNotCount()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.NotNull(UserPathEditor.Prepend(@"C:\py\home\installer", Home));
    }

    [Fact]
    public void Registry_KeepsVariablesUnexpanded_AndTheValueKind()
    {
        if (!OperatingSystem.IsWindows()) return;

        using (var key = Registry.CurrentUser.CreateSubKey(_key))
            key.SetValue("Path", @"%USERPROFILE%\bin", RegistryValueKind.ExpandString);

        Assert.True(UserPathEditor.AddToRegistry(_key, Home));
        Assert.False(UserPathEditor.AddToRegistry(_key, Home));

        using var check = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(RegistryValueKind.ExpandString, check.GetValueKind("Path"));
        Assert.Equal(
            @"C:\py\home\Scripts;C:\py\home;%USERPROFILE%\bin",
            check.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public void Registry_NoExistingValue_CreatesOne()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.True(UserPathEditor.AddToRegistry(_key, Home));

        using var check = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(@"C:\py\home\Scripts;C:\py\home", check.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    private PythonManager CreateManager(bool addToPath) => new(new PythonManagerOptions
    {
        SharedPath = _fixture.SharedRoot,
        EnvironmentPath = _fixture.NewPath("project"),
        AddToUserPath = addToPath,
        UserEnvironmentKey = _key,
    });

    [Fact]
    public void AddToUserPath_IsOnByDefault()
    {
        Assert.True(new PythonManagerOptions().AddToUserPath);
    }

    [Fact]
    public async Task Initialize_OptionOn_UpdatesUserAndProcessPath()
    {
        await using var manager = CreateManager(addToPath: true);
        await manager.InitializeAsync();

        var home = Path.GetDirectoryName(manager.SharedPythonPath)!;
        if (!OperatingSystem.IsWindows()) home = Path.GetDirectoryName(home)!;

        Assert.Contains(home, Environment.GetEnvironmentVariable("PATH"));

        if (!OperatingSystem.IsWindows()) return;
        using var check = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal($@"{home}\Scripts;{home}", check.GetValue("Path"));
    }

    [Fact]
    public async Task Initialize_OptionOff_LeavesPathAlone()
    {
        await using var manager = CreateManager(addToPath: false);
        await manager.InitializeAsync();

        Assert.Equal(_processPath, Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows()) Assert.Null(Registry.CurrentUser.OpenSubKey(_key));
    }

    [Fact]
    public async Task ChildProcesses_FindTheirOwnPythonByName_EvenWithTheOptionOff()
    {
        await using var manager = _fixture.CreateManager(Guid.NewGuid().ToString("N")[..8]);
        var result = await manager.RunAsync(new[] { "-c", "import os; print(os.environ['PATH'].split(os.pathsep)[0])" });

        Assert.Equal(Path.GetDirectoryName(manager.PythonExecutable), result.StandardOutput.Trim());
    }

    [Fact]
    public async Task PythonProcess_ScriptsFindTheirOwnPythonByName()
    {
        await using var process = new PythonProcess();
        await process.StartAsync(_fixture.Python, _fixture.WriteScript("""
            import os, sys
            print("READY", flush=True)
            print(os.environ["PATH"].split(os.pathsep)[0], flush=True)
            sys.stdin.read()
            """));

        Assert.Equal(Path.GetDirectoryName(_fixture.Python), await process.ReceiveAsync());
    }
}
