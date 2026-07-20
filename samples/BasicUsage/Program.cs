using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Basic Usage ===\n");

// Create a Python manager
await using var manager = new PythonManager();

// Auto-download Python, create venv, install pip
Console.WriteLine("Initializing Python environment...");
await manager.InitializeAsync();
Console.WriteLine($"Python: {manager.PythonExecutable}\n");

// Install packages
Console.WriteLine("Installing packages...");
await manager.InstallPackagesAsync(new[] { "numpy", "requests" });

// Check if installed
var hasNumpy = await manager.CheckPackageInstalledAsync("numpy");
Console.WriteLine($"numpy installed: {hasNumpy}\n");

// Run a Python script
Console.WriteLine("Running Python script...");
await using var process = new PythonProcess();
await process.StartAsync(manager.PythonExecutable, Path.Combine(AppContext.BaseDirectory, "test.py"));
var result = await process.SendReceiveAsync("{\"method\":\"run\",\"params\":{\"code\":\"import numpy as np; print(np.__version__)\"}}");
Console.WriteLine($"Output: {result}");
await process.SendAsync("__EXIT__");

Console.WriteLine("\nDone!");
