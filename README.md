# PythonEnvSharp

Auto-download and manage Python environments, pip packages, and virtual environments for C# projects, on Windows, Linux and macOS.

## Features

- **Auto-download Python** — Installs a private copy of Python 3.11 by default. It doesn't look for or use a Python already on the machine
- **Verified downloads** — Python downloads are checked against a pinned SHA-256 hash or the installer's signature before use
- **On PATH by default** — For libraries and tools that look for `python` there, with an option to turn it off
- **No installer by default** — Built-in versions unpack into a plain folder, with the python.org installer available when you need it
- **Shared environments** — Multiple projects share one Python installation and one packages folder
- **Per-project venvs** — Each project gets its own virtual environment
- **Mirror support** — Tsinghua, Aliyun and USTC fallback mirrors for pip. Python itself downloads from npmmirror first, then the official site
- **Resume downloads** — Interrupted Python downloads resume from where they left off. Slow downloads are never cut off; a source only gets replaced if no data arrives for `DownloadStallTimeout`
- **Progress reporting** — Through `ILogger` and `IProgress<PythonDownloadProgress>`
- **Named pipes** — Bidirectional C#↔Python communication that needs no extra Python packages
- **stdin/stdout** — Simple process communication

## Installation

```bash
dotnet add package PythonEnvSharp
```

Requires .NET 8 or later.

## Quick Start

### Basic Usage

```csharp
using PythonEnvSharp;

// Pass an ILogger to the constructor to see progress
await using var manager = new PythonManager();

// Download Python if needed and create a venv
await manager.InitializeAsync();

// Install packages with mirror fallback. Throws PythonEnvException if every index fails.
await manager.InstallPackagesAsync(new[] { "numpy", "pandas>=2" });

// Run Python to completion
var result = await manager.RunAsync(new[] { "-c", "import numpy; print(numpy.__version__)" });
Console.WriteLine(result.StandardOutput);

// Check if a package or version requirement is satisfied
var hasNumpy = await manager.CheckPackageInstalledAsync("numpy");
```

### Shared Environments

Save disk space by sharing Python across multiple projects:

```csharp
// Project A
await using var projectA = PythonManager.ForProject("my_app_a");
await projectA.InstallPackagesAsync(new[] { "torch" });

// Project B — reuses same Python, sees same packages
await using var projectB = PythonManager.ForProject("my_app_b");
var hasTorch = await projectB.CheckPackageInstalledAsync("torch"); // true
```

All projects see one set of shared packages, so they also share one version of each. Only the packages you name replace an existing version. Dependencies already in the shared folder are kept, even if pip would have chosen another version, and the logger reports when that happens, because a new package can need something newer than what's kept. If projects need different versions, set `UseSharedEnvironment = false`, which gives a project its own Python and packages.

### stdin/stdout Communication

The script must print `READY` once it can accept messages. `StartAsync` throws a `PythonEnvException` containing the script's stderr if the script exits first. It waits as long as the script needs, so a slow model load is fine. To limit that, set `StartupTimeout` or pass a cancellation token.

```csharp
await using var process = new PythonProcess();
await process.StartAsync(manager.PythonExecutable, "my_script.py");

// Send JSON, receive JSON
var result = await process.JsonRpcAsync("add", "{\"a\":1,\"b\":2}");
Console.WriteLine(result); // {"status":"ok","result":3}

// Disposing sends __EXIT__ and closes stdin, then kills the script if it hasn't exited after 5 seconds
```

Each message is one line, and stdout is the channel, so have the script log to stderr. `JsonRpcAsync` skips lines that aren't JSON objects, but `SendReceiveAsync` treats any line as the reply. If libraries you use might print to stdout, prefer named pipes.

### Named Pipes Communication

Stdout stays free for the script's own output. The script receives `--pipe-in NAME` and `--pipe-out NAME`, and needs no extra packages. See [samples/NamedPipes/calculator.py](samples/NamedPipes/calculator.py) for a complete script for all platforms.

```csharp
await using var pipe = new PythonPipe();
await pipe.StartAsync(manager.PythonExecutable, "calculator.py");

var result = await pipe.JsonRpcAsync("add", "{\"a\":1,\"b\":2}");
```

```python
# Windows
requests = open(rf"\\.\pipe\{args.pipe_in}", "r", encoding="utf-8", newline="\n")
replies = open(rf"\\.\pipe\{args.pipe_out}", "w", encoding="utf-8", newline="\n")
```

### Mirror Fallback

Indexes are tried in order, and a blank URL means pip's default index. Mirrors must have a valid HTTPS certificate, because TLS verification stays on.

```csharp
var mirrors = new (string Name, string Url)[]
{
    ("Tsinghua", "https://pypi.tuna.tsinghua.edu.cn/simple"),
    ("Aliyun", "https://mirrors.aliyun.com/pypi/simple"),
    ("PyPI", ""),
};

await manager.InstallPackagesAsync(new[] { "torch", "numpy" }, mirrors: mirrors);
```

### Extra pip Options

Package names can't carry pip options. Set them on the manager instead:

```csharp
var options = new PythonManagerOptions();
options.ExtraPipArguments.Add("--extra-index-url");
options.ExtraPipArguments.Add("https://download.pytorch.org/whl/cu121");

await using var manager = PythonManager.ForProject("my_app", options);
await manager.InstallPackagesAsync(new[] { "torch" });
```

### Python Versions

Versions install side by side, and each minor version has its own packages folder. How a version gets installed depends on what you ask for:

