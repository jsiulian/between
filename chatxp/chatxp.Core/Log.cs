namespace chatxp.core;

/// <summary>
/// Minimal logging sink for connection diagnostics — deliberately no logging
/// framework, so Core stays dependency-free and the UI decides what to do with
/// entries. Core classes call Info/Error on background threads; UI sinks must
/// marshal to the UI thread themselves.
/// </summary>
public interface ILog
{
    void Info(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Default no-op sink (used when no diagnostics are wanted, e.g. unit tests).</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    private NullLog() { }
    public void Info(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
