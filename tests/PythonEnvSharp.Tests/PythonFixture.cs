using Microsoft.Extensions.Logging;
using PythonEnvSharp;
using Xunit;

namespace PythonEnvSharp.Tests;

/// <summary>
/// One real Python for the whole run, kept between runs so it only downloads once.
/// Tests run one at a time because they share its packages folder.
/// </summary>
public sealed class PythonFixture : IAsyncLifetime
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PythonEnvSharpTests");

    public string SharedRoot { get; } = Path.Combine(Root, "shared");

    public string WorkDir { get; } = Path.Combine(Root, "runs", Guid.NewGuid().ToString("N")[..8]);

    public string Python { get; private set; } = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(WorkDir);

        // Packages start clean every run. The interpreter stays.
        foreach (var packages in Directory.Exists(SharedRoot) ? Directory.GetDirectories(SharedRoot, "packages-*") : Array.Empty<string>())
            Directory.Delete(packages, recursive: true);

        await using var manager = CreateManager("base");
        await manager.InitializeAsync();
        Python = manager.PythonExecutable;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(WorkDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a slow process exit; it's temp space
        return Task.CompletedTask;
    }

    public PythonManager CreateManager(string project, ILogger? logger = null, string? version = null) =>
        new(new PythonManagerOptions
        {
            SharedPath = SharedRoot,
            EnvironmentPath = Path.Combine(WorkDir, "projects", project),
            PythonVersion = version ?? PythonManagerOptions.DefaultPythonVersion,
            AddToUserPath = false, // tests must never change the real user PATH
        }, logger);

    public string WriteScript(string body)
    {
        var path = Path.Combine(WorkDir, $"script_{Guid.NewGuid():N}.py");
        File.WriteAllText(path, body);
        return path;
    }

    public string NewPath(string name) => Path.Combine(WorkDir, $"{Guid.NewGuid():N}_{name}");
}

[CollectionDefinition("Python")]
public sealed class PythonCollection : ICollectionFixture<PythonFixture> { }

internal sealed class ListLogger : ILogger
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add(formatter(state, exception));
    }
}
