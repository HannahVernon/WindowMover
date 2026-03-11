using System.Collections.Concurrent;

namespace WindowMover.Core.Services;

/// <summary>
/// Simple file logger that writes to %APPDATA%\WindowMover\logs\.
/// Rotates daily and keeps the last 7 days of logs.
/// </summary>
public sealed class AppLogger : IDisposable
{
    private static readonly Lazy<AppLogger> _instance = new(() => new AppLogger());
    public static AppLogger Instance => _instance.Value;

    private readonly string _logDir;
    private readonly BlockingCollection<string> _queue = new(1000);
    private readonly Thread _writerThread;
    private readonly CancellationTokenSource _cts = new();

    private AppLogger()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowMover", "logs");
        Directory.CreateDirectory(_logDir);

        _writerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "WindowMover-Logger"
        };
        _writerThread.Start();

        PurgOldLogs();
    }

    public void Info(string message) => Enqueue("INFO", message);
    public void Warn(string message) => Enqueue("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var text = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Enqueue("ERROR", text);
    }

    private void Enqueue(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        _queue.TryAdd(line);
    }

    private void ProcessQueue()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    var path = Path.Combine(_logDir, $"WindowMover-{DateTime.Now:yyyy-MM-dd}.log");
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { /* don't crash the app over logging */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PurgOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.GetFiles(_logDir, "WindowMover-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _cts.Cancel();
        _writerThread.Join(2000);
        _cts.Dispose();
        _queue.Dispose();
    }
}
