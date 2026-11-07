using Microsoft.Extensions.Logging;
using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Shared Environment Demo ===\n");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger("PythonEnvSharp");

// Create two projects that share the same Python installation
Console.WriteLine("Creating Project A...");
await using var projectA = PythonManager.ForProject("demo_project_a", logger: logger);
await projectA.InitializeAsync();
Console.WriteLine($"  Python: {projectA.PythonExecutable}\n");

Console.WriteLine("Creating Project B...");
await using var projectB = PythonManager.ForProject("demo_project_b", logger: logger);
await projectB.InitializeAsync();
Console.WriteLine($"  Python: {projectB.PythonExecutable}\n");

// Install packages to shared environment (available to both projects)
Console.WriteLine("Installing 'requests' to shared environment...");
await projectA.InstallPackagesAsync(new[] { "requests" });

// Verify both projects can see the package
Console.WriteLine("\nVerifying packages:");
var aHas = await projectA.CheckPackageInstalledAsync("requests");
var bHas = await projectB.CheckPackageInstalledAsync("requests");
Console.WriteLine($"  Project A sees requests: {aHas}");
Console.WriteLine($"  Project B sees requests: {bHas}");

// Run a script from each project
var scriptPath = Path.Combine(AppContext.BaseDirectory, "test_script.py");

Console.WriteLine("\nRunning script from Project A:");
await using (var process = new PythonProcess())
{
    await process.StartAsync(projectA.PythonExecutable, scriptPath);
    Console.WriteLine($"  {await process.SendReceiveAsync("{\"method\":\"info\"}")}");
}

Console.WriteLine("\nRunning script from Project B:");
await using (var process = new PythonProcess())
{
    await process.StartAsync(projectB.PythonExecutable, scriptPath);
    Console.WriteLine($"  {await process.SendReceiveAsync("{\"method\":\"info\"}")}");
}

Console.WriteLine("\nDone! Both projects share the same Python installation.");
