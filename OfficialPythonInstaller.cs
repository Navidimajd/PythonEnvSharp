using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PythonEnvSharp;

/// <summary>
/// Installs Python with the python.org installer, which covers versions outside the pinned catalog and
/// registers Python with Windows. There's no pinned hash for arbitrary versions, so the file is trusted
/// only if Windows verifies its signature and the signer is the Python Software Foundation. That also
/// makes the mirror safe. PATH is left to PythonManager, so AddToUserPath is honored.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class OfficialPythonInstaller
{
    private const string ExpectedSigner = "Python Software Foundation";

    public static async Task InstallAsync(
        string version,
        string targetDir,
        string workRoot,
        TimeSpan downloadTimeout,
        TimeSpan stallTimeout,
        IProgress<PythonDownloadProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        ThrowIfAlreadyRegistered(version, architecture);

        var file = $"python-{version}-{architecture}.exe";
        var urls = new[]
        {
            $"https://registry.npmmirror.com/-/binary/python/{version}/{file}",
            $"https://www.python.org/ftp/python/{version}/{file}",
        };

        var downloads = Path.Combine(workRoot, ".downloads");
        Directory.CreateDirectory(downloads);
        var installer = Path.Combine(downloads, file);

        await DownloadVerifiedAsync(urls, installer, version, downloadTimeout, stallTimeout, progress, logger, ct);

        logger.LogInformation("Running the python.org installer for Python {Version}", version);
        await RunInstallerAsync(installer, targetDir, version, ct);
        File.Delete(installer);
    }

    /// <summary>
    /// Windows keeps one per-user install of each minor version. Running the installer again would modify
    /// or upgrade that install, and could remove its PATH entries, so this refuses instead of touching it.
    /// </summary>
    private static void ThrowIfAlreadyRegistered(string version, string architecture)
    {
        var minor = PythonLayout.MajorMinor(version);
        var tag = architecture == "arm64" ? $"{minor}-arm64" : minor;

        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Python\PythonCore\{tag}\InstallPath");
        if (key is null) return;

        throw new PythonEnvException(
            $"Python {minor} is already installed for this user at {key.GetValue(null)}. Running the python.org installer " +
            "could change that install, so it wasn't run. Use a version in the built-in catalog with InstallMethod.Auto, " +
            "set CustomDistribution, or uninstall the existing Python from Windows Settings.");
    }

    private static async Task DownloadVerifiedAsync(
        string[] urls,
        string installer,
        string version,
        TimeSpan timeout,
        TimeSpan stallTimeout,
        IProgress<PythonDownloadProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // A partial file fails verification, so only a finished file from an earlier run is reused.
        if (File.Exists(installer) && !IsSignedByPython(installer)) File.Delete(installer);
        if (File.Exists(installer)) return;

        var partial = installer + ".partial";
        var errors = new List<string>();
        foreach (var url in urls)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(timeout);
            try
            {
                logger.LogInformation("Downloading Python from {Url}", url);
                await PythonInstaller.DownloadAsync(client, url, partial, stallTimeout, progress, logger, attempt.Token);

                if (!IsSignedByPython(partial))
                {
                    File.Delete(partial);
                    throw new InvalidDataException($"the file isn't validly signed by the {ExpectedSigner}");
                }

                File.Move(partial, installer, overwrite: true);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var reason = attempt.IsCancellationRequested ? $"timed out after {timeout}" : ex.Message;
                logger.LogWarning("Download from {Url} failed: {Reason}", url, reason);
                errors.Add($"{url}: {reason}");
            }
        }

        throw new PythonEnvException(
            $"Failed to download the Python {version} installer: {string.Join("; ", errors)}. " +
            "python.org doesn't publish installers for every patch release. " +
            "Choose another version, or set PythonManagerOptions.CustomDistribution to use your own archive.");
    }

    private static async Task RunInstallerAsync(string installer, string targetDir, string version, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installer,
            Arguments = $"/quiet InstallAllUsers=0 TargetDir=\"{targetDir}\" PrependPath=0 Shortcuts=0 AssociateFiles=0 Include_test=0 Include_pip=1 Include_launcher=0",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = ProcessRunner.Start(psi);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKillTree(process);
            throw;
        }

        // 3010 means success, with a restart recommended.
        if (process.ExitCode is not (0 or 3010))
            throw new PythonEnvException($"The Python {version} installer failed with exit code {process.ExitCode}.", process.ExitCode, null);

        if (!File.Exists(PythonLayout.PythonExecutableIn(targetDir)))
        {
            throw new PythonEnvException(
                $"The Python {version} installer finished without installing to {targetDir}. Windows allows one per-user " +
                "install of each version, so it's probably already installed somewhere else. Uninstall it from Windows " +
                "Settings or use InstallMethod.Auto with a version in the built-in catalog.");
        }
    }

    /// <summary>True if Windows trusts the file's signature and the signer is the Python Software Foundation.</summary>
    internal static bool IsSignedByPython(string path)
    {
        if (!HasTrustedSignature(path)) return false;

        try
        {
            // Still the way to read a file's Authenticode signer: the suggested replacement doesn't read signed files.
#pragma warning disable SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == ExpectedSigner;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    internal static bool HasTrustedSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // no UI
                RevocationChecks = 0,
                UnionChoice = 1, // a file
                FileInfo = fileInfoPointer,
            };

            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(-1, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }
}
