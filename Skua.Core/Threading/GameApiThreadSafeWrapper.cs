using Skua.Core.Interfaces;
using System.Collections.Concurrent;

namespace Skua.Core.Threading;

public class GameApiThreadSafeWrapper : IGameApiThreadSafeWrapper
{
    private readonly IScriptInterface _scriptInterface;
    private readonly SemaphoreSlim _apiAccessSemaphore;
    private readonly ConcurrentQueue<GameApiCall> _pendingCalls = new();
    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;

    public GameApiThreadSafeWrapper(IScriptInterface scriptInterface, int maxConcurrentGameCalls = 1)
    {
        _scriptInterface = scriptInterface;
        _apiAccessSemaphore = new(maxConcurrentGameCalls, maxConcurrentGameCalls);
    }

    public void Start()
    {
        if (_processingTask != null)
            return;

        _processingCts = new();
        _processingTask = ProcessGameCallsAsync(_processingCts.Token);
    }

    public async Task StopAsync()
    {
        _processingCts?.Cancel();
        if (_processingTask != null)
        {
            await _processingTask;
        }
        _apiAccessSemaphore.Dispose();
    }

    public async Task<T> ExecuteAsync<T>(Func<IScriptInterface, T> operation, string operationName = "")
    {
        GameApiCall call = new()
        {
            OperationName = operationName,
            ExecutionTime = DateTime.UtcNow,
            IsAsync = false
        };

        await _apiAccessSemaphore.WaitAsync();
        try
        {
            return operation(_scriptInterface);
        }
        finally
        {
            _apiAccessSemaphore.Release();
        }
    }

    public async Task ExecuteAsync(Action<IScriptInterface> operation, string operationName = "")
    {
        GameApiCall call = new()
        {
            OperationName = operationName,
            ExecutionTime = DateTime.UtcNow,
            IsAsync = false
        };

        await _apiAccessSemaphore.WaitAsync();
        try
        {
            operation(_scriptInterface);
        }
        finally
        {
            _apiAccessSemaphore.Release();
        }
    }

    public async Task<T> ExecuteAsyncTask<T>(Func<IScriptInterface, Task<T>> operation, string operationName = "")
    {
        await _apiAccessSemaphore.WaitAsync();
        try
        {
            return await operation(_scriptInterface);
        }
        finally
        {
            _apiAccessSemaphore.Release();
        }
    }

    public async Task ExecuteAsyncTask(Func<IScriptInterface, Task> operation, string operationName = "")
    {
        await _apiAccessSemaphore.WaitAsync();
        try
        {
            await operation(_scriptInterface);
        }
        finally
        {
            _apiAccessSemaphore.Release();
        }
    }

    private async Task ProcessGameCallsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyCollection<GameApiCall> GetPendingCalls()
    {
        return _pendingCalls.ToList().AsReadOnly();
    }

    public void ClearPendingCalls()
    {
        while (_pendingCalls.TryDequeue(out _)) { }
    }
}

public interface IGameApiThreadSafeWrapper
{
    void Start();
    Task StopAsync();
    Task<T> ExecuteAsync<T>(Func<IScriptInterface, T> operation, string operationName = "");
    Task ExecuteAsync(Action<IScriptInterface> operation, string operationName = "");
    Task<T> ExecuteAsyncTask<T>(Func<IScriptInterface, Task<T>> operation, string operationName = "");
    Task ExecuteAsyncTask(Func<IScriptInterface, Task> operation, string operationName = "");
    IReadOnlyCollection<GameApiCall> GetPendingCalls();
    void ClearPendingCalls();
}

public class GameApiCall
{
    public string OperationName { get; set; } = string.Empty;
    public DateTime ExecutionTime { get; set; }
    public bool IsAsync { get; set; }
    public long DurationMs { get; set; }
    public Exception? Error { get; set; }
}
