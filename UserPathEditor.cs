using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PythonEnvSharp;

/// <summary>Adds a Python installation to a PATH value, leaving every existing entry as written.</summary>
internal static class UserPathEditor
{
    private const string ValueName = "Path";

    /// <summary>
    /// Returns the PATH value with the installation's folders at the front, or null if nothing needs
    /// to change. A 1.x install's "install" folder on PATH counts as already present, so a machine
    /// that upgrades keeps resolving "python" to the same interpreter.
    /// </summary>
    public static string? Prepend(string? currentPath, string home)
    {
        var existing = (currentPath ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToHashSet(EntryComparer);

        var legacyInstall = Normalize(Path.Combine(home, "install"));
        if (existing.Any(entry => IsSameOrInside(entry, legacyInstall))) return null;

        var missing = FoldersFor(home).Where(folder => !existing.Contains(Normalize(folder))).ToList();
        if (missing.Count == 0) return null;

        var rest = (currentPath ?? "").TrimStart(Path.PathSeparator);
        return string.Join(Path.PathSeparator, rest.Length == 0 ? missing : missing.Append(rest));
    }

    /// <summary>
    /// Updates the user's PATH in the registry and tells running programs about it. This goes through
    /// the registry directly because Environment.SetEnvironmentVariable would expand entries like
    /// %USERPROFILE%\bin and store them as fixed paths.
    /// </summary>
    /// <returns>True if the value changed.</returns>
    [SupportedOSPlatform("windows")]
    public static bool AddToRegistry(string subKey, string home)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);

        var raw = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = raw is null ? RegistryValueKind.ExpandString : key.GetValueKind(ValueName);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return false;

        var updated = Prepend(raw as string, home);
        if (updated is null) return false;

        key.SetValue(ValueName, updated, kind);
        BroadcastEnvironmentChange();
        return true;
    }

    private static IEnumerable<string> FoldersFor(string home) => OperatingSystem.IsWindows()
        ? new[] { Path.Combine(home, "Scripts"), home }
        : new[] { Path.Combine(home, "bin") };

    private static StringComparer EntryComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Normalize(string entry)
    {
        var expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        var trimmed = expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? expanded : trimmed;
    }

    private static bool IsSameOrInside(string entry, string folder) =>
        EntryComparer.Equals(entry, folder)
        || entry.StartsWith(folder + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange()
    {
        const int HwndBroadcast = 0xFFFF;
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 2000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
