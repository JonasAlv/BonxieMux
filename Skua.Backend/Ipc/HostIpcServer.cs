using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Backend.Ipc;

/// <summary>
/// Owns the named-pipe IPC listener loop for a host window,
/// forwarding each received line to the host's message dispatcher.
/// </summary>
public sealed class HostIpcServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Action<string> _onMessage;
    private readonly CancellationTokenSource _cts = new();
    private int _started;
    private int _disposed;

    public HostIpcServer(string pipeName, Action<string> onMessage)
    {
        _pipeName = pipeName;
        _onMessage = onMessage;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    using CancellationTokenRegistration reg = _cts.Token.Register(() =>
                    {
                        try { pipeServer?.Dispose(); } catch { }
                    });

                    await pipeServer.WaitForConnectionAsync(_cts.Token);

                    using (pipeServer)
                    using (StreamReader sr = new(pipeServer))
                    {
                        string? line = await sr.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                            _onMessage(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { pipeServer?.Dispose(); } catch { }
                    break;
                }
                catch
                {
                    try { pipeServer?.Dispose(); } catch { }
                    if (_cts.Token.IsCancellationRequested)
                        break;
                    await Task.Delay(100);
                }
            }

            if (Path.IsPathRooted(_pipeName))
            {
                try
                {
                    if (File.Exists(_pipeName))
                        File.Delete(_pipeName);
                }
                catch { }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
