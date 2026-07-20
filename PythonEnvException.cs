namespace PythonEnvSharp;

public class PythonEnvException : Exception
{
    public PythonEnvException(string message) : base(message) { }
    public PythonEnvException(string message, Exception inner) : base(message, inner) { }
}
