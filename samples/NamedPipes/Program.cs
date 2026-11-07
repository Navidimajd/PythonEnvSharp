using Microsoft.Extensions.Logging;
using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Named Pipes Demo ===\n");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger("PythonEnvSharp");

// Initialize Python environment
await using var manager = new PythonManager(logger: logger);
await manager.InitializeAsync();

// Start a Python process that talks over named pipes
Console.WriteLine("Starting Python process...");
var scriptPath = Path.Combine(AppContext.BaseDirectory, "calculator.py");

await using var pipe = new PythonPipe();
await pipe.StartAsync(manager.PythonExecutable, scriptPath);
Console.WriteLine("Connected!\n");

// Test basic operations
Console.Write("Ping: ");
Console.WriteLine(await pipe.JsonRpcAsync("ping", "{}"));

Console.Write("Add 2+3: ");
Console.WriteLine(await pipe.JsonRpcAsync("add", "{\"a\":2,\"b\":3}"));

Console.Write("Multiply 4*5: ");
Console.WriteLine(await pipe.JsonRpcAsync("multiply", "{\"a\":4,\"b\":5}"));

Console.Write("Square 7: ");
Console.WriteLine(await pipe.JsonRpcAsync("square", "{\"n\":7}"));

// Disposing the pipe asks the script to exit
Console.WriteLine("\nDone!");
