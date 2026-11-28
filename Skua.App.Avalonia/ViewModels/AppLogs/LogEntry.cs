using Skua.Core.Models;
using Skua.Shared.Avalonia.Controls.Icons;
using System;
using System.Text.RegularExpressions;

namespace Skua.App.Avalonia.ViewModels.AppLogs;

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error,
    System
}

public enum LogFilterMode
{
    All,
    Issues,
    System
}

public sealed class LogEntry
{
    private static readonly Regex LeadingTimeRegex = new(@"^\[(\d{2}:\d{2}:\d{2})\]\s*", RegexOptions.Compiled);
    private static readonly Regex IsoTimeRegex = new(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?)\s+", RegexOptions.Compiled);
    private static readonly Regex RufflePrefixRegex = new(@"^(TRACE|DEBUG|INFO|WARN|ERROR)\s+([a-zA-Z0-9_:]+):\s*", RegexOptions.Compiled);
    private static readonly Regex BracketTagRegex = new(@"^\[([^\]]{1,32})\]\s*", RegexOptions.Compiled);
    private static readonly Regex ParenTagRegex = new(@"^\(([^)]{1,32})\)\s*", RegexOptions.Compiled);

    public LogEntry(string rawText, LogType logType, string? fallbackTimestamp = null)
    {
        RawText = rawText ?? string.Empty;
        LogType = logType;

        string work = RawText.Trim();
        string timestamp = string.Empty;
        string tag = string.Empty;
        LogSeverity severity = LogSeverity.Info;
        string? ruffleLevel = null;

        // 1. Extract leading timestamp ([HH:mm:ss] or ISO-8601 from Ruffle/Mugetsu)
        Match timeMatch = LeadingTimeRegex.Match(work);
        if (timeMatch.Success)
        {
            timestamp = timeMatch.Groups[1].Value;
            work = work[timeMatch.Length..].TrimStart();
        }
        else
        {
            Match isoMatch = IsoTimeRegex.Match(work);
            if (isoMatch.Success)
            {
                string isoRaw = isoMatch.Groups[1].Value;
                timestamp = DateTimeOffset.TryParse(isoRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset dto)
                    ? dto.ToLocalTime().ToString("HH:mm:ss")
                    : isoRaw.Substring(11, 8);
                work = work[isoMatch.Length..].TrimStart();
            }
            else
            {
                timestamp = fallbackTimestamp ?? DateTime.Now.ToString("HH:mm:ss");
            }
        }

        // 2. Extract Ruffle/tracing level + target (e.g. "WARN ruffle_desktop::skua: ...")
        Match ruffleMatch = RufflePrefixRegex.Match(work);
        if (ruffleMatch.Success)
        {
            ruffleLevel = ruffleMatch.Groups[1].Value;
            string target = ruffleMatch.Groups[2].Value;
            int sep = target.LastIndexOf("::", StringComparison.Ordinal);
            tag = sep >= 0 ? target[(sep + 2)..] : target;
            work = work[ruffleMatch.Length..].TrimStart();
        }
        else
        {
            // 3. Extract [Tag] or (Source) prefix
            Match bMatch = BracketTagRegex.Match(work);
            if (bMatch.Success)
            {
                tag = bMatch.Groups[1].Value;
                work = work[bMatch.Length..].TrimStart();
            }
            else
            {
                Match pMatch = ParenTagRegex.Match(work);
                if (pMatch.Success)
                {
                    tag = pMatch.Groups[1].Value;
                    work = work[pMatch.Length..].TrimStart();
                }
                else if (work.StartsWith("Login watchdog:", StringComparison.OrdinalIgnoreCase))
                {
                    tag = "Watchdog";
                    work = work["Login watchdog:".Length..].TrimStart();
                }
            }
        }

        // 4. Detect severity & strip noisy community-script emojis for crisp UI rendering
        bool hasCheckEmoji = work.Contains("✔", StringComparison.Ordinal) || work.Contains("✅", StringComparison.Ordinal);
        bool hasCrossEmoji = work.Contains("❌", StringComparison.Ordinal) || work.Contains("⛔", StringComparison.Ordinal);
        bool hasWarnEmoji = work.Contains("⚠", StringComparison.Ordinal);

        string cleaned = StripScriptEmojis(work);

        if (ruffleLevel == "ERROR"
            || logType == LogType.Flash
            || hasCrossEmoji
            || ContainsAny(cleaned, "failed", "error:", "exception", "panic", "fatal", "could not", "unable to", "aborted"))
        {
            severity = LogSeverity.Error;
        }
        else if (ruffleLevel == "WARN"
            || hasWarnEmoji
            || ContainsAny(cleaned, "warning", "stuck on", "stalled", "timed out", "timeout", "didn't finish", "not ready", "avoiding ", "connection lost", "disconnected"))
        {
            severity = LogSeverity.Warning;
        }
        else if (hasCheckEmoji
            || ContainsAny(cleaned, "completed quest", "connected successfully", "re-login was successful", "finished successfully", "succeeded", "acquired"))
        {
            severity = LogSeverity.Success;
        }
        else if (IsSystemTag(tag) || logType == LogType.Mugetsu)
        {
            severity = LogSeverity.System;
        }

        Timestamp = timestamp;
        Tag = tag;
        Message = cleaned;
        Severity = severity;
        IconKind = severity switch
        {
            LogSeverity.Error => CodiconKind.Error,
            LogSeverity.Warning => CodiconKind.Warning,
            LogSeverity.Success => CodiconKind.Pass,
            LogSeverity.System => logType == LogType.Mugetsu ? CodiconKind.Terminal : CodiconKind.Sync,
            _ => CodiconKind.CircleSmallFilled
        };
    }

