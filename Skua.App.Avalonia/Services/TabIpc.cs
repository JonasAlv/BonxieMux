using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using Skua.App.Avalonia.Views;
using Skua.Core.Models;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Cross-platform lightweight IPC between TabbedHostWindow (host) and MainWindow (child instances)
/// using asynchronous named pipes and serialized message delivery.
/// </summary>
public static class TabIpc
{
    private static readonly ConcurrentDictionary<int, Channel<string>> _queues = new();

    public static string GetChildPipeName(int pid) =>
        Path.Combine(ClientFileSources.TabsDIR, $"child_{pid}.pipe");

    private static void DeleteSocket(string pipeName)
    {
        if (Path.IsPathRooted(pipeName))
        {
            try
            {
                if (File.Exists(pipeName))
                    File.Delete(pipeName);
            }
            catch { }
        }
    }

    public static void SendMessage(int childPid, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (childPid <= 0)
            return;

        string payload = $"{msg}:{wParam.ToInt64()}:{lParam.ToInt64()}";
        var channel = _queues.GetOrAdd(childPid, pid =>
        {
            var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            Task.Run(() => ProcessQueueAsync(pid, ch.Reader));
            return ch;
        });

        channel.Writer.TryWrite(payload);
    }

    public static void CleanUp(int childPid)
    {
        if (_queues.TryRemove(childPid, out var channel))
        {
            channel.Writer.TryComplete();
        }
        DeleteSocket(GetChildPipeName(childPid));
    }

    private static async Task ProcessQueueAsync(int childPid, ChannelReader<string> reader)
    {
        string pipeName = GetChildPipeName(childPid);
        try
        {
            while (await reader.WaitToReadAsync())
            {
                NamedPipeClientStream? client = null;
                StreamWriter? writer = null;
                try
                {
                    // Attempt connection with retries
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        try
                        {
                            client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                            await client.ConnectAsync(300);
                            writer = new StreamWriter(client) { AutoFlush = true };
                            break;
                        }
                        catch
                        {
                            try { client?.Dispose(); } catch { }
                            client = null;
                            await Task.Delay(50);
                        }
                    }

                    if (client != null && client.IsConnected && writer != null)
                    {
                        while (reader.TryRead(out string? message))
                        {
                            await writer.WriteLineAsync(message);
                            await writer.FlushAsync();
                        }
                    }
                }
                catch
                {
                    // Stream failure, will attempt reconnect on next message
                }
                finally
                {
                    try { writer?.Dispose(); } catch { }
                    try { client?.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // Reader completed or faulted
        }
    }

    public static void StartListener(MainWindow window, int pid, CancellationToken ct)
    {
        string pipeName = GetChildPipeName(pid);
        Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    NamedPipeServerStream? server = null;
                    try
                    {
                        server = new NamedPipeServerStream(
                            pipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        using (ct.Register(() => { try { server?.Dispose(); } catch { } }))
                        {
                            await server.WaitForConnectionAsync(ct);
                        }

                        using (server)
                        using (var reader = new StreamReader(server))
                        {
                            while (!ct.IsCancellationRequested)
                            {
                                string? line = await reader.ReadLineAsync();
                                if (line == null) break;
                                if (string.IsNullOrWhiteSpace(line)) continue;

                                var parts = line.Split(':');
                                if (parts.Length >= 3 &&
                                    uint.TryParse(parts[0], out uint msg) &&
                                    long.TryParse(parts[1], out long wParam) &&
                                    long.TryParse(parts[2], out long lParam))
                                {
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        window.HandleIpcMessage(msg, (IntPtr)wParam, (IntPtr)lParam);
                                    });
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        try { server?.Dispose(); } catch { }
                        break;
                    }
                    catch
                    {
                        try { server?.Dispose(); } catch { }
                        if (ct.IsCancellationRequested) break;
                        await Task.Delay(50, ct);
                    }
                }
            }
            finally
            {
                DeleteSocket(pipeName);
            }
        }, ct);
    }
}
