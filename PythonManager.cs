using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PythonEnvSharp;

/// <summary>
/// Manages Python environment: shared Python, packages, per-project venvs.
/// </summary>
public sealed class PythonManager : IAsyncDisposable
{
    // Answers "is this requirement satisfied?" with pip's own parsing, so name normalization,
    // version specifiers and metadata all follow the same rules pip uses.
    private const string CheckScript = """
        import importlib.metadata as md, json, sys
        from pip._vendor.packaging.requirements import Requirement
        from pip._vendor.packaging.utils import canonicalize_name
        extra = sys.argv[1]
        paths = [p for p in sys.path if p] + ([extra] if extra else [])
        found = {}
        for d in md.distributions(path=paths):
            meta = d.metadata
            name = meta["Name"] if meta else None
            if name:
                found.setdefault(canonicalize_name(name), d.version)
        result = {}
        for spec in sys.argv[2:]:
            try:
                r = Requirement(spec)
                v = found.get(canonicalize_name(r.name))
                # Requested extras may need more packages, so those are left for pip to resolve.
                result[spec] = v is not None and not r.extras and r.url is None and r.specifier.contains(v, prereleases=True)
            except Exception:
                result[spec] = False
        print(json.dumps(result))
        """;

    // Moves what pip installed into a staging folder over to the shared folder. Only the packages
    // the caller asked for replace an existing version. Anything else already there is kept, because
    // other projects may depend on it, and a kept version that differs from pip's choice is reported.
    // Replacing goes through RECORD, so files the old version had and the new one doesn't are removed.
    private const string MergeStagedPackagesScript = """
        import csv, os, re, shutil, sys
        from pip._vendor.packaging.requirements import Requirement

        staging, target = sys.argv[1], sys.argv[2]

        def canonical(name):
            return re.sub(r"[-_.]+", "-", name).lower()

        requested = set()
        for spec in sys.argv[3:]:
            try:
                requested.add(canonical(Requirement(spec).name))
            except Exception:
                pass

        def dists(folder):
            found = {}
            for entry in os.listdir(folder):
                if entry.endswith(".dist-info"):
                    name, version = entry[:-len(".dist-info")].rsplit("-", 1)
                    found[canonical(name)] = (entry, version)
            return found

        def recorded_files(folder, info):
            record = os.path.join(folder, info, "RECORD")
            if not os.path.exists(record):
                return []
            with open(record, newline="", encoding="utf-8") as f:
                rows = [row[0] for row in csv.reader(f) if row]
            files = []
            for row in rows:
                parts = os.path.normpath(row).split(os.sep)
                # pip --target records scripts and data as ../../bin/x, but places them at bin/x.
                while parts and parts[0] == os.pardir:
                    parts.pop(0)
                relative = os.path.join(*parts) if parts else ""
                if relative and not os.path.isabs(relative) and parts[0] != info:
                    files.append(relative)
            return files

        def remove_dist(info):
            for relative in recorded_files(target, info):
                path = os.path.join(target, relative)
                if os.path.isfile(path):
                    os.remove(path)
                parent = os.path.dirname(path)
                while parent != target and os.path.isdir(parent) and not os.listdir(parent):
                    os.rmdir(parent)
                    parent = os.path.dirname(parent)
            shutil.rmtree(os.path.join(target, info))

        def move_dist(info):
            for relative in recorded_files(staging, info):
                source = os.path.join(staging, relative)
                if os.path.isfile(source):
                    destination = os.path.join(target, relative)
                    os.makedirs(os.path.dirname(destination), exist_ok=True)
                    os.replace(source, destination)
            destination = os.path.join(target, info)
            if os.path.exists(destination):
                shutil.rmtree(destination)
            shutil.move(os.path.join(staging, info), destination)

        existing = dists(target)
        for name, (info, version) in dists(staging).items():
            if name in existing and name not in requested:
                if existing[name][1] != version:
                    print(f"Kept {name} {existing[name][1]} because it is already shared; pip chose {version} for this install")
                continue
            if name in existing:
                remove_dist(existing[name][0])
            move_dist(info)
        """;

    private const string HealthScript = "import pip, sys, venv; print('%d.%d.%d' % sys.version_info[:3])";

    private readonly PythonManagerOptions _options;
    private readonly ILogger _logger;
    private readonly string _sharedPath;
    private readonly string _projectPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _isInitialized;
    private bool _disposed;

