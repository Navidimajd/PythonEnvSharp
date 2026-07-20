using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PythonEnvSharp;

/// <summary>
/// Manages Python environment: shared Python, packages, per-project venvs.
/// </summary>
public sealed class PythonManager : IAsyncDisposable
{
    private readonly PythonManagerOptions _options;
    private readonly ILogger _logger;
    private readonly string _sharedPath;
    private readonly string _projectPath;
    private bool _isInitialized;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string PythonExecutable => GetPythonExecutable();

    /// <summary>
    /// Get the shared Python path (used by all projects).
    /// </summary>
    public string SharedPythonPath => Path.Combine(_sharedPath, "python", "python.exe");

    /// <summary>
    /// Get the shared packages directory.
    /// </summary>
    public string SharedPackagesPath => Path.Combine(_sharedPath, "packages");

    public PythonManager(PythonManagerOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new PythonManagerOptions();
        _logger = logger ?? NullLogger<PythonManager>.Instance;

        _sharedPath = _options.SharedPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PythonEnvSharp", "shared");

        _projectPath = _options.EnvironmentPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PythonEnvSharp", "projects", Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>
    /// Create a named project environment (reuses existing if name matches).
    /// </summary>
    public static PythonManager ForProject(string projectName, PythonManagerOptions? options = null, ILogger? logger = null)
    {
        options ??= new PythonManagerOptions();
        options.EnvironmentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PythonEnvSharp", "projects", projectName);

        return new PythonManager(options, logger);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized) return;

            if (_options.UseSharedEnvironment)
            {
                await SetupSharedEnvironmentAsync(cancellationToken);
                await SetupProjectVenvAsync(cancellationToken);
            }
            else
            {
                await SetupStandaloneEnvironmentAsync(cancellationToken);
            }

            _isInitialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SetupSharedEnvironmentAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_sharedPath);

