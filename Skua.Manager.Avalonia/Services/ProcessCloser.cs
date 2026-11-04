using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.Services;

/// <summary>
/// Stops client processes by asking them to close first via SIGTERM, so they run their own shutdown
/// (stopping scripts, removing the tray icon).
/// </summary>
internal static class ProcessCloser
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    /// <summary>
    /// Posts graceful termination to the process.
    /// </summary>
    public static bool RequestClose(Process process)
    {
        try
        {
            return kill(process.Id, SIGTERM) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the process to close and waits up to <paramref name="timeout"/>; kills it if it is still running.
    /// </summary>
    public static async Task CloseAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return;

        if (RequestClose(process))
        {
            using CancellationTokenSource cts = new(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                Trace.WriteLine($"Process {process.Id} did not close within {timeout.TotalSeconds:0.#}s, killing it.");
            }
        }

        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync();
        }
    }
}
