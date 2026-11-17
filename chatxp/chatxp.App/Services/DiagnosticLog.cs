using System.Collections.Concurrent;

namespace chatxp.App.Services;

/// <summary>
/// UI-facing log sink: thread-safe ring buffer of entries. The Diagnostics tab binds to
/// Entries. Core calls Info/Error from background threads; marshalling to the UI thread
/// is done by the ViewModel when it subscribes (this class stays UI-free and testable).
/// </summary>
public sealed class DiagnosticLog : chatxp.core.ILog
{
    public sealed record Entry(DateTimeOffset Time, string Level, string Message);

    private readonly ConcurrentQueue<Entry> _entries = new();
    private readonly int _capacity;

    /// <summary>Raised on every new entry (background thread).</summary>
    public event Action<Entry>? Added;

    public DiagnosticLog(int capacity = 500)
    {
        _capacity = capacity;
    }

    public IReadOnlyCollection<Entry> Entries => _entries.ToArray();

    private void Add(string level, string message)
    {
        var entry = new Entry(DateTimeOffset.Now, level, message);
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
        Added?.Invoke(entry);
    }

    public void Info(string message) => Add("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        // Exceptions become part of the logged detail (failures must be append-detailed).
        if (exception is not null)
            message = $"{message}\n    Exception: {exception.GetType().Name}: {exception.Message}";
        Add("ERROR", message);
    }
}
