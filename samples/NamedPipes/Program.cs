using PythonEnvSharp;

Console.WriteLine("=== PythonEnvSharp Named Pipes Demo ===\n");

// Initialize Python environment
await using var manager = new PythonManager();
await manager.InitializeAsync();
await manager.EnsureNamedPipeSupportAsync(); // Install pywin32

// Start a Python process with stdin/stdout communication
Console.WriteLine("Starting Python process...");
var scriptPath = Path.Combine(AppContext.BaseDirectory, "calculator.py");

await using var process = new PythonProcess();
await process.StartAsync(manager.PythonExecutable, scriptPath);
Console.WriteLine("Connected!\n");

// Test basic operations
Console.Write("Ping: ");
Console.WriteLine(await process.SendReceiveAsync("{\"method\":\"ping\"}"));

Console.Write("Add 2+3: ");
Console.WriteLine(await process.SendReceiveAsync("{\"method\":\"add\",\"params\":{\"a\":2,\"b\":3}}"));

Console.Write("Multiply 4*5: ");
Console.WriteLine(await process.SendReceiveAsync("{\"method\":\"multiply\",\"params\":{\"a\":4,\"b\":5}}"));

Console.Write("Square 7: ");
Console.WriteLine(await process.SendReceiveAsync("{\"method\":\"square\",\"params\":{\"n\":7}}"));

// Clean exit
await process.SendAsync("__EXIT__");
Console.WriteLine("\nDone!");
