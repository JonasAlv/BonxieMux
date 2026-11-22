using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.Services;

public static class ServerPing
{
    private static readonly byte[] VersionCheck = Encoding.ASCII.GetBytes("<msg t='sys'><body action='verChk' r='0'><ver v='165' /></body></msg>\0");

    public static async Task<long?> MeasureAsync(string host, int port, int samples = 3, int timeoutMs = 2500, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return null;

        List<long> times = new();
        for (int i = 0; i < samples && !token.IsCancellationRequested; i++)
        {
            try
            {
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeoutMs);
                using TcpClient client = new() { NoDelay = true };
                await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                byte[] reply = new byte[256];
                Stopwatch watch = Stopwatch.StartNew();
                await stream.WriteAsync(VersionCheck, limit.Token).ConfigureAwait(false);
                StringBuilder message = new();
                bool answered = false, refused = false;
                while (!answered && !refused)
                {
                    int read = await stream.ReadAsync(reply, limit.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    for (int b = 0; b < read && !answered && !refused; b++)
                    {
                        if (reply[b] != 0)
                        {
                            message.Append((char)reply[b]);
                            continue;
                        }
                        string text = message.ToString();
                        message.Clear();
                        if (text.Contains("action='apiOK'", StringComparison.OrdinalIgnoreCase) || text.Contains("action=\"apiOK\"", StringComparison.OrdinalIgnoreCase))
                        {
                            times.Add(watch.ElapsedMilliseconds);
                            answered = true;
                        }
                        else if (text.Contains("apiKO", StringComparison.OrdinalIgnoreCase))
                            refused = true;
                    }
                }
            }
            catch
            {
            }
        }

        if (times.Count == 0)
            return null;
        times.Sort();
        return times[times.Count / 2];
    }
}