        // Download shared Python if not exists
        var sharedPython = Path.Combine(_sharedPath, "python", "python.exe");
        if (!File.Exists(sharedPython))
        {
            Console.WriteLine("  Downloading shared Python...");
            await DownloadPythonAsync(Path.Combine(_sharedPath, "python"), ct);
        }
        else
        {
            Console.WriteLine("  Shared Python already installed.");
        }
    }

    private async Task SetupProjectVenvAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_projectPath);

        var venvDir = Path.Combine(_projectPath, "venv");
        var projectPython = Path.Combine(venvDir, ScriptsDir, "python.exe");
        if (File.Exists(projectPython)) return;

        var sharedPython = Path.Combine(_sharedPath, "python", "python.exe");
        if (!File.Exists(sharedPython))
        {
            throw new PythonEnvException("Shared Python not found. Run InitializeAsync first.");
        }

        // Try to create venv
        Console.WriteLine("  Creating project environment...");
        var exitCode = await RunCommandWithExitCodeAsync(sharedPython, $"-m venv \"{venvDir}\"", ct);

        if (exitCode != 0 || !File.Exists(projectPython))
        {
            // Embedded Python doesn't have venv, use shared Python directly
            Console.WriteLine("  venv not available, using shared Python directly");
            return;
        }

        // Configure venv to use shared packages
        var sitePackages = Path.Combine(venvDir, "Lib", "site-packages");
        if (Directory.Exists(sitePackages))
        {
            // Create .pth file to include shared packages
            var sharedPackagesDir = Path.Combine(_sharedPath, "packages");
            Directory.CreateDirectory(sharedPackagesDir);
            var pthFile = Path.Combine(sitePackages, "pythonenvsharp_shared.pth");
            await File.WriteAllTextAsync(pthFile, sharedPackagesDir + Environment.NewLine, ct);
            Console.WriteLine($"  Configured to use shared packages from: {sharedPackagesDir}");
        }

        Console.WriteLine("  Project environment created.");
    }

    private async Task SetupStandaloneEnvironmentAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_projectPath);

        var pythonDir = Path.Combine(_projectPath, "python");
        var pythonExe = Path.Combine(pythonDir, "python.exe");

        if (!File.Exists(pythonExe))
        {
            await DownloadPythonAsync(pythonDir, ct);
        }
    }

    public async Task InstallPackagesAsync(
        IEnumerable<string> packages,
        IEnumerable<(string Name, string Url)>? mirrors = null,
        CancellationToken cancellationToken = default)
    {
        var pkgList = packages.ToList();
        var mirrorList = mirrors?.ToList() ?? GetDefaultMirrors();
        var pipFlags = "--no-cache-dir --timeout 60 --retries 3";

        if (_options.UseSharedEnvironment)
        {
            // Install to shared packages directory
            await InstallToSharedAsync(pkgList, mirrorList, pipFlags, cancellationToken);
        }
        else
        {
            // Install to project environment
            var pythonExe = GetPythonExecutable();
            await InstallToEnvironmentAsync(pythonExe, pkgList, mirrorList, pipFlags, cancellationToken);
        }
    }

    private async Task InstallToSharedAsync(
        List<string> packages, List<(string Name, string Url)> mirrors, string pipFlags, CancellationToken ct)
    {
        var sharedPython = Path.Combine(_sharedPath, "python", "python.exe");
        if (!File.Exists(sharedPython))
        {
            throw new PythonEnvException("Shared Python not found. Run InitializeAsync first.");
        }

        // Check what's already installed
        Console.WriteLine("  Checking shared packages...");
        var checkOutput = await RunCommandAsync(sharedPython,
            $"-m pip show {string.Join(" ", packages)}", ct);

        var installed = packages.Where(p =>
            checkOutput.Contains($"Name: {p}", StringComparison.OrdinalIgnoreCase)).ToList();
        var notInstalled = packages.Where(p =>
            !checkOutput.Contains($"Name: {p}", StringComparison.OrdinalIgnoreCase)).ToList();

        if (installed.Count > 0)
            Console.WriteLine($"  Already in shared: {string.Join(", ", installed)}");

        if (notInstalled.Count == 0)
        {
            Console.WriteLine("  All packages available in shared environment.");
            return;
        }

        Console.WriteLine($"  Installing to shared: {string.Join(", ", notInstalled)}");

        // Install with --target to shared packages directory
        var targetDir = Path.Combine(_sharedPath, "packages");
        Directory.CreateDirectory(targetDir);

        var cmd = $"-m pip install {string.Join(" ", notInstalled)} {pipFlags} --target \"{targetDir}\"";
        var exitCode = await RunCommandWithExitCodeAsync(sharedPython, cmd, ct);

        if (exitCode != 0)
        {
            // Try mirrors
            foreach (var (name, url) in mirrors.Skip(1))
            {
                var indexArg = $"-i {url} --trusted-host {new Uri(url).Host}";
                cmd = $"-m pip install {string.Join(" ", notInstalled)} {pipFlags} --target \"{targetDir}\" {indexArg}";
                Console.WriteLine($"  Retrying from {name}...");
                exitCode = await RunCommandWithExitCodeAsync(sharedPython, cmd, ct);
                if (exitCode == 0)
                {
                    Console.WriteLine($"  Installed from {name}");
                    return;
                }
            }
        }
    }

    private async Task InstallToEnvironmentAsync(
        string pythonExe, List<string> packages, List<(string Name, string Url)> mirrors, string pipFlags, CancellationToken ct)
    {
        Console.WriteLine("  Checking installed packages...");
        var checkOutput = await RunCommandAsync(pythonExe, $"-m pip show {string.Join(" ", packages)}", ct);

        var notInstalled = packages.Where(p =>
            !checkOutput.Contains($"Name: {p}", StringComparison.OrdinalIgnoreCase)).ToList();

        if (notInstalled.Count == 0)
        {
            Console.WriteLine("  All packages installed.");
            return;
        }

        Console.WriteLine($"  Need to install: {string.Join(", ", notInstalled)}");

        var cmd = $"-m pip install {string.Join(" ", notInstalled)} {pipFlags}";
        var exitCode = await RunCommandWithExitCodeAsync(pythonExe, cmd, ct);

        if (exitCode != 0)
        {
            foreach (var (name, url) in mirrors.Skip(1))
            {
                var indexArg = $"-i {url} --trusted-host {new Uri(url).Host}";
                cmd = $"-m pip install {string.Join(" ", notInstalled)} {pipFlags} {indexArg}";
                Console.WriteLine($"  Retrying from {name}...");
                exitCode = await RunCommandWithExitCodeAsync(pythonExe, cmd, ct);
                if (exitCode == 0)
                {
                    Console.WriteLine($"  Installed from {name}");
                    return;
                }
            }
        }
    }

    public async Task<bool> CheckPackageInstalledAsync(string packageName, CancellationToken cancellationToken = default)
    {
        // Check in shared packages directory
        if (_options.UseSharedEnvironment)
        {
            var sharedPackagesDir = Path.Combine(_sharedPath, "packages");
            if (Directory.Exists(sharedPackagesDir))
            {
                // Check if package directory exists (case-insensitive)
                var packageDir = Directory.GetDirectories(sharedPackagesDir)
                    .FirstOrDefault(d => Path.GetFileName(d).Equals(packageName, StringComparison.OrdinalIgnoreCase));
                if (packageDir is not null) return true;
            }
        }

        // Also check via pip
        var pythonExe = GetPythonExecutable();
        var output = await RunCommandAsync(pythonExe, $"-m pip show {packageName}", cancellationToken);
        return output.Contains($"Name: {packageName}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Install pywin32 for named pipe support on Windows.
    /// </summary>
    public async Task EnsureNamedPipeSupportAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return;

        var installed = await CheckPackageInstalledAsync("pywin32", cancellationToken);
        if (installed) return;

        Console.WriteLine("  Installing pywin32 for named pipe support...");
        await InstallPackagesAsync(new[] { "pywin32" }, cancellationToken: cancellationToken);
    }

    public string GetPythonExecutable()
    {
        if (_options.UseSharedEnvironment)
        {
            var venvPython = Path.Combine(_projectPath, "venv", ScriptsDir, "python.exe");
            if (File.Exists(venvPython))
                return venvPython;

            return Path.Combine(_sharedPath, "python", "python.exe");
        }

        return Path.Combine(_projectPath, "python", "python.exe");
    }

    private static string ScriptsDir => OperatingSystem.IsWindows() ? "Scripts" : "bin";

    private async Task<string> DownloadPythonAsync(string targetDir, CancellationToken ct)
    {
        // Download full Python installer (includes venv, pip, etc.)
        var installerName = $"python-{_options.PythonVersion}-amd64.exe";

        var mirrors = new (string Name, string Url)[]
        {
            ("npmmirror", $"https://registry.npmmirror.com/-/binary/python/{_options.PythonVersion}/{installerName}"),
            ("python.org", $"https://www.python.org/ftp/python/{_options.PythonVersion}/{installerName}"),
        };

        foreach (var (name, url) in mirrors)
        {
            try
            {
                Console.WriteLine($"  Downloading Python from {name}...");

                using var httpClient = new HttpClient { Timeout = _options.DownloadTimeout };
                var tempInstaller = Path.Combine(Path.GetTempPath(), $"python-{Guid.NewGuid():N}.exe");

                {
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 0;
                    var downloaded = 0L;

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(tempInstaller, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                    var buffer = new byte[81920];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;
                        if (total > 0)
                            Console.Write($"\r  Downloading: {downloaded / 1024 / 1024}MB / {total / 1024 / 1024}MB ({downloaded * 100 / total}%)");
                    }
                    Console.WriteLine();
                }

                // Install Python silently
                Console.WriteLine("  Installing Python...");
                var installDir = Path.Combine(targetDir, "install");
                Directory.CreateDirectory(installDir);

                var installPsi = new ProcessStartInfo
                {
                    FileName = tempInstaller,
                    Arguments = $"/quiet InstallAllUsers=0 TargetDir=\"{installDir}\" PrependPath=1 Include_test=0 Include_pip=1 Include_launcher=0",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var installProc = Process.Start(installPsi);
                if (installProc is not null)
                {
                    await installProc.WaitForExitAsync(ct);
                }

                File.Delete(tempInstaller);

                // Find the installed python.exe
                var installedPython = Directory.GetFiles(installDir, "python.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (installedPython is null)
                {
                    Console.WriteLine("  Python installation failed, trying embedded fallback...");
                    return await DownloadEmbeddedPythonAsync(targetDir, ct);
                }

                // Copy to target directory
                var installDir2 = Path.GetDirectoryName(installedPython) ?? installDir;
                CopyDirectory(installDir2, targetDir);

                return targetDir;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name} failed: {ex.Message}");
            }
        }

        // Fallback to embedded Python
        Console.WriteLine("  Full installer failed, trying embedded Python...");
        return await DownloadEmbeddedPythonAsync(targetDir, ct);
    }

    private async Task<string> DownloadEmbeddedPythonAsync(string targetDir, CancellationToken ct)
    {
        var zipName = $"python-{_options.PythonVersion}-embed-amd64.zip";

        var mirrors = new (string Name, string Url)[]
        {
            ("npmmirror", $"https://registry.npmmirror.com/-/binary/python/{_options.PythonVersion}/{zipName}"),
            ("python.org", $"https://www.python.org/ftp/python/{_options.PythonVersion}/{zipName}"),
        };

        foreach (var (name, url) in mirrors)
        {
            try
            {
                Console.WriteLine($"  Downloading embedded Python from {name}...");

                using var httpClient = new HttpClient { Timeout = _options.DownloadTimeout };
                var tempZip = Path.Combine(Path.GetTempPath(), $"python-{Guid.NewGuid():N}.zip");

                {
                    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 0;
                    var downloaded = 0L;

                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                    var buffer = new byte[81920];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;
                        if (total > 0)
                            Console.Write($"\r  Downloading: {downloaded / 1024 / 1024}MB / {total / 1024 / 1024}MB ({downloaded * 100 / total}%)");
                    }
                    Console.WriteLine();
                }

                ZipFile.ExtractToDirectory(tempZip, targetDir, true);
                File.Delete(tempZip);

                // Enable pip
                var pthFile = Directory.GetFiles(targetDir, "python*._pth").FirstOrDefault();
                if (pthFile is not null)
                {
                    var content = await File.ReadAllTextAsync(pthFile, ct);
                    if (content.Contains("#import"))
                    {
                        content = content.Replace("#import", "import");
                        await File.WriteAllTextAsync(pthFile, content, ct);
                    }
                }

                return targetDir;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name} failed: {ex.Message}");
            }
        }

        throw new PythonEnvException("Failed to download Python from all mirrors");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private static List<(string Name, string Url)> GetDefaultMirrors() => new()
    {
        ("PyPI", ""),
        ("Tsinghua", "https://pypi.tuna.tsinghua.edu.cn/simple"),
        ("Aliyun", "https://mirrors.aliyun.com/pypi/simple"),
        ("USTC", "https://pypi.mirrors.ustc.edu.cn/simple"),
    };

    private async Task<string> RunCommandAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = _projectPath
        };
        using var proc = Process.Start(psi) ?? throw new PythonEnvException("Failed to start process");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout;
    }

    private async Task<int> RunCommandWithExitCodeAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = _projectPath
        };
        using var proc = Process.Start(psi) ?? throw new PythonEnvException("Failed to start process");

        var outputTask = Task.Run(async () =>
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line != null) Console.WriteLine($"    {line}");
            }
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line != null && !line.Contains("WARNING") && !line.Contains("DEPRECATION"))
                    Console.WriteLine($"    {line}");
            }
        }, ct);

        await Task.WhenAll(outputTask, errorTask);
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
        await ValueTask.CompletedTask;
    }
}
