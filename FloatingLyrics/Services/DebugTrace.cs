namespace FloatingLyrics.Services;

public static class DebugTrace
{
    public static string LogRootPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatingLyrics");

    public static string LogFilePath => Path.Combine(LogRootPath, "startup-trace.log");

    /// <summary>
    /// Subscribe to receive log messages in real time (e.g. from a side-panel overlay).
    /// </summary>
    public static event Action<string>? OnMessage;

    // In-memory ring buffer — keeps the last 200 lines for late subscribers.
    private static readonly Queue<string> _buffer = new();
    private const int MaxBuffer = 200;

    private const long MaxLogFileBytes = 2L * 1024 * 1024;
    private static readonly object _lock = new();
    private static readonly object _fileLock = new();

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        lock (_lock)
        {
            // Keep ring buffer bounded.
            if (_buffer.Count >= MaxBuffer)
                _buffer.Dequeue();
            _buffer.Enqueue(line);
        }

        // Fire event on the calling thread; UI subscribers must marshal to their
        // own dispatcher themselves (e.g. Dispatcher.Invoke / DispatcherQueue).
        try { OnMessage?.Invoke(line); }
        catch { /* never let a bad subscriber crash the app */ }

        // Always persist to disk as a fallback.
        WriteToFile(line);
    }

    /// <summary>
    /// Returns a snapshot of all buffered lines — useful for populating a panel
    /// that opens after some messages have already been written.
    /// </summary>
    public static IReadOnlyList<string> GetSnapshot()
    {
        lock (_lock)
            return _buffer.ToList();
    }

    // -------------------------------------------------------------------------
    private static void WriteToFile(string line)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(LogRootPath);
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length >= MaxLogFileBytes)
                {
                    var backupPath = LogFilePath + ".old";
                    File.Move(LogFilePath, backupPath, overwrite: true);
                }

                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