    public string PythonExecutable => GetPythonExecutable();

    /// <summary>
    /// Get the shared Python path (used by all projects).
    /// </summary>
    public string SharedPythonPath => PythonLayout.SharedPythonExecutable(_sharedPath, _options.PythonVersion);

    /// <summary>
    /// Get the shared packages directory.
    /// </summary>
    public string SharedPackagesPath => PythonLayout.SharedPackagesDir(_sharedPath, _options.PythonVersion);

    public PythonManager(PythonManagerOptions? options = null, ILogger? logger = null)
    {
        _options = options?.Clone() ?? new PythonManagerOptions();
        _logger = logger ?? NullLogger<PythonManager>.Instance;

        if (!Regex.IsMatch(_options.PythonVersion, @"^\d+\.\d+\.\d+$"))
            throw new ArgumentException($"PythonVersion must look like 3.11.9, not '{_options.PythonVersion}'.", nameof(options));

        if (_options.InstallMethod == PythonInstallMethod.OfficialInstaller && _options.CustomDistribution is not null)
            throw new ArgumentException("CustomDistribution can't be combined with the OfficialInstaller install method.", nameof(options));

        _sharedPath = _options.SharedPath ?? PythonLayout.DefaultSharedRoot;
        _projectPath = _options.EnvironmentPath ?? PythonLayout.ProjectDir(DefaultProjectName());
    }

    /// <summary>
    /// Create a named project environment (reuses existing if name matches).
    /// </summary>
    public static PythonManager ForProject(string projectName, PythonManagerOptions? options = null, ILogger? logger = null)
    {
        if (!IsValidProjectName(projectName))
            throw new ArgumentException($"'{projectName}' can't be used as a project name, because it isn't a plain folder name.", nameof(projectName));

        var copy = options?.Clone() ?? new PythonManagerOptions();
        copy.EnvironmentPath = PythonLayout.ProjectDir(projectName);
        return new PythonManager(copy, logger);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        Directory.CreateDirectory(_projectPath);

        using (await FileLock.AcquireAsync(Path.Combine(_sharedPath, ".install.lock"), _logger, ct))
        {
            if (!File.Exists(SharedPythonPath))
            {
                var target = PythonLayout.SharedHome(_sharedPath, _options.PythonVersion);
                await InstallPythonAsync(target, _sharedPath, ct);
            }
            else
            {
                _logger.LogInformation("Shared Python already installed: {Path}", SharedPythonPath);
            }
        }

        var health = await CheckHealthAsync(SharedPythonPath, ct);
        if (!IsHealthy(health))
            throw new PythonEnvException($"The shared Python at {SharedPythonPath} is unusable: {health.Problem} Delete its folder to reinstall it.");

        if (_options.AddToUserPath)
        {
            using (await FileLock.AcquireAsync(Path.Combine(_sharedPath, ".install.lock"), _logger, ct))
            {
                AddToPath(PythonLayout.SharedHome(_sharedPath, _options.PythonVersion));
            }
        }
    }

