using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Skua.App.Avalonia.Flash;

/// <summary>
/// Opt-in accounting of what crosses the game player bridge. The player runs ActionScript and draws frames on one thread,
/// so every call the host makes (and every byte it has to serialize) is time taken from the frame budget.
/// Set <c>SKUA_FLASH_STATS=1</c> to log the heaviest callers once a minute through <see cref="Trace"/>.
/// </summary>
internal sealed class BridgeStats : IDisposable
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(60);
    private const int TopCount = 10;

    private sealed class Entry
    {
        public long Count;
        public long Chars;
        public long Ticks;
    }

    private readonly ConcurrentDictionary<string, Entry> _calls = new();
    private readonly ConcurrentDictionary<string, Entry> _events = new();
    private readonly Timer? _timer;
    private long _lastReportTimestamp = Stopwatch.GetTimestamp();

    public BridgeStats()
    {
        Enabled = Environment.GetEnvironmentVariable("SKUA_FLASH_STATS") is "1" or "true" or "TRUE" or "True";
        if (Enabled)
            _timer = new Timer(_ => Report(), null, ReportInterval, ReportInterval);
    }

    public bool Enabled { get; }

    /// <summary>A call the host made to the player and the answer it got (<paramref name="chars"/> long).</summary>
    public void RecordCall(string function, long startTimestamp, int chars)
    {
        if (!Enabled)
            return;

        Add(_calls, function, chars, Stopwatch.GetTimestamp() - startTimestamp);
    }

    /// <summary>An <c>ExternalInterface.call</c> from the SWF (<paramref name="chars"/> is the JSON line's length).</summary>
    public void RecordEvent(string name, int chars)
    {
        if (!Enabled)
            return;

        Add(_events, name, chars, 0);
    }

    private static void Add(ConcurrentDictionary<string, Entry> map, string key, int chars, long ticks)
    {
        Entry entry = map.GetOrAdd(key, static _ => new Entry());
        Interlocked.Increment(ref entry.Count);
        Interlocked.Add(ref entry.Chars, chars);
        Interlocked.Add(ref entry.Ticks, ticks);
    }

    private void Report()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = Math.Max(1, (now - Interlocked.Exchange(ref _lastReportTimestamp, now)) / (double)Stopwatch.Frequency);

            StringBuilder text = new();
            text.Append("[FlashBridge] last ").Append(seconds.ToString("0")).AppendLine("s");
            Append(text, "calls (host -> player, answer size, time waiting)", _calls, seconds, showTime: true);
            Append(text, "events (player -> host)", _events, seconds, showTime: false);
            Trace.WriteLine(text.ToString().TrimEnd());
        }
        catch
        {
            // Diagnostics must never affect the bridge.
        }
    }

    private static void Append(StringBuilder text, string title, ConcurrentDictionary<string, Entry> map, double seconds, bool showTime)
    {
        // Swap each entry's counters out so the next report only covers the following interval.
        var snapshot = map
            .Select(kv => (Name: kv.Key, Count: Interlocked.Exchange(ref kv.Value.Count, 0), Chars: Interlocked.Exchange(ref kv.Value.Chars, 0), Ticks: Interlocked.Exchange(ref kv.Value.Ticks, 0)))
            .Where(e => e.Count > 0)
            .OrderByDescending(e => showTime ? e.Ticks : e.Chars)
            .ToList();

        long totalChars = snapshot.Sum(e => e.Chars);
        long totalCount = snapshot.Sum(e => e.Count);
        text.Append("  ").Append(title).Append(": ")
            .Append((totalCount / seconds).ToString("0.0")).Append("/s, ")
            .Append((totalChars / seconds / 1024).ToString("0.0")).AppendLine(" KB/s");

        foreach (var e in snapshot.Take(TopCount))
        {
            text.Append("    ").Append(e.Name.PadRight(28))
                .Append((e.Count / seconds).ToString("0.0").PadLeft(8)).Append("/s ")
                .Append((e.Chars / Math.Max(1, e.Count) / 1024.0).ToString("0.0").PadLeft(8)).Append(" KB avg");
            if (showTime)
                text.Append(' ').Append((e.Ticks * 1000.0 / Stopwatch.Frequency / seconds).ToString("0").PadLeft(6)).Append(" ms/s");
            text.AppendLine();
        }
    }

    public void Dispose() => _timer?.Dispose();
}
