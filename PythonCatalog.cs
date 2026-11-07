using System.Runtime.InteropServices;

namespace PythonEnvSharp;

/// <summary>
/// Pinned python-build-standalone builds. The hashes come from each release's SHA256SUMS file and
/// are what make the mirror safe to use: a file with the wrong hash is rejected wherever it came from.
/// </summary>
internal static class PythonCatalog
{
    private const string WinX64 = "x86_64-pc-windows-msvc";
    private const string WinArm64 = "aarch64-pc-windows-msvc";
    private const string LinuxX64 = "x86_64-unknown-linux-gnu";
    private const string LinuxArm64 = "aarch64-unknown-linux-gnu";
    private const string MacX64 = "x86_64-apple-darwin";
    private const string MacArm64 = "aarch64-apple-darwin";

    private static readonly Dictionary<string, (string Tag, Dictionary<string, string> Hashes)> Builds = new()
    {
        ["3.11.9"] = ("20240415", new()
        {
            [WinX64] = "368474c69f476e7de4adaf50b61d9fcf6ec8b4db88cc43c5f71c860b3cd29c69",
            [LinuxX64] = "78b1c16a9fd032997ba92a60f46a64f795cd18ff335659dfdf6096df277b24d5",
            [LinuxArm64] = "b3a7199ac2615d75fb906e5ba556432efcf24baf8651fc70370d9f052d4069ee",
            [MacX64] = "9afd734f63a23783cf0257bef25c9231ffc80e7747486dc54cf72f325213fd15",
            [MacArm64] = "7af7058f7c268b4d87ed7e08c2c7844ef8460863b3e679db3afdce8bb1eedfae",
        }),
        ["3.11.18"] = ("20261026", new()
        {
            [WinX64] = "75cb501dc11c7b55c6b447c58e4910bce5b1f609f1017328b4f3a858dfaf5428",
            [WinArm64] = "9ee1a7cae577c62680659d1d8a45f6bb199c3b2692fe7f05480be20be5df2317",
            [LinuxX64] = "a87ee63646c8f8438b7af3954b2135ed2af91d033c631f12a8f20af903046d06",
            [LinuxArm64] = "a344c887b04a309f292cac1ef7c7a85e39e11932ec301d635258c569d830861b",
            [MacX64] = "a85474bcc78a98ca6b0070d8d615b0494b912b960239e6f18cc12d648f2dc270",
            [MacArm64] = "cba3455e18516fbe8668cff2820c9b6a53447c552d612443c2b2fb60d390a908",
        }),
        ["3.12.16"] = ("20261026", new()
        {
            [WinX64] = "590dfcbe3a34c07301e6332e13e2188b98655cd072b08afa45a7998dc4c22bc5",
            [WinArm64] = "ad585318bfe47980cbaa90be12a65319c5b4eec7472655428528068e710a6c9a",
            [LinuxX64] = "6a79e922e50006b601f29e688407659da1c2b86ebcd4e9c7254cf234be8b89de",
            [LinuxArm64] = "a945427411dc6851a1393533b74a4a1ac434321ae397f2ec69897a9b4fde032b",
            [MacX64] = "a962e47df522889eaa7ff0d6a56203f1079f68aabb65ffdbe8455f362bc769f3",
            [MacArm64] = "fbf4afef53c2a6b72bb741bc06d5671a9dfaf87bd84e5aa73ac9b3d0d589f14e",
        }),
        ["3.13.18"] = ("20261026", new()
        {
            [WinX64] = "ebe90cc1dce2dd2d7afa1f0aa9b6c269c7c0174aeabdc6a801381602b7283424",
            [WinArm64] = "13f8bb6ff296a826dd903ea732255ca02fc3a4db4287c2b0ebe0781360b6028d",
            [LinuxX64] = "126343c54daff4068efc1ac269518d0cc202d57857794948103dd1454e8881be",
            [LinuxArm64] = "b48451865bde9a39e2f3e71d7fdcbf1f32615f50f24d78dc50d04ede6501cf81",
            [MacX64] = "708be9e7df297f68bdae589ea9e62a621db5abe7708f79e851c41410347452d1",
            [MacArm64] = "e5af2fde6a937a0e0c795a0c595c4efb70578d57463a2657c819992b9b9acbac",
        }),
    };

    public static bool IsAvailable(string version) =>
        Builds.TryGetValue(version, out var build) && build.Hashes.ContainsKey(CurrentPlatform());

    public static PythonDistribution Resolve(string version)
    {
        if (!Builds.TryGetValue(version, out var build))
        {
            throw new PythonEnvException(
                $"Python {version} isn't in the built-in catalog ({string.Join(", ", Builds.Keys)}). " +
                "Set PythonManagerOptions.CustomDistribution to install it from your own URL and hash.");
        }

        var platform = CurrentPlatform();
        if (!build.Hashes.TryGetValue(platform, out var hash))
        {
            throw new PythonEnvException($"The built-in catalog has no Python {version} build for {platform}.");
        }

        var file = $"cpython-{version}+{build.Tag}-{platform}-install_only.tar.gz";
        return new PythonDistribution(hash, new[]
        {
            $"https://registry.npmmirror.com/-/binary/python-build-standalone/{build.Tag}/{file}",
            $"https://github.com/astral-sh/python-build-standalone/releases/download/{build.Tag}/{file}",
        });
    }

    private static string CurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (arch is not (Architecture.X64 or Architecture.Arm64))
            throw new PythonEnvException($"Unsupported architecture: {arch}.");
        var isX64 = arch == Architecture.X64;

        if (OperatingSystem.IsWindows()) return isX64 ? WinX64 : WinArm64;
        if (OperatingSystem.IsLinux()) return isX64 ? LinuxX64 : LinuxArm64;
        if (OperatingSystem.IsMacOS()) return isX64 ? MacX64 : MacArm64;
        throw new PythonEnvException("Unsupported operating system.");
    }
}
