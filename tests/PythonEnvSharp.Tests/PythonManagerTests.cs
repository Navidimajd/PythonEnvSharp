using PythonEnvSharp;
using Xunit;

namespace PythonEnvSharp.Tests;

[Collection("Python")]
public sealed class PythonManagerTests
{
    private static readonly (string, string)[] PyPI = { ("PyPI", "") };

    private readonly PythonFixture _fixture;

    public PythonManagerTests(PythonFixture fixture) => _fixture = fixture;

    private static string Unique => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Install_SecondTimeIsANoOp()
    {
        var logger = new ListLogger();
        await using var manager = _fixture.CreateManager(Unique, logger);

        await manager.InstallPackagesAsync(new[] { "six" }, PyPI);
        await manager.InstallPackagesAsync(new[] { "six" }, PyPI);

        Assert.Contains(logger.Lines, l => l.StartsWith("Already installed: six"));
        Assert.True(await manager.CheckPackageInstalledAsync("six"));
    }

    [Fact]
    public async Task Install_NonexistentPackage_Throws()
    {
        await using var manager = _fixture.CreateManager(Unique);
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            manager.InstallPackagesAsync(new[] { "this-package-does-not-exist-zz91" }, PyPI));
        Assert.NotEqual(0, ex.ExitCode);
        Assert.Contains("No matching distribution", ex.StandardError);
    }

    [Fact]
    public async Task Install_UsesTheFirstMirrorInTheList()
    {
        await using var manager = _fixture.CreateManager(Unique);

        // Nothing listens here. If the entry were ignored, pip would use PyPI and succeed.
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() =>
            manager.InstallPackagesAsync(new[] { "pyparsing" }, new[] { ("Unreachable", "http://127.0.0.1:9/simple") }));
        Assert.Contains("Unreachable", ex.Message);
    }

    [Fact]
    public async Task Install_BlankUrlAnywhereInTheList_IsDefaultIndex()
    {
        var logger = new ListLogger();
        await using var manager = _fixture.CreateManager(Unique, logger);

        await manager.InstallPackagesAsync(new[] { "iniconfig" }, new[] { ("Unreachable", "http://127.0.0.1:9/simple"), ("PyPI", "") });

        Assert.Contains(logger.Lines, l => l == "Installed from PyPI");
    }

    [Fact]
    public async Task Install_ReplacesAnExistingVersion_BothDirections()
    {
        await using var manager = _fixture.CreateManager(Unique);

        foreach (var version in new[] { "1.16.0", "1.17.0", "1.16.0" })
        {
            await manager.InstallPackagesAsync(new[] { $"six=={version}" }, PyPI);

            Assert.True(await manager.CheckPackageInstalledAsync($"six=={version}"));
            var imported = await manager.RunAsync(new[] { "-c", "import six; print(six.__version__)" });
            Assert.Equal(version, imported.StandardOutput.Trim());
            Assert.Single(Directory.GetDirectories(manager.SharedPackagesPath, "six-*.dist-info"));
        }
    }

    [Fact]
    public async Task Install_KeepsSharedDependencies_AndReportsThem()
    {
        var logger = new ListLogger();
        await using var manager = _fixture.CreateManager(Unique, logger);

        await manager.InstallPackagesAsync(new[] { "MarkupSafe==2.1.5" }, PyPI);
        await manager.InstallPackagesAsync(new[] { "Jinja2" }, PyPI);

        Assert.True(await manager.CheckPackageInstalledAsync("MarkupSafe==2.1.5"), "the existing dependency must stay");
        Assert.True(await manager.CheckPackageInstalledAsync("Jinja2"));
        Assert.Contains(logger.Lines, l => l.StartsWith("Kept markupsafe 2.1.5"));

        var imported = await manager.RunAsync(new[] { "-c", "import jinja2; print('ok')" });
        Assert.Equal("ok", imported.StandardOutput.Trim());
    }

    [Fact]
    public async Task Install_ReplacingRemovesFilesTheNewVersionDropped()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await manager.InstallPackagesAsync(new[] { "attrs==23.2.0" }, PyPI);

        var record = Directory.GetDirectories(manager.SharedPackagesPath, "attrs-*.dist-info").Single();
        var leftover = Path.Combine(manager.SharedPackagesPath, "attr", "only_in_old_version.py");
        File.WriteAllText(leftover, "");
        File.AppendAllText(Path.Combine(record, "RECORD"), "attr/only_in_old_version.py,,\n");

        await manager.InstallPackagesAsync(new[] { "attrs==24.2.0" }, PyPI);

        Assert.False(File.Exists(leftover));
        Assert.True(await manager.CheckPackageInstalledAsync("attrs==24.2.0"));
    }

    [Fact]
    public async Task Install_KeepsConsoleScripts()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await manager.InstallPackagesAsync(new[] { "wheel" }, PyPI);

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(manager.SharedPackagesPath, "bin"), "wheel*"));
    }

    [Fact]
    public async Task FailedInstall_LeavesSharedFolderUnchanged()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await manager.InitializeAsync();
        var before = Directory.GetFileSystemEntries(manager.SharedPackagesPath).OrderBy(e => e).ToList();

        await Assert.ThrowsAsync<PythonEnvException>(() =>
            manager.InstallPackagesAsync(new[] { "this-package-does-not-exist-zz94" }, PyPI));

        Assert.Equal(before, Directory.GetFileSystemEntries(manager.SharedPackagesPath).OrderBy(e => e).ToList());
        Assert.Empty(Directory.GetDirectories(_fixture.SharedRoot, ".staging-*"));
    }

    [Fact]
    public async Task ExistingPlainPathPth_IsLeftAlone()
    {
        var name = Unique;
        await using (var first = _fixture.CreateManager(name)) await first.InitializeAsync();

        await using var second = _fixture.CreateManager(name);
        var sitePackages = OperatingSystem.IsWindows()
            ? Path.Combine(_fixture.WorkDir, "projects", name, "venv", "Lib", "site-packages")
            : Directory.GetDirectories(Path.Combine(_fixture.WorkDir, "projects", name, "venv", "lib"), "python*").Select(d => Path.Combine(d, "site-packages")).First();
        var pth = Path.Combine(sitePackages, "pythonenvsharp_shared.pth");
        var plain = second.SharedPackagesPath + Environment.NewLine;
        File.WriteAllText(pth, plain);

        await second.InitializeAsync();

        Assert.Equal(plain, File.ReadAllText(pth));
    }

    [Fact]
    public async Task PthPointingSomewhereElse_IsCorrected()
    {
        var name = Unique;
        await using (var first = _fixture.CreateManager(name)) await first.InitializeAsync();

        var sitePackages = OperatingSystem.IsWindows()
            ? Path.Combine(_fixture.WorkDir, "projects", name, "venv", "Lib", "site-packages")
            : Directory.GetDirectories(Path.Combine(_fixture.WorkDir, "projects", name, "venv", "lib"), "python*").Select(d => Path.Combine(d, "site-packages")).First();
        var pth = Path.Combine(sitePackages, "pythonenvsharp_shared.pth");
        File.WriteAllText(pth, "C:\\moved\\packages\n");

        await using var second = _fixture.CreateManager(name);
        await second.InitializeAsync();

        Assert.Contains("addsitedir", File.ReadAllText(pth));
    }

    [Fact]
    public void OfficialInstaller_RejectsAnythingNotSignedByPython()
    {
        if (!OperatingSystem.IsWindows()) return;

        var unsigned = _fixture.NewPath("unsigned.exe");
        File.WriteAllText(unsigned, "not a real installer");
        Assert.False(OfficialPythonInstaller.IsSignedByPython(unsigned));

        Assert.False(OfficialPythonInstaller.HasTrustedSignature(unsigned));

        // Validly signed, but by someone else. The first check proves the signature check itself works.
        var signedByOthers = Environment.ProcessPath!;
        Assert.True(OfficialPythonInstaller.HasTrustedSignature(signedByOthers));
        Assert.False(OfficialPythonInstaller.IsSignedByPython(signedByOthers));
    }

    [Fact]
    public void OfficialInstaller_CantBeCombinedWithCustomDistribution()
    {
        Assert.Throws<ArgumentException>(() => new PythonManager(new PythonManagerOptions
        {
            InstallMethod = PythonInstallMethod.OfficialInstaller,
            CustomDistribution = new PythonDistribution(new string('a', 64), new[] { "https://example.com/x" }),
        }));
    }

    [Fact]
    public async Task Check_NormalizesNames_AndAvoidsFalsePositives()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await manager.InstallPackagesAsync(new[] { "python-dateutil" }, PyPI);

        Assert.True(await manager.CheckPackageInstalledAsync("python-dateutil"));
        Assert.True(await manager.CheckPackageInstalledAsync("python_dateutil"));
        Assert.True(await manager.CheckPackageInstalledAsync("Python.Dateutil"));
        Assert.False(await manager.CheckPackageInstalledAsync("python"), "prefix of an installed name");
        Assert.False(await manager.CheckPackageInstalledAsync("python-dateutil<1"), "unsatisfied version");

        Directory.CreateDirectory(Path.Combine(manager.SharedPackagesPath, "bin"));
        Assert.False(await manager.CheckPackageInstalledAsync("bin"), "a folder that isn't a package");
    }

    [Fact]
    public async Task Packages_AreSharedBetweenProjects()
    {
        await using var a = _fixture.CreateManager(Unique);
        await using var b = _fixture.CreateManager(Unique);

        await a.InstallPackagesAsync(new[] { "tomli" }, PyPI);

        Assert.True(await b.CheckPackageInstalledAsync("tomli"));
        var imported = await b.RunAsync(new[] { "-c", "import tomli; print('ok')" });
        Assert.Equal("ok", imported.StandardOutput.Trim());
    }

    [Fact]
    public async Task PackagesThatUseTheirOwnPthFiles_Work()
    {
        if (!OperatingSystem.IsWindows()) return; // pywin32 is Windows-only

        await using var manager = _fixture.CreateManager(Unique);
        await manager.InstallPackagesAsync(new[] { "pywin32" }, PyPI);

        var imported = await manager.RunAsync(new[] { "-c", "import win32api; print('ok')" });
        Assert.Equal("ok", imported.StandardOutput.Trim());
    }

    [Fact]
    public async Task Install_Cancelled_ThrowsCancellation()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await manager.InitializeAsync();

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.InstallPackagesAsync(new[] { "requests" }, PyPI, cts.Token));
    }

    [Fact]
    public async Task UsableWithoutCallingInitializeFirst()
    {
        await using var manager = _fixture.CreateManager(Unique);
        Assert.False(await manager.CheckPackageInstalledAsync("definitely-not-installed-zz93"));
    }

    [Fact]
    public async Task ParallelInitialize_SameProject_Works()
    {
        var name = Unique;
        await using var a = _fixture.CreateManager(name);
        await using var b = _fixture.CreateManager(name);
        await Task.WhenAll(a.InitializeAsync(), b.InitializeAsync());
        Assert.True(File.Exists(a.PythonExecutable));
    }

    [Fact]
    public async Task InterruptedVenv_IsRepaired()
    {
        var name = Unique;
        await using (var first = _fixture.CreateManager(name)) await first.InitializeAsync();

        // A venv interrupted before pip was installed has an interpreter but no pip
        var venv = Path.Combine(_fixture.WorkDir, "projects", name, "venv");
        foreach (var pip in Directory.GetDirectories(venv, "pip*", SearchOption.AllDirectories)) Directory.Delete(pip, recursive: true);

        await using var second = _fixture.CreateManager(name);
        await second.InitializeAsync();
        var result = await second.RunAsync(new[] { "-m", "pip", "--version" });
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DamagedVenv_ExplainsHowToRecover()
    {
        var name = Unique;
        await using (var first = _fixture.CreateManager(name)) await first.InitializeAsync();

        // Pip's metadata remains, so venv believes pip is installed and can't repair it
        var venv = Path.Combine(_fixture.WorkDir, "projects", name, "venv");
        foreach (var pip in Directory.GetDirectories(venv, "pip", SearchOption.AllDirectories)) Directory.Delete(pip, recursive: true);

        await using var second = _fixture.CreateManager(name);
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => second.InitializeAsync());
        Assert.Contains("Delete the venv folder", ex.Message);
    }

    [Fact]
    public async Task PythonVersion_IsHonored_AndMismatchedVenvIsReported()
    {
        var name = Unique;
        await using (var v311 = _fixture.CreateManager(name)) await v311.InitializeAsync();

        await using var v312 = _fixture.CreateManager(Unique, version: "3.12.16");
        await v312.InitializeAsync();
        var reported = await v312.RunAsync(new[] { "--version" });
        Assert.Equal("Python 3.12.16", reported.StandardOutput.Trim());
        Assert.Contains("packages-3.12", v312.SharedPackagesPath);

        await using var mismatched = _fixture.CreateManager(name, version: "3.12.16");
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => mismatched.InitializeAsync());
        Assert.Contains("3.11.9", ex.Message);
    }

    [Fact]
    public async Task UnknownVersion_ExplainsHowToProceed()
    {
        await using var manager = new PythonManager(new PythonManagerOptions
        {
            SharedPath = _fixture.NewPath("shared"),
            EnvironmentPath = _fixture.NewPath("project"),
            PythonVersion = "3.99.0",
            AddToUserPath = false,
        });
        var ex = await Assert.ThrowsAsync<PythonEnvException>(() => manager.InitializeAsync());
        Assert.Contains("CustomDistribution", ex.Message);
    }

    [Fact]
    public async Task Standalone_InstallsPythonAndPackagesInTheProject()
    {
        var project = _fixture.NewPath("standalone");
        await using var manager = new PythonManager(new PythonManagerOptions { EnvironmentPath = project, UseSharedEnvironment = false, AddToUserPath = false });

        await manager.InstallPackagesAsync(new[] { "six" }, PyPI);

        Assert.StartsWith(project, manager.PythonExecutable);
        Assert.True(await manager.CheckPackageInstalledAsync("six"));
    }

    [Fact]
    public async Task PackagesPathWithNonAsciiCharacters_IsReachable()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await manager.InitializeAsync();

        var dir = Path.Combine(_fixture.WorkDir, $"پکیج 中 {Unique}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "probe_module.py"), "value = 42");

        var line = PythonManager.BuildSharedPackagesPthLine(dir);
        Assert.True(line.All(c => c < 128), "the line must be pure ASCII");

        var script = _fixture.WriteScript($"{line}\nimport probe_module\nprint(probe_module.value)");
        var result = await manager.RunAsync(new[] { script });
        Assert.Equal("42", result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("../../escaped")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\temp")]
    [InlineData("")]
    public void ForProject_RejectsNamesThatAreNotPlainFolders(string name)
    {
        Assert.Throws<ArgumentException>(() => PythonManager.ForProject(name));
    }

    [Fact]
    public void ForProject_DoesNotChangeTheCallersOptions()
    {
        var options = new PythonManagerOptions();
        PythonManager.ForProject("one", options);
        Assert.Null(options.EnvironmentPath);
    }

    [Theory]
    [InlineData("--index-url=https://example.com")]
    [InlineData("-r requirements.txt")]
    [InlineData("  ")]
    public async Task Install_RejectsPipOptionsAndBlanks(string package)
    {
        await using var manager = _fixture.CreateManager(Unique);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.InstallPackagesAsync(new[] { package }));
    }

    [Fact]
    public async Task Install_RejectsInvalidMirrorUrl()
    {
        await using var manager = _fixture.CreateManager(Unique);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.InstallPackagesAsync(new[] { "six" }, new[] { ("Bad", "not a url") }));
    }

    [Fact]
    public void InvalidPythonVersion_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new PythonManager(new PythonManagerOptions { PythonVersion = "../3.11" }));
    }

    [Fact]
    public async Task UseAfterDispose_Throws()
    {
        var manager = _fixture.CreateManager(Unique);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.InitializeAsync());
    }
}
