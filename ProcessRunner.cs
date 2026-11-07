using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PythonEnvSharp;

/// <summary>Starts processes, and runs them to completion reading stdout and stderr at the same time.</summary>
internal static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

        // Without this, Python uses the locale encoding on its pipes and corrupts non-ASCII text.
        psi.Environment["PYTHONUTF8"] = "1";

        AddInterpreterToPath(psi, exe);
        return psi;
    }

    /// <summary>
    /// Lets anything the script starts find the same Python by name, as an activated venv would.
    /// </summary>
    private static void AddInterpreterToPath(ProcessStartInfo psi, string exe)
    {
        var directory = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(directory)) return;

        var folders = new List<string> { Path.GetFullPath(directory) };
        var scripts = Path.Combine(folders[0], "Scripts");
        if (OperatingSystem.IsWindows() && Directory.Exists(scripts)) folders.Add(scripts);

        if (psi.Environment.TryGetValue("PATH", out var current) && !string.IsNullOrEmpty(current)) folders.Add(current);
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, folders);
    }

    public static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) ?? throw new PythonEnvException($"Failed to start {psi.FileName}");
        }
        catch (Win32Exception ex)
        {
            throw new PythonEnvException($"Failed to start {psi.FileName}: {ex.Message}", ex);
        }
    }

    public static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // already exited
        catch (Win32Exception) { } // exiting, or not ours to kill
    }

    public static async Task<PythonRunResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        string workingDirectory,
        CancellationToken ct,
        Action<string>? onOutput = null,
        Action<string>? onError = null)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(CreateStartInfo(exe, args, workingDirectory));
        using var killOnCancel = ct.Register(() => TryKillTree(process));

        var stdout = Pump(process.StandardOutput, onOutput);
        var stderr = Pump(process.StandardError, onError);
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(CancellationToken.None);

        ct.ThrowIfCancellationRequested();
        return new PythonRunResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static async Task<string> Pump(StreamReader reader, Action<string>? onLine)
    {
        var captured = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            captured.AppendLine(line);
            onLine?.Invoke(line);
        }
        return captured.ToString();
    }
}
