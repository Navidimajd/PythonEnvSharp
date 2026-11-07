namespace PythonEnvSharp;

public class PythonEnvException : Exception
{
    public PythonEnvException(string message) : base(message) { }
    public PythonEnvException(string message, Exception inner) : base(message, inner) { }

    public PythonEnvException(string message, int exitCode, string? standardError) : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>Exit code of the failed Python or pip process, when there was one.</summary>
    public int? ExitCode { get; }

    /// <summary>Captured stderr of the failed process, when there was one.</summary>
    public string? StandardError { get; }
}
