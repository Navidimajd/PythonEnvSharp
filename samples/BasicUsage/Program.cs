using Microsoft.Extensions.Logging;
using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Basic Usage ===\n");

// The library reports progress through ILogger, so a console logger shows it here
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger("PythonEnvSharp");

// Create a Python manager
await using var manager = new PythonManager(logger: logger);

// Auto-download Python and create a venv
Console.WriteLine("Initializing Python environment...");
await manager.InitializeAsync();
Console.WriteLine($"Python: {manager.PythonExecutable}\n");

// Install packages. This throws PythonEnvException if every package index fails.
Console.WriteLine("Installing packages...");
await manager.InstallPackagesAsync(new[] { "numpy", "requests" });

// Check if installed
var hasNumpy = await manager.CheckPackageInstalledAsync("numpy");
Console.WriteLine($"numpy installed: {hasNumpy}\n");

// Run Python to completion
var version = await manager.RunAsync(new[] { "-c", "import numpy; print(numpy.__version__)" });
Console.WriteLine($"numpy version: {version.StandardOutput.Trim()}\n");

// Talk to a long-running script
Console.WriteLine("Running Python script...");
await using var process = new PythonProcess();
await process.StartAsync(manager.PythonExecutable, Path.Combine(AppContext.BaseDirectory, "test.py"));
var result = await process.SendReceiveAsync("{\"method\":\"run\",\"params\":{\"code\":\"import numpy as np; print(np.__version__)\"}}");
Console.WriteLine($"Output: {result}");

Console.WriteLine("\nDone!");
