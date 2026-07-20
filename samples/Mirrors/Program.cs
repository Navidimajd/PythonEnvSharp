using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Mirror Fallback Demo ===\n");

// Custom mirrors for faster downloads in China
var mirrors = new (string Name, string Url)[]
{
    ("PyPI (default)", ""),
    ("Tsinghua (China)", "https://pypi.tuna.tsinghua.edu.cn/simple"),
    ("Aliyun (China)", "https://mirrors.aliyun.com/pypi/simple"),
    ("USTC (China)", "https://pypi.mirrors.ustc.edu.cn/simple"),
};

// Create manager with custom mirrors
await using var manager = new PythonManager();
await manager.InitializeAsync();

Console.WriteLine("Installing packages with mirror fallback...\n");
await manager.InstallPackagesAsync(
    new[] { "pandas", "scikit-learn" },
    mirrors: mirrors);

Console.WriteLine("\nVerifying installation:");
var hasPandas = await manager.CheckPackageInstalledAsync("pandas");
var hasSklearn = await manager.CheckPackageInstalledAsync("scikit-learn");
Console.WriteLine($"  pandas: {hasPandas}");
Console.WriteLine($"  scikit-learn: {hasSklearn}");

Console.WriteLine("\nDone!");