| Version | Windows | Linux and macOS |
|---------|---------|-----------------|
| **3.11.9** (the default), **3.11.18**, **3.12.16**, **3.13.18** | Archive checked against a pinned SHA-256 hash | Same |
| Anything else | python.org installer, run only if Windows verifies it's signed by the Python Software Foundation | Needs `CustomDistribution` |

3.11.9 has no Windows ARM64 archive, so on ARM64 it uses the installer too.

The archive is a plain folder that Windows doesn't know about. Some libraries find Python through the Windows registry rather than PATH. For those, use the installer, which registers Python under `Software\Python` and in installed apps:

```csharp
var options = new PythonManagerOptions
{
    PythonVersion = "3.12.10",
    InstallMethod = PythonInstallMethod.OfficialInstaller,
};
```

Python.org only publishes installers for some patch releases, and Windows allows one per-user install of each minor version. If that minor version is already installed for the user, the library refuses to run the installer, because it could change the existing install. To remove an installer-based Python, uninstall it from Windows Settings before deleting its folder. If the folder is already gone, the uninstall fails until you run the installer's repair first.

To use your own [python-build-standalone](https://github.com/astral-sh/python-build-standalone) style archive on any platform:

```csharp
var options = new PythonManagerOptions
{
    PythonVersion = "3.14.0",
    CustomDistribution = new PythonDistribution("<sha256 of the archive>", new[] { "https://example.com/python.tar.gz" }),
};
```

### Python on PATH

Some libraries and tools find Python by looking for `python` on PATH, so `InitializeAsync` puts this Python first by default:

- **On Windows**, it adds the Python folder and its `Scripts` folder to the front of the user's PATH permanently. Other entries stay exactly as written, and running programs are told about the change. Programs that are already open may still need a restart to see it.
- **In the current process**, on every platform, it updates PATH so anything you start next finds this Python. Linux and macOS have no user PATH to edit, so nothing persists there.

Being first means this Python takes priority over any other Python for everything that user runs. The entries also come back on each initialization if they're removed. To opt out, set `AddToUserPath = false`:

```csharp
var options = new PythonManagerOptions { AddToUserPath = false };
```

Separately, and regardless of this option, processes started by this library always have their own Python's folder first on PATH, so scripts can start `python` by name and get the same interpreter.

## Directory Structure

The root is `%LOCALAPPDATA%\PythonEnvSharp` on Windows and `~/.local/share/PythonEnvSharp` on Linux. Set `PYTHONENVSHARP_HOME` to use a different folder.

```
PythonEnvSharp/
├── shared/
│   ├── python-3.11.9/   # Shared Python (downloaded once)
│   └── packages-3.11/   # Shared packages (installed once)
└── projects/
    ├── my_app_a/
    │   └── venv/        # Project-specific venv
    └── my_app_b/
        └── venv/        # References shared packages
```

`new PythonManager()` uses a project folder named after your application's entry assembly.

## API Reference

### PythonManager

| Method | Description |
|--------|-------------|
| `InitializeAsync()` | Download Python if needed and create the venv |
| `InstallPackagesAsync(packages, mirrors)` | Install packages with mirror fallback; throws if all indexes fail |
| `CheckPackageInstalledAsync(package)` | Check if a package or requirement is satisfied |
| `RunAsync(arguments)` | Run Python to completion and return its exit code and output |
| `ForProject(name)` | Create named project environment |

### PythonProcess

| Member | Description |
|--------|-------------|
| `StartAsync(python, script)` | Start Python process and wait for `READY` |
| `SendAsync(message)` | Send message to Python |
| `ReceiveAsync()` | Receive message from Python |
| `SendReceiveAsync(message)` | Send and receive; safe to call concurrently |
| `JsonRpcAsync(method, jsonParams)` | Send a JSON request and receive its reply |
| `Arguments`, `Environment`, `WorkingDirectory`, `StartupTimeout` | Configure before starting |
| `StandardErrorTail` | The script's most recent stderr lines |

### PythonPipe

| Member | Description |
|--------|-------------|
| `StartAsync(python, script)` | Start with named pipes |
| `SendAsync(message)` | Send via pipe |
| `ReceiveAsync()` | Receive via pipe |
| `SendReceiveAsync(message)` | Send and receive; safe to call concurrently |
| `JsonRpcAsync(method, jsonParams)` | Send a JSON request and receive its reply |
| `ConnectTimeout` | Configure before starting |
| `StandardErrorTail` | The script's most recent stderr lines |

## Upgrading from 1.x

- **.NET 8 or later is required.** .NET 6 is no longer supported.
- **Failed installs now throw** `PythonEnvException` instead of returning normally.
- **Package names can't carry pip options.** Use `ExtraPipArguments`.
- **Output goes through `ILogger`,** not the console. Pass a logger to see progress.
- **`new PythonManager()` reuses one project folder** named after your application, instead of creating a new random one each run.
- **`--trusted-host` is gone.** A mirror with an invalid certificate now fails.
- **Existing installs keep working.** A 1.x `shared/python` folder and its `shared/packages` are reused when the Python version matches. Venvs that already work keep their existing link to the shared packages, so pywin32 imports stay broken in those until you recreate the venv. If your PATH already has the 1.x `shared\python\install` entries, they're left in place, so `python` keeps resolving to the same interpreter. Keep that folder, and the "Python 3.11.9 (64-bit)" entry in Windows Settings that installed it, for as long as you rely on those entries.
- **`EnsureNamedPipeSupportAsync` and `AutoCreateVenv` are obsolete** and do nothing.

## Samples

See `samples/` directory for complete examples:

- **BasicUsage** — Simple Python environment setup
- **NamedPipes** — C#↔Python communication over named pipes
- **SharedEnvironment** — Shared Python across projects
- **Mirrors** — Custom pip mirrors

## License

MIT
