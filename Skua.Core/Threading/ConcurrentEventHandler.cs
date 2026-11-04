using System.Collections.Concurrent;

namespace Skua.Core.Threading;

public class ConcurrentEventHandler<TEventArgs> : IDisposable where TEventArgs : EventArgs
{
    private readonly ConcurrentQueue<(object? sender, TEventArgs args)> _eventQueue = new();
    private readonly Delegate[] _handlers;
    private readonly CancellationTokenSource _cts;
    private Task? _processingTask;

    public ConcurrentEventHandler(EventHandler<TEventArgs> eventHandler)
    {
        _cts = new();
        _handlers = eventHandler.GetInvocationList();
        Start();
    }

    private void Start()
    {
        _processingTask = ProcessEventsAsync(_cts.Token);
    }

    public void RaiseEvent(object? sender, TEventArgs args)
    {
        _eventQueue.Enqueue((sender, args));
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            while (_eventQueue.TryDequeue(out (object? sender, TEventArgs args) eventData))
            {
                (object? sender, TEventArgs? args) = eventData;
                await Task.Run(() =>
                {
                    foreach (Delegate handler in _handlers)
                    {
                        try
                        {
                            if (handler is EventHandler<TEventArgs> typedHandler)
                            {
                                typedHandler(sender, args);
                            }
                        }
                        catch
                        {
                        }
                    }
                }, ct);
            }

            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_processingTask != null)
        {
            await _processingTask;
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}

public class ConcurrentEventAggregator : IDisposable
{
    private readonly ConcurrentDictionary<string, object> _eventHandlers = new();
    private readonly CancellationTokenSource _cts;

    public ConcurrentEventAggregator()
    {
        _cts = new();
    }

    public void Subscribe<TEventArgs>(string eventName, EventHandler<TEventArgs> handler) where TEventArgs : EventArgs
    {
        _eventHandlers.TryAdd(eventName, handler);
    }

    public void Unsubscribe(string eventName)
    {
        _eventHandlers.TryRemove(eventName, out _);
    }

    public void PublishEvent<TEventArgs>(string eventName, object? sender, TEventArgs args) where TEventArgs : EventArgs
    {
        if (_eventHandlers.TryGetValue(eventName, out object? handler) && handler is EventHandler<TEventArgs> typedHandler)
        {
            Task.Run(() =>
            {
                foreach (EventHandler<TEventArgs> invocation in typedHandler.GetInvocationList())
                {
                    try
                    {
                        invocation(sender, args);
                    }
                    catch
                    {
                    }
                }
            }, _cts.Token);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