    public string RawText { get; }
    public LogType LogType { get; }
    public string Timestamp { get; }
    public string Tag { get; }
    public bool HasTag => !string.IsNullOrEmpty(Tag);
    public string Message { get; }
    public LogSeverity Severity { get; }
    public CodiconKind IconKind { get; }

    public bool IsError => Severity == LogSeverity.Error;
    public bool IsWarning => Severity == LogSeverity.Warning;
    public bool IsSuccess => Severity == LogSeverity.Success;
    public bool IsSystem => Severity == LogSeverity.System || IsSystemTag(Tag);
    public bool IsIssue => Severity is LogSeverity.Warning or LogSeverity.Error;

    public bool Matches(string? search, LogFilterMode mode)
    {
        if (mode == LogFilterMode.Issues && !IsIssue)
            return false;
        if (mode == LogFilterMode.System && !IsSystem)
            return false;

        if (string.IsNullOrWhiteSpace(search))
            return true;

        return RawText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || Tag.Contains(search, StringComparison.OrdinalIgnoreCase)
            || Message.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => RawText;

    private static bool IsSystemTag(string tag) =>
        tag.Equals("Relogin", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Watchdog", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("ButlerV5", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Options", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Loadouts", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Auto Attack", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("Auto Hunt", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("debug", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string StripScriptEmojis(string text)
    {
        string s = text
            .Replace("[✔]", "[OK]", StringComparison.Ordinal)
            .Replace("[❌]", "[No]", StringComparison.Ordinal)
            .Replace("✔", string.Empty, StringComparison.Ordinal)
            .Replace("✅", string.Empty, StringComparison.Ordinal)
            .Replace("❌", string.Empty, StringComparison.Ordinal)
            .Replace("⛔", string.Empty, StringComparison.Ordinal)
            .Replace("ℹ️", string.Empty, StringComparison.Ordinal)
            .Replace("ℹ", string.Empty, StringComparison.Ordinal)
            .Replace("💎", string.Empty, StringComparison.Ordinal)
            .Replace("⚠️", string.Empty, StringComparison.Ordinal)
            .Replace("⚠", string.Empty, StringComparison.Ordinal);

        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);

        return s.Trim();
    }
}
