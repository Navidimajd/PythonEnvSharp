using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Shared Environment Demo ===\n");

// Create two projects that share the same Python installation
Console.WriteLine("Creating Project A...");
var projectA = PythonManager.ForProject("demo_project_a");
await projectA.InitializeAsync();
Console.WriteLine($"  Python: {projectA.PythonExecutable}\n");

Console.WriteLine("Creating Project B...");
var projectB = PythonManager.ForProject("demo_project_b");
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
Console.WriteLine("\nRunning script from Project A:");
{
    var scriptPath = Path.Combine(AppContext.BaseDirectory, "test_script.py");
    await using var process = new PythonProcess();
    await process.StartAsync(projectA.PythonExecutable, scriptPath);
    var result = await process.SendReceiveAsync("{\"method\":\"info\"}");
    Console.WriteLine($"  {result}");
    await process.SendAsync("__EXIT__");
}

Console.WriteLine("\nRunning script from Project B:");
{
    var scriptPath = Path.Combine(AppContext.BaseDirectory, "test_script.py");
    await using var process = new PythonProcess();
    await process.StartAsync(projectB.PythonExecutable, scriptPath);
    var result = await process.SendReceiveAsync("{\"method\":\"info\"}");
    Console.WriteLine($"  {result}");
    await process.SendAsync("__EXIT__");
}

Console.WriteLine("\nDone! Both projects share the same Python installation.");