    /// <summary>
    /// A failure here only gets a warning: Python itself works, and some machines forbid this edit by policy.
    /// </summary>
    private void AddToPath(string home)
    {
        if (!_options.AddToUserPath) return;

        var processPath = UserPathEditor.Prepend(Environment.GetEnvironmentVariable("PATH"), home);
        if (processPath is not null) Environment.SetEnvironmentVariable("PATH", processPath);

        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (UserPathEditor.AddToRegistry(_options.UserEnvironmentKey, home))
                _logger.LogInformation("Added {Home} to the user's PATH", home);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning("Couldn't add {Home} to the user's PATH: {Reason}", home, ex.Message);
        }
    }

    private async Task SetupProjectVenvAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_projectPath);

        using (await FileLock.AcquireAsync(Path.Combine(_projectPath, ".lock"), _logger, ct))
        {
            var venvDir = Path.Combine(_projectPath, "venv");
            var venvPython = PythonLayout.VenvPython(venvDir);

            PythonHealth? health = File.Exists(venvPython) ? await CheckHealthAsync(venvPython, ct) : null;
            if (health?.Version is not null && !IsHealthy(health))
            {
                throw new PythonEnvException(
                    $"The venv in {venvDir} doesn't match the requested Python: {health.Problem} " +
                    "Delete the venv or use a different project name.");
            }

            if (health is null || !IsHealthy(health))
            {
                // Without --clear this keeps what's there and only adds what's missing, which
                // repairs an interrupted creation. It can't fix pip files damaged in other ways.
                _logger.LogInformation("Creating project environment in {Path}", venvDir);
                var result = await RunLoggedAsync(SharedPythonPath, new[] { "-m", "venv", venvDir }, ct);
                if (result.ExitCode != 0)
                    throw Failure($"Couldn't create a venv in {venvDir}.", result);

                health = await CheckHealthAsync(venvPython, ct);
                if (!IsHealthy(health))
                    throw new PythonEnvException($"The venv in {venvDir} is unusable: {health.Problem} Delete the venv folder to recreate it.");
            }

            await LinkSharedPackagesAsync(venvDir, ct);
        }
    }

    /// <summary>
    /// Adds the shared packages folder to the venv through a .pth file. It uses site.addsitedir
    /// rather than a plain path, because only that also processes .pth files inside the folder,
    /// which packages like pywin32 depend on.
    /// </summary>
    private async Task LinkSharedPackagesAsync(string venvDir, CancellationToken ct)
    {
        var sitePackages = OperatingSystem.IsWindows()
            ? Path.Combine(venvDir, "Lib", "site-packages")
            : Directory.GetDirectories(Path.Combine(venvDir, "lib"), "python*").Select(d => Path.Combine(d, "site-packages")).First();

        Directory.CreateDirectory(SharedPackagesPath);
        var pthFile = Path.Combine(sitePackages, "pythonenvsharp_shared.pth");
        var content = BuildSharedPackagesPthLine(SharedPackagesPath) + "\n";
        var existing = File.Exists(pthFile) ? await File.ReadAllTextAsync(pthFile, ct) : null;

        // A venv from 1.x has the plain path. Switching it to addsitedir would start running .pth files
        // in the shared folder that it has never run, so a working venv keeps what it has.
        if (existing is null || (existing != content && existing.Trim() != SharedPackagesPath))
        {
            await File.WriteAllTextAsync(pthFile, content, new UTF8Encoding(false), ct);
            _logger.LogInformation("Configured to use shared packages from {Path}", SharedPackagesPath);
        }
    }

    /// <summary>Written as pure ASCII, because Python 3.11 reads .pth files in the locale encoding.</summary>
    internal static string BuildSharedPackagesPthLine(string packagesDir)
    {
        var literal = new StringBuilder();
        foreach (var rune in packagesDir.EnumerateRunes())
        {
            if (rune.Value == '\\') literal.Append('/');
            else if (rune.Value == '"') literal.Append("\\\"");
            else if (rune.Value is < 0x20 or > 0x7E) literal.Append($"\\U{rune.Value:x8}");
            else literal.Append((char)rune.Value);
        }
        return $"import site; site.addsitedir(\"{literal}\")";
    }

    private async Task SetupStandaloneEnvironmentAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_projectPath);

        using (await FileLock.AcquireAsync(Path.Combine(_projectPath, ".lock"), _logger, ct))
        {
            var pythonDir = Path.Combine(_projectPath, "python");
            if (!File.Exists(PythonLayout.PythonExecutableIn(pythonDir)))
            {
                await InstallPythonAsync(pythonDir, _projectPath, ct);
            }

            var health = await CheckHealthAsync(GetPythonExecutable(), ct);
            if (!IsHealthy(health))
                throw new PythonEnvException($"The Python in {pythonDir} is unusable: {health.Problem} Delete the folder to reinstall it.");

            AddToPath(pythonDir);
        }
    }

    private async Task InstallPythonAsync(string target, string workRoot, CancellationToken ct)
    {
        var version = _options.PythonVersion;
        var method = _options.InstallMethod;

        if (method == PythonInstallMethod.OfficialInstaller
            || (method == PythonInstallMethod.Auto && _options.CustomDistribution is null && !PythonCatalog.IsAvailable(version) && OperatingSystem.IsWindows()))
        {
            if (!OperatingSystem.IsWindows())
                throw new PythonEnvException("The python.org installer only runs on Windows.");

            await OfficialPythonInstaller.InstallAsync(version, target, workRoot, _options.DownloadTimeout, _options.DownloadStallTimeout, _options.DownloadProgress, _logger, ct);
            return;
        }

        var distribution = _options.CustomDistribution ?? PythonCatalog.Resolve(version);
        await PythonInstaller.InstallAsync(distribution, target, workRoot, _options.DownloadTimeout, _options.DownloadStallTimeout, _options.DownloadProgress, _logger, ct);
    }

    /// <param name="Version">The version it reports, or null if it can't run or lacks pip.</param>
    /// <param name="Problem">What's wrong, if it isn't healthy.</param>
    private sealed record PythonHealth(string? Version, string? Problem);

    private bool IsHealthy(PythonHealth health) => health.Version == _options.PythonVersion;

    private async Task<PythonHealth> CheckHealthAsync(string python, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(python, new[] { "-c", HealthScript }, _projectPath, ct);
        if (result.ExitCode != 0)
            return new PythonHealth(null, $"it failed its health check. {Tail(result.StandardError)}");

        var version = result.StandardOutput.Trim();
        return new PythonHealth(version, version == _options.PythonVersion
            ? null
            : $"it has version {version}, but {_options.PythonVersion} was requested.");
    }

    /// <summary>
    /// Installs packages, trying each mirror in order. Throws <see cref="PythonEnvException"/> if every mirror fails.
    /// </summary>
    /// <param name="packages">Anything pip accepts as a requirement, such as "numpy" or "numpy==1.26.4".</param>
    /// <param name="mirrors">Package indexes to try. A blank URL means pip's default index.</param>
    /// <param name="cancellationToken">Stops pip, including any running build.</param>
    public async Task InstallPackagesAsync(
        IEnumerable<string> packages,
        IEnumerable<(string Name, string Url)>? mirrors = null,
        CancellationToken cancellationToken = default)
    {
        var pkgList = packages.ToList();
        foreach (var package in pkgList) ValidateRequirement(package);

        var mirrorList = mirrors?.ToList() ?? GetDefaultMirrors();
        if (mirrorList.Count == 0) mirrorList = GetDefaultMirrors();
        foreach (var (name, url) in mirrorList) ValidateMirror(name, url);

        if (pkgList.Count == 0) return;
        await InitializeAsync(cancellationToken);

        var lockPath = _options.UseSharedEnvironment
            ? Path.Combine(_sharedPath, ".packages.lock")
            : Path.Combine(_projectPath, ".lock");

        using (await FileLock.AcquireAsync(lockPath, _logger, cancellationToken))
        {
            var state = await CheckRequirementsAsync(pkgList, cancellationToken);
            var missing = pkgList.Where(p => !state[p]).ToList();
            if (missing.Count == 0)
            {
                _logger.LogInformation("Already installed: {Packages}", string.Join(", ", pkgList));
                return;
            }

            _logger.LogInformation("Installing: {Packages}", string.Join(", ", missing));
            await InstallFromMirrorsAsync(missing, mirrorList, cancellationToken);
        }
    }

    private async Task InstallFromMirrorsAsync(List<string> packages, List<(string Name, string Url)> mirrors, CancellationToken ct)
    {
        var python = _options.UseSharedEnvironment ? SharedPythonPath : GetPythonExecutable();

        // Shared installs go to a staging folder first, so a failed install changes nothing.
        var staging = _options.UseSharedEnvironment ? Path.Combine(_sharedPath, $".staging-{Guid.NewGuid():N}") : null;

        PythonRunResult? last = null;
        var failedMirrors = new List<string>();

        try
        {
            foreach (var (name, url) in mirrors)
            {
                var args = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--timeout", "60", "--retries", "3" };
                if (staging is not null) args.AddRange(new[] { "--target", staging });
                if (!string.IsNullOrEmpty(url)) args.AddRange(new[] { "--index-url", url });
                args.AddRange(_options.ExtraPipArguments);
                args.AddRange(packages);

                _logger.LogInformation("Trying package index {Name}", name);
                last = await RunLoggedAsync(python, args, ct);
                if (last.ExitCode == 0)
                {
                    if (staging is not null) await MergeStagedPackagesAsync(python, staging, packages, ct);
                    _logger.LogInformation("Installed from {Name}", name);
                    return;
                }

                failedMirrors.Add(name);
                _logger.LogWarning("Install from {Name} failed with exit code {ExitCode}", name, last.ExitCode);
            }

            throw Failure($"Couldn't install {string.Join(", ", packages)} from any package index ({string.Join(", ", failedMirrors)}).", last!);
        }
        finally
        {
            if (staging is not null) TryDeleteFolder(staging);
        }
    }

    private async Task MergeStagedPackagesAsync(string python, string staging, List<string> requested, CancellationToken ct)
    {
        Directory.CreateDirectory(SharedPackagesPath);
        var args = new List<string> { "-c", MergeStagedPackagesScript, staging, SharedPackagesPath };
        args.AddRange(requested);

        var merge = await RunLoggedAsync(python, args, ct);
        if (merge.ExitCode != 0) throw Failure("Couldn't move installed packages into the shared folder.", merge);
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Couldn't remove {Folder}: {Reason}", folder, ex.Message);
        }
    }

    /// <summary>Checks whether a package, or a requirement such as "numpy>=2", is satisfied.</summary>
    public async Task<bool> CheckPackageInstalledAsync(string packageName, CancellationToken cancellationToken = default)
    {
        ValidateRequirement(packageName);
        await InitializeAsync(cancellationToken);
        var state = await CheckRequirementsAsync(new[] { packageName }, cancellationToken);
        return state[packageName];
    }

    private async Task<Dictionary<string, bool>> CheckRequirementsAsync(IReadOnlyCollection<string> requirements, CancellationToken ct)
    {
        var args = new List<string> { "-c", CheckScript, _options.UseSharedEnvironment ? SharedPackagesPath : "" };
        args.AddRange(requirements);

        var result = await ProcessRunner.RunAsync(GetPythonExecutable(), args, _projectPath, ct);
        if (result.ExitCode != 0) throw Failure("Couldn't check installed packages.", result);

        var lastLine = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return JsonSerializer.Deserialize<Dictionary<string, bool>>(lastLine ?? "")
            ?? throw new PythonEnvException("Couldn't read the package check result.");
    }

    /// <summary>
    /// Runs Python to completion and returns its exit code and output. A non-zero exit code doesn't throw.
    /// </summary>
    public async Task<PythonRunResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await ProcessRunner.RunAsync(GetPythonExecutable(), arguments, _projectPath, cancellationToken);
    }

    /// <summary>
    /// No longer does anything. Named pipes don't need pywin32: Python can open them with open().
    /// </summary>
    [Obsolete("Named pipes don't need pywin32. To use pywin32 itself, install it with InstallPackagesAsync.")]
    public Task EnsureNamedPipeSupportAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public string GetPythonExecutable()
    {
        if (_options.UseSharedEnvironment)
        {
            var venvPython = PythonLayout.VenvPython(Path.Combine(_projectPath, "venv"));
            return File.Exists(venvPython) ? venvPython : SharedPythonPath;
        }

        return PythonLayout.PythonExecutableIn(Path.Combine(_projectPath, "python"));
    }

    private static List<(string Name, string Url)> GetDefaultMirrors() => new()
    {
        ("PyPI", ""),
        ("Tsinghua", "https://pypi.tuna.tsinghua.edu.cn/simple"),
        ("Aliyun", "https://mirrors.aliyun.com/pypi/simple"),
        ("USTC", "https://pypi.mirrors.ustc.edu.cn/simple"),
    };

    private static string DefaultProjectName()
    {
        var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return name is not null && IsValidProjectName(name) ? name : "default";
    }

    private static bool IsValidProjectName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..")
        && name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void ValidateRequirement(string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
            throw new ArgumentException("Package names can't be blank.", nameof(requirement));
        if (requirement.TrimStart().StartsWith('-'))
            throw new ArgumentException($"'{requirement}' looks like a pip option. Use PythonManagerOptions.ExtraPipArguments for those.", nameof(requirement));
    }

    private static void ValidateMirror(string name, string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"Mirror '{name}' has an invalid URL: '{url}'.", nameof(url));
    }

    private Task<PythonRunResult> RunLoggedAsync(string exe, IEnumerable<string> args, CancellationToken ct) =>
        ProcessRunner.RunAsync(exe, args, _projectPath, ct,
            line => _logger.LogInformation("{Line}", line),
            line => _logger.LogWarning("{Line}", line));

    private static PythonEnvException Failure(string message, PythonRunResult result) =>
        new($"{message} Exit code {result.ExitCode}. {Tail(result.StandardError)}", result.ExitCode, result.StandardError);

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : "..." + trimmed[^2000..];
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lock.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
