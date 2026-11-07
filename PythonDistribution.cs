namespace PythonEnvSharp;

/// <summary>
/// A relocatable Python archive to install: a .tar.gz laid out like python-build-standalone
/// "install_only" builds, with everything under a top-level "python" folder.
/// </summary>
/// <param name="Sha256">Expected SHA-256 of the archive, in hex. A download that doesn't match is rejected.</param>
/// <param name="Urls">Download locations, tried in order.</param>
public sealed record PythonDistribution(string Sha256, IReadOnlyList<string> Urls);

/// <summary>How Python gets installed when it's missing.</summary>
public enum PythonInstallMethod
{
    /// <summary>
    /// Uses the hash-pinned archive when the version is in the built-in catalog or
    /// <see cref="PythonManagerOptions.CustomDistribution"/> is set. Otherwise, on Windows, uses the python.org installer.
    /// </summary>
    Auto,

    /// <summary>
    /// Always uses the python.org installer, Windows only. Its signature is verified before it runs. It registers
    /// Python with Windows, under Software\Python and in installed apps, for libraries that look for it there.
    /// </summary>
    OfficialInstaller,
}

/// <summary>Progress of a Python download.</summary>
public sealed record PythonDownloadProgress(long BytesDownloaded, long? TotalBytes, string Url);

/// <summary>Result of running Python to completion.</summary>
public sealed record PythonRunResult(int ExitCode, string StandardOutput, string StandardError);
