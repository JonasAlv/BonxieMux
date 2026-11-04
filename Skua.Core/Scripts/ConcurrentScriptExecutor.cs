using System.Collections.Concurrent;

namespace Skua.Core.Scripts;

public class ConcurrentScriptExecutor : IDisposable
{
    private readonly int _maxConcurrentScripts;
    private readonly SemaphoreSlim _executionSemaphore;
    private readonly ConcurrentDictionary<string, ScriptExecutionContext> _activeScripts = new();

    public ConcurrentScriptExecutor(int maxConcurrentScripts = 1)
    {
        _maxConcurrentScripts = maxConcurrentScripts;
        _executionSemaphore = new(maxConcurrentScripts, maxConcurrentScripts);
    }

    public async Task<ScriptExecutionContext> StartScriptAsync(
        string scriptId,
        Func<CancellationToken, Task> scriptAction)
    {
        await _executionSemaphore.WaitAsync();

        ScriptExecutionContext context = new(scriptId);
        _activeScripts.TryAdd(scriptId, context);

        _ = ExecuteScriptInternalAsync(scriptId, scriptAction, context);

        return context;
    }

    private async Task ExecuteScriptInternalAsync(
        string scriptId,
        Func<CancellationToken, Task> scriptAction,
        ScriptExecutionContext context)
    {
        try
        {
            await scriptAction(context.CancellationToken);
            context.MarkCompleted();
        }
        catch (OperationCanceledException)
        {
            context.MarkCancelled();
        }
        catch (Exception ex)
        {
            context.MarkFailed(ex);
        }
        finally
        {
            _activeScripts.TryRemove(scriptId, out _);
            _executionSemaphore.Release();
        }
    }

    public async Task StopScriptAsync(string scriptId)
    {
        if (_activeScripts.TryGetValue(scriptId, out ScriptExecutionContext? context))
        {
            context.Cancel();
            await context.WaitForCompletionAsync();
        }
    }

    public IReadOnlyDictionary<string, ScriptExecutionContext> GetActiveScripts()
    {
        Dictionary<string, ScriptExecutionContext> dict = new();
        foreach (KeyValuePair<string, ScriptExecutionContext> kvp in _activeScripts)
        {
            dict.Add(kvp.Key, kvp.Value);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ScriptExecutionContext>(dict);
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, ScriptExecutionContext> kvp in _activeScripts)
        {
            kvp.Value.Cancel();
        }
        _executionSemaphore?.Dispose();
    }
}

public class ScriptExecutionContext
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completionSource = new();
    private DateTime _startTime;

    public string ScriptId { get; }
    public CancellationToken CancellationToken => _cts.Token;
    public ExecutionState State { get; private set; } = ExecutionState.Pending;
    public Exception? Error { get; private set; }
    public TimeSpan Elapsed => DateTime.UtcNow - _startTime;

    public ScriptExecutionContext(string scriptId)
    {
        ScriptId = scriptId;
        _startTime = DateTime.UtcNow;
    }

    public void Cancel()
    {
        _cts.Cancel();
    }

    public void MarkCompleted()
    {
        State = ExecutionState.Completed;
        _completionSource.TrySetResult();
    }

    public void MarkCancelled()
    {
        State = ExecutionState.Cancelled;
        _completionSource.TrySetCanceled();
    }

    public void MarkFailed(Exception ex)
    {
        State = ExecutionState.Failed;
        Error = ex;
        _completionSource.TrySetException(ex);
    }

    public async Task WaitForCompletionAsync()
    {
        await _completionSource.Task;
    }
}

public enum ExecutionState
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed
}
