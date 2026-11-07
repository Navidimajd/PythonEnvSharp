using System.Diagnostics;

namespace PythonEnvSharp;

/// <summary>
/// Where things live on disk. New installs use versioned folders (python-3.11.9, packages-3.11),
/// so different versions can't collide. A pre-2.0 "python" folder is reused when its version matches.
/// </summary>
internal static class PythonLayout
{
    public static string BaseDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("PYTHONENVSHARP_HOME");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PythonEnvSharp")
                : overridden;
        }
    }

    public static string DefaultSharedRoot => Path.Combine(BaseDir, "shared");

    public static string ProjectDir(string name) => Path.Combine(BaseDir, "projects", name);

    public static string PythonExecutableIn(string home) =>
        OperatingSystem.IsWindows() ? Path.Combine(home, "python.exe") : Path.Combine(home, "bin", "python3");

    public static string VenvPython(string venvDir) =>
        OperatingSystem.IsWindows() ? Path.Combine(venvDir, "Scripts", "python.exe") : Path.Combine(venvDir, "bin", "python");

    public static string SharedHome(string root, string version)
    {
        var versioned = Path.Combine(root, $"python-{version}");
        if (File.Exists(PythonExecutableIn(versioned))) return versioned;
        return IsMatchingLegacyInstall(root, version) ? LegacyHome(root) : versioned;
    }

    public static string SharedPythonExecutable(string root, string version) =>
        PythonExecutableIn(SharedHome(root, version));

    public static string SharedPackagesDir(string root, string version) =>
        SharedHome(root, version) == LegacyHome(root)
            ? Path.Combine(root, "packages")
            : Path.Combine(root, $"packages-{MajorMinor(version)}");

    public static string MajorMinor(string version) => string.Join('.', version.Split('.').Take(2));

    private static string LegacyHome(string root) => Path.Combine(root, "python");

    private static bool IsMatchingLegacyInstall(string root, string version)
    {
        // Versions before 2.0 only installed on Windows, where python.exe carries its version.
        if (!OperatingSystem.IsWindows()) return false;
        var exe = PythonExecutableIn(LegacyHome(root));
        return File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).ProductVersion == version;
    }
}
