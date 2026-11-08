using System.Text.Json;
using Skua.Core.Models;

namespace Skua.Core.Services;

/// <summary>How far a Watch Dog action is.</summary>
public enum DogPhase
{
    /// <summary>It is doing something; stays until the result is known.</summary>
    Working = 0,
    /// <summary>It worked (shown for a few seconds).</summary>
    Done = 1,
    /// <summary>It did not work (stays until something else happens).</summary>
    Failed = 2
}

/// <summary>What the Watch Dog (and the re-login) is doing for one client, as the tab window shows it.</summary>
public sealed record DogNoticeData(DateTime UpdatedUtc, DateTime SinceUtc, DogPhase Phase, string Text, DateTime? RetryAtUtc);

/// <summary>
/// The Watch Dog's status for the tab window: a small file per client in tabs directory ({pid}.dog-notice.json). The client
/// writes it when the Watch Dog or the re-login starts, retries, finishes or gives up; the tab window shows a line under the tabs
/// ("Communication with the server was lost, logging back in", "Login didn't go through. Retrying in 4 s (attempt 2 of 5)").
/// </summary>
public static class DogNotice
{
    private static readonly object Gate = new();
    private static DogNoticeData? _current;
    private static string StatePath(int processId) => Path.Combine(ClientFileSources.TabsDIR, $"{processId}.dog-notice.json");

    /// <summary>True while a Working notice is up in this process (cheap to ask, e.g. once per tick).</summary>
    public static bool IsWorking { get { lock (Gate) return _current?.Phase == DogPhase.Working; } }

    public static void Working(string text, int? retryInSeconds = null) => Set(DogPhase.Working, text, retryInSeconds);
    public static void Done(string text) => Set(DogPhase.Done, text, null);
    public static void Failed(string text) => Set(DogPhase.Failed, text, null);

    private static void Set(DogPhase phase, string text, int? retryInSeconds)
    {
        try
        {
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                // The time a step has been going counts from when the Watch Dog started working, not from every new line.
                DateTime since = _current is { Phase: DogPhase.Working } && phase == DogPhase.Working ? _current.SinceUtc : now;
                _current = new DogNoticeData(now, since, phase, text.Trim(), retryInSeconds is int s ? now.AddSeconds(s) : null);
                Write(_current);
            }
        }
        catch { }
    }

    /// <summary>Takes the notice down (nothing is shown for this client).</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                _current = null;
                File.Delete(StatePath(Environment.ProcessId));
            }
        }
        catch { }
    }

    private static void Write(DogNoticeData data)
    {
        string file = StatePath(Environment.ProcessId), temporary = file + ".tmp";
        string dir = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        ClientFileSources.RestrictDirectoryPermissions(dir);
        File.WriteAllText(temporary, JsonSerializer.Serialize(data));
        ClientFileSources.RestrictFilePermissions(temporary);
        File.Move(temporary, file, true);
        ClientFileSources.RestrictFilePermissions(file);
    }

    public static DogNoticeData? Read(int processId)
    {
        try
        {
            string file = StatePath(processId);
            return File.Exists(file) ? JsonSerializer.Deserialize<DogNoticeData>(File.ReadAllText(file)) : null;
        }
        catch { return null; }
    }
}
