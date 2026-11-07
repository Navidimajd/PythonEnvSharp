namespace PythonEnvSharp;

public sealed class PythonManagerOptions
{
    public const string DefaultPythonVersion = "3.11.9";

    /// <summary>
    /// Project-specific environment path. If null, a folder named after the entry assembly is used.
    /// </summary>
    public string? EnvironmentPath { get; set; }

    /// <summary>
    /// Shared Python/packages directory. All projects share this.
    /// Default: %LOCALAPPDATA%\PythonEnvSharp\shared (~/.local/share/PythonEnvSharp/shared on Linux),
    /// or $PYTHONENVSHARP_HOME/shared when that variable is set.
    /// </summary>
    public string? SharedPath { get; set; }

    /// <summary>
    /// Python version to use, as major.minor.patch. See <see cref="InstallMethod"/> for how it's installed.
    /// </summary>
    public string PythonVersion { get; set; } = DefaultPythonVersion;

    /// <summary>
    /// Moves on to the next download source if no data arrives for this long. A slow download that keeps
    /// making progress is never interrupted, and an interrupted one resumes. Default: 60 seconds.
    /// </summary>
    public TimeSpan DownloadStallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Optional limit on the total time for one download attempt from one source. Default: no limit.
    /// </summary>
    public TimeSpan DownloadTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    [Obsolete("Has no effect. Shared environments always use a venv, because that is how shared packages reach Python.")]
    public bool AutoCreateVenv { get; set; } = true;

    /// <summary>
    /// If true, uses a shared Python installation and shared packages.
    /// Each project gets a lightweight venv that adds the shared packages folder to its path.
    /// </summary>
    public bool UseSharedEnvironment { get; set; } = true;

    /// <summary>
    /// If true (the default), InitializeAsync puts this Python first on PATH for libraries and tools
    /// that look for "python" there. On Windows this changes the user's PATH permanently, and adds the
    /// entries back on each initialization if they've been removed. On every platform it also changes
    /// PATH for the current process. Linux and macOS have no user PATH to edit, so nothing persists there.
    /// </summary>
    public bool AddToUserPath { get; set; } = true;

    /// <summary>Registry key under HKEY_CURRENT_USER that holds the user's PATH. Tests point this elsewhere.</summary>
    internal string UserEnvironmentKey { get; set; } = "Environment";

    /// <summary>How Python gets installed when it's missing.</summary>
    public PythonInstallMethod InstallMethod { get; set; } = PythonInstallMethod.Auto;

    /// <summary>Archive to install instead of the built-in catalog entry for <see cref="PythonVersion"/>.</summary>
    public PythonDistribution? CustomDistribution { get; set; }

    /// <summary>Receives progress while Python downloads.</summary>
    public IProgress<PythonDownloadProgress>? DownloadProgress { get; set; }

    /// <summary>
    /// Extra arguments for every "pip install", such as "--extra-index-url" and its URL.
    /// Package names passed to InstallPackagesAsync can't carry pip options.
    /// </summary>
    public IList<string> ExtraPipArguments { get; set; } = new List<string>();

    public string GetSharedPythonPath() => PythonLayout.SharedPythonExecutable(ResolvedSharedRoot, PythonVersion);

    public string GetSharedPackagesPath() => PythonLayout.SharedPackagesDir(ResolvedSharedRoot, PythonVersion);

    private string ResolvedSharedRoot => SharedPath ?? PythonLayout.DefaultSharedRoot;

    internal PythonManagerOptions Clone()
    {
        var copy = (PythonManagerOptions)MemberwiseClone();
        copy.ExtraPipArguments = new List<string>(ExtraPipArguments);
        return copy;
    }
}
