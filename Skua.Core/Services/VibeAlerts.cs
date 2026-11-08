using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skua.Core.Services;

/// <summary>
/// VibeSkuffle alerts: posts to a Discord webhook when recovery needs a person (a script that won't start again,
/// AQW looking down, a client that keeps crashing, a rescue that gave up), plus a daily summary of what the
/// watchdog did. Off unless <c>alerts.txt</c> next to VibeSkuffle.exe has a webhook in it.
/// </summary>
/// <remarks>
/// alerts.txt is kept by the build and left out of release zips; it isn't the VibeSkua "Discord Webhook URL"
/// setting, so script start/stop notifications stay as they are. The same alert for the same account is sent at
/// most once every 10 minutes.
/// </remarks>
public static class VibeAlerts
{
    public const string FileName = "alerts.txt";
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static readonly object CfgLock = new();
    private static DateTime _cfgRead = DateTime.MinValue;
    private static string? _webhook;
    private static int _summaryHour = 8;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, DateTime> LastSent = new();
    private static readonly SemaphoreSlim SendGate = new(1, 1);

    public enum Level { Info, Warning, Problem }

    private static void ReadConfig()
    {
        lock (CfgLock)
        {
            if (DateTime.UtcNow - _cfgRead < TimeSpan.FromSeconds(60))
                return;
            _cfgRead = DateTime.UtcNow;
            _webhook = null;
            _summaryHour = 8;
            try
            {
                if (!File.Exists(ConfigPath))
                    return;
                foreach (string raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = line[..eq].Trim().ToLowerInvariant(), value = line[(eq + 1)..].Trim();
                    if (key == "webhook" && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        _webhook = value;
                    else if (key == "summary_hour" && int.TryParse(value, out int h))
                        _summaryHour = h;
                }
            }
            catch { }
        }
    }

    public static bool Enabled
    {
        get
        {
            ReadConfig();
            return _webhook is not null;
        }
    }

    /// <summary>Hour (0-23, local time) to post the daily summary; -1 = no daily summary.</summary>
    public static int SummaryHour
    {
        get
        {
            ReadConfig();
            return _summaryHour;
        }
    }

    /// <summary>Queues an alert. Never throws, never blocks the caller.</summary>
    public static void Send(string account, string title, string text, Level level = Level.Problem)
    {
        try
        {
            if (!Enabled)
                return;
            string key = $"{account}|{title}";
            DateTime now = DateTime.UtcNow;
            if (LastSent.TryGetValue(key, out DateTime last) && now - last < TimeSpan.FromMinutes(10))
                return;
            LastSent[key] = now;
            _ = Task.Run(() => PostAsync(string.IsNullOrWhiteSpace(account) ? title : $"{account}: {title}", text, level));
        }
        catch { }
    }

    /// <summary>Posts right away (for the summary); returns whether Discord took it.</summary>
    public static Task<bool> PostAsync(string title, string text, Level level = Level.Info)
    {
        return PostCoreAsync(title, text, level);
    }

    private static async Task<bool> PostCoreAsync(string title, string text, Level level)
    {
        ReadConfig();
        string? url = _webhook;
        if (url is null)
            return false;
        await SendGate.WaitAsync();
        try
        {
            int color = level switch { Level.Problem => 0xE74C3C, Level.Warning => 0xF39C12, _ => 0x3498DB };
            var payload = new
            {
                username = "VibeSkuffle",
                embeds = new[]
                {
                    new
                    {
                        title = Trim(title, 250),
                        description = Trim(text, 4000),
                        color,
                        timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    },
                },
            };
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using StringContent body = new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await Http.PostAsync(url, body);
                    if (response.IsSuccessStatusCode)
                    {
                        LogResult($"sent \"{Trim(title, 80)}\" to Discord.");
                        return true;
                    }
                    if ((int)response.StatusCode == 429)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
                        continue;
                    }
                    LogResult($"Discord refused \"{Trim(title, 80)}\" (HTTP {(int)response.StatusCode}); check the webhook in {FileName}.");
                    return false;
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                        LogResult($"couldn't reach Discord for \"{Trim(title, 80)}\": {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
            return false;
        }
        finally
        {
            // Discord allows about 5 messages per 2 seconds per webhook; stay well under it.
            await Task.Delay(1000);
            SendGate.Release();
        }
    }

    /// <summary>One line in login-watchdog.log (never the webhook itself).</summary>
    private static void LogResult(string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "login-watchdog.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [alerts] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- daily summary ---------------------------------------------------------------------------------

    private static readonly Regex LineRx = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[([^\]]*)\]\s?(.*)$", RegexOptions.Compiled);

    private static readonly (string Label, Func<string, bool> Match, bool Problem)[] Kinds =
    {
        // Most specific first: one log line counts once.
        ("whole-client restarts", m => m.Contains("Restarting the whole client") || m.Contains("restarting the client (asked"), false),
        ("stuck logins", m => m.StartsWith("stuck on \"") && !m.Contains("has been lost"), false),
        ("connection lost", m => m.StartsWith("stuck on \"") && m.Contains("has been lost"), false),
        ("logged-out relogs", m => m.Contains("logged out with nothing happening"), false),
        ("quest-bug relogs", m => m.Contains("while farming (an AQW bug"), false),
        ("stuck-script relogs", m => m.Contains("the script has done nothing"), false),
        ("frozen windows", m => m.Contains("stopped answering"), false),
        ("memory restarts", m => (m.Contains("MB (limit") && m.EndsWith("restarting it.")) || m.Contains("the PC is low on memory"), false),
        ("crash restarts", m => m.Contains("closed unexpectedly"), false),
        ("script restarts", m => m.Contains("starting the script again"), false),
        ("graphics switches", m => m.Contains("graphics (right-click menu)"), false),
        ("rescue calls", m => m.Contains("asking the other accounts for help"), false),
        ("rescues helped", m => m.Contains("keeps dying in"), false),
        ("scripts left stopped", m => m.Contains("couldn't get the script running again"), true),
        ("rescues gave up", m => m.Contains("it's still stuck; going back") || m.StartsWith("couldn't get into") || m.StartsWith("couldn't follow"), true),
        ("AQW looked down", m => m.Contains("recoveries in a row (AQW may be down)"), true),
        ("kept crashing", m => m.Contains("keeps crashing"), true),
        ("no saved login", m => m.Contains("no saved login"), true),
        ("graphics switches undone", m => m.Contains("graphics didn't start within"), true),
    };

    /// <summary>Counts what the watchdog did since <paramref name="since"/>, per account, from login-watchdog.log.</summary>
    public static string BuildSummary(DateTime since, out bool anyProblems)
    {
        anyProblems = false;
        string path = Path.Combine(AppContext.BaseDirectory, "login-watchdog.log");
        Dictionary<string, Dictionary<string, int>> perAccount = new(StringComparer.OrdinalIgnoreCase);
        List<string> problems = new();
        try
        {
            if (File.Exists(path))
            {
                using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(fs);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    Match m = LineRx.Match(line);
                    if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t) || t < since)
                        continue;
                    string account = m.Groups[2].Value.Trim();
                    if (account.Length == 0 || account == "?")
                        account = "(unknown)";
                    string msg = m.Groups[3].Value.Replace("(tab host) ", "").Replace("(client) ", "").Trim();
                    foreach ((string label, Func<string, bool> match, bool problem) in Kinds)
                    {
                        if (!match(msg))
                            continue;
                        if (!perAccount.TryGetValue(account, out Dictionary<string, int>? counts))
                            perAccount[account] = counts = new();
                        counts[label] = counts.GetValueOrDefault(label) + 1;
                        if (problem)
                        {
                            anyProblems = true;
                            if (problems.Count < 15)
                                problems.Add($"`{t:HH:mm}` **{account}**: {Trim(msg, 160)}");
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return $"Couldn't read login-watchdog.log: {ex.Message}";
        }

        StringBuilder sb = new();
        if (perAccount.Count == 0)
        {
            sb.Append("Nothing needed fixing. 👍");
            return sb.ToString();
        }
        foreach ((string account, Dictionary<string, int> counts) in perAccount.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> parts = Kinds.Where(k => counts.ContainsKey(k.Label)).Select(k => $"{k.Label} {counts[k.Label]}");
            sb.Append("**").Append(account).Append("**: ").Append(string.Join(" · ", parts)).Append('\n');
        }
        if (problems.Count > 0)
        {
            sb.Append("\n**Needs a look:**\n");
            foreach (string p in problems)
                sb.Append(p).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Called about once a minute by the tab window: posts the summary of the last 24 hours once a day at
    /// <see cref="SummaryHour"/> (remembered in %TEMP%, so a restart doesn't post it twice).
    /// </summary>
    public static void DailySummaryTick()
    {
        try
        {
            int hour = SummaryHour;
            if (!Enabled || hour is < 0 or > 23 || DateTime.Now.Hour != hour)
                return;
            string dir = Path.Combine(Path.GetTempPath(), "VibeSkuaTabs");
            Directory.CreateDirectory(dir);
            string marker = Path.Combine(dir, "summary-sent.txt");
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == today)
                return;
            File.WriteAllText(marker, today);
            string text = BuildSummary(DateTime.Now.AddHours(-24), out bool problems);
            _ = PostAsync("Watchdog summary (last 24 hours)", text, problems ? Level.Warning : Level.Info);
        }
        catch { }
    }
}
