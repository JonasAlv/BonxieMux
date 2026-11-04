using System.Collections.Concurrent;
using System.Diagnostics;

namespace Skua.Core.Services;

public class ConcurrentLoggingService : IDisposable
{
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly SemaphoreSlim _queueSemaphore = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<LogEntry> _logAction;
    private Task? _processingTask;

    public ConcurrentLoggingService(Action<LogEntry> logAction)
    {
        _logAction = logAction;
        _processingTask = ProcessLogsAsync(_cts.Token);
    }

    public void Log(string message, LogLevel level = LogLevel.Info, string? category = null)
    {
        LogEntry entry = new()
        {
            Message = message,
            Level = level,
            Category = category,
            Timestamp = DateTime.UtcNow,
            ThreadId = Thread.CurrentThread.ManagedThreadId
        };

        _logQueue.Enqueue(entry);
        _queueSemaphore.Release();
    }

    private async Task ProcessLogsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _queueSemaphore.WaitAsync(ct);

                while (_logQueue.TryDequeue(out LogEntry? entry))
                {
                    try
                    {
                        _logAction(entry);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Error processing log: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _processingTask?.Wait(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        _queueSemaphore?.Dispose();
    }
}

public class LogEntry
{
    public string Message { get; set; } = string.Empty;
    public LogLevel Level { get; set; }
    public string? Category { get; set; }
    public DateTime Timestamp { get; set; }
    public int ThreadId { get; set; }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}
