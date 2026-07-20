namespace PythonEnvSharp;

public sealed class PythonManagerOptions
{
    public const string DefaultPythonVersion = "3.11.9";

    /// <summary>
    /// Project-specific environment path. If null, uses default.
    /// </summary>
    public string? EnvironmentPath { get; set; }

    /// <summary>
    /// Shared Python/packages directory. All projects share this.
    /// Default: ~/.PythonEnvSharp/shared
    /// </summary>
    public string? SharedPath { get; set; }

    public string PythonVersion { get; set; } = DefaultPythonVersion;
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public bool AutoCreateVenv { get; set; } = true;

    /// <summary>
    /// If true, uses shared Python installation and packages.
    /// Each project gets a lightweight venv that symlinks to shared packages.
    /// </summary>
    public bool UseSharedEnvironment { get; set; } = true;

    public string GetSharedPythonPath() =>
        Path.Combine(SharedPath ?? GetDefaultSharedPath(), "python", "python.exe");

    public string GetSharedPackagesPath() =>
        Path.Combine(SharedPath ?? GetDefaultSharedPath(), "packages");

    private static string GetDefaultSharedPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PythonEnvSharp", "shared");
}
