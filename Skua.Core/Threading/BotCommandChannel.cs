using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Skua.Core.Threading;

public class BotCommandRequest
{
    public string CommandId { get; } = Guid.NewGuid().ToString();
    public string CommandName { get; set; } = string.Empty;
    public object? CommandData { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public TaskCompletionSource<BotCommandResponse> ResponseTask { get; } = new();
}

public class BotCommandResponse
{
    public string CommandId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public interface IBotCommandHandler
{
    string CommandName { get; }
    Task<object?> HandleAsync(object? data, CancellationToken ct);
}

public class BotCommandChannel : IDisposable
{
    private readonly Channel<BotCommandRequest> _commandChannel;
    private readonly ConcurrentDictionary<string, IBotCommandHandler> _handlers = new();
    private readonly CancellationTokenSource _cts;
    private Task? _processingTask;

    public BotCommandChannel(int capacity = 100)
    {
        _cts = new();
        BoundedChannelOptions options = new(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _commandChannel = Channel.CreateBounded<BotCommandRequest>(options);
    }

    public void RegisterHandler(IBotCommandHandler handler)
    {
        _handlers.TryAdd(handler.CommandName, handler);
    }

    public void Start()
    {
        if (_processingTask != null)
            return;

        _processingTask = ProcessCommandsAsync(_cts.Token);
    }

    public async Task<BotCommandResponse> SendCommandAsync(string commandName, object? data, int timeoutMs = 5000)
    {
        BotCommandRequest request = new()
        {
            CommandName = commandName,
            CommandData = data
        };

        await _commandChannel.Writer.WriteAsync(request, _cts.Token);

        using CancellationTokenSource cts = new(timeoutMs);
        try
        {
            return await request.ResponseTask.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new()
            {
                CommandId = request.CommandId,
                Success = false,
                Error = $"Command '{commandName}' timed out after {timeoutMs}ms"
            };
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (BotCommandRequest request in _commandChannel.Reader.ReadAllAsync(ct))
            {
                if (_handlers.TryGetValue(request.CommandName, out IBotCommandHandler? handler))
                {
                    try
                    {
                        object? result = await handler.HandleAsync(request.CommandData, ct).ConfigureAwait(false);
                        request.ResponseTask.SetResult(new()
                        {
                            CommandId = request.CommandId,
                            Success = true,
                            Result = result
                        });
                    }
                    catch (Exception ex)
                    {
                        request.ResponseTask.SetResult(new()
                        {
                            CommandId = request.CommandId,
                            Success = false,
                            Error = ex.Message
                        });
                    }
                }
                else
                {
                    request.ResponseTask.SetResult(new()
                    {
                        CommandId = request.CommandId,
                        Success = false,
                        Error = $"Handler for command '{request.CommandName}' not found"
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task StopAsync()
    {
        _commandChannel.Writer.Complete();
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
