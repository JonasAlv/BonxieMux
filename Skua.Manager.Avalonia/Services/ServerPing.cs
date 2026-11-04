using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.Services;

/// <summary>
/// Latency of a game server as the game itself sees it: how long the server takes to answer the game's first message (the version
/// check). No login is involved and nothing else is sent. A plain connect or ICMP ping is not used: the addresses sit behind nearby
/// edges, so those say little about the server (a far server such as Yokai connects as fast as the rest but answers slowly).
/// </summary>
public static class ServerPing
{
    private static readonly byte[] VersionCheck = Encoding.ASCII.GetBytes("<msg t='sys'><body action='verChk' r='0'><ver v='165' /></body></msg>\0");

    /// <summary>Median round trip in milliseconds over <paramref name="samples"/> tries, or null when no try got an answer within <paramref name="timeoutMs"/>.</summary>
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
                // The answer ends with a zero byte; only the server's own "apiOK" counts (an "apiKO" or a closed connection is no sample).
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
                // no answer for this try
            }
        }

        if (times.Count == 0)
            return null;
        times.Sort();
        return times[times.Count / 2];
    }
}
