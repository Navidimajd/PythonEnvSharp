# PythonEnvSharp

Auto-download and manage Python environments, pip packages, and virtual environments for C# projects.

## Features

- **Auto-download Python** — Downloads Python 3.11 if not found (with mirror fallback)
- **Shared environments** — Multiple projects share one Python installation
- **Per-project venvs** — Each project gets its own virtual environment
- **Mirror support** — Tsinghua, Aliyun, USTC mirrors for fast downloads in China
- **Resume downloads** — Interrupted downloads resume from where they left off
- **Progress reporting** — Real-time download progress
- **Named pipes** — Bidirectional C#↔Python communication
- **stdin/stdout** — Simple process communication

## Installation

```bash
dotnet add package PythonEnvSharp
```

## Quick Start

### Basic Usage

```csharp
using PythonEnvSharp;

// Auto-download Python, create venv, install pip
using var manager = new PythonManager();
await manager.InitializeAsync();

// Install packages with mirror fallback
await manager.InstallPackagesAsync(new[] { "numpy", "pandas" });

// Run Python commands
var result = await manager.RunCommandAsync("-c \"import numpy; print(numpy.__version__)\"");

// Check if package installed
var hasNumpy = await manager.CheckPackageInstalledAsync("numpy");
```

### Shared Environments

Save disk space by sharing Python across multiple projects:

```csharp
// Project A
var projectA = PythonManager.ForProject("my_app_a");
await projectA.InitializeAsync();
await projectA.InstallPackagesAsync(new[] { "torch" });

// Project B — reuses same Python, sees same packages
var projectB = PythonManager.ForProject("my_app_b");
await projectB.InitializeAsync();

var hasTorch = await projectB.CheckPackageInstalledAsync("torch"); // true!
```

### Named Pipes Communication

```csharp
using var process = new PythonProcess();
await process.StartAsync(pythonExe, "my_script.py");

// Send JSON, receive JSON
var result = await process.SendReceiveAsync("{\"method\":\"add\",\"params\":{\"a\":1,\"b\":2}}");
Console.WriteLine(result); // {"status":"ok","result":3}

await process.SendAsync("__EXIT__");
```

### Mirror Fallback

```csharp
var mirrors = new (string Name, string Url)[]
{
    ("PyPI", ""),
    ("Tsinghua", "https://pypi.tuna.tsinghua.edu.cn/simple"),
    ("Aliyun", "https://mirrors.aliyun.com/pypi/simple"),
};

await manager.InstallPackagesAsync(new[] { "torch", "numpy" }, mirrors: mirrors);
```

## Directory Structure

```
~/.PythonEnvSharp/
├── shared/
│   ├── python/          # Shared Python (downloaded once)
│   └── packages/        # Shared packages (installed once)
└── projects/
    ├── my_app_a/
    │   └── venv/        # Project-specific venv
    └── my_app_b/
        └── venv/        # References shared packages
```

## API Reference

### PythonManager

| Method | Description |
|--------|-------------|
| `InitializeAsync()` | Download Python, create venv, install pip |
| `InstallPackagesAsync(packages, mirrors)` | Install packages with mirror fallback |
| `CheckPackageInstalledAsync(package)` | Check if package is installed |
| `EnsureNamedPipeSupportAsync()` | Install pywin32 for named pipes |
| `ForProject(name)` | Create named project environment |

### PythonProcess

| Method | Description |
|--------|-------------|
| `StartAsync(python, script)` | Start Python process |
| `SendAsync(message)` | Send message to Python |
| `ReceiveAsync()` | Receive message from Python |
| `SendReceiveAsync(message)` | Send and receive |

### PythonPipe

| Method | Description |
|--------|-------------|
| `StartAsync(python, script)` | Start with named pipes |
| `SendAsync(message)` | Send via pipe |
| `ReceiveAsync()` | Receive via pipe |

## Samples

See `samples/` directory for complete examples:

- **BasicUsage** — Simple Python environment setup
- **NamedPipes** — C#↔Python communication
- **SharedEnvironment** — Shared Python across projects
- **Mirrors** — Custom pip mirrors

## License

MIT
