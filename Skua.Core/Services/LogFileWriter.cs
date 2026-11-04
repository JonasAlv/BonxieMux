using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.Core.Services;

/// <summary>
/// Writes Script and Debug entries in short batches. Consecutive duplicate lines are collapsed,
/// daily files roll over at 5 MB, and files older than 14 days are removed.
/// </summary>
internal sealed partial class LogFileWriter : IDisposable
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int KeepDays = 14;
    private const int RepeatSummaryEvery = 1000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettingRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly string _directory;
    private readonly object _lock = new();
    private readonly StringBuilder _pending = new();
    private readonly Timer _timer;
    private string _account = "unknown";
    private string? _lastLine;
    private string? _lastLineTime;
    private int _repeats;
    private bool? _enabled;
    private DateTime _enabledReadAt;
    private bool _cleanedUp;
    private volatile bool _disposed;

    public LogFileWriter(ISettingsService settings, string directory)
    {
        _settings = settings;
        _directory = directory;
        _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public void SetAccount(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        string account = InvalidFileNameChars().Replace(username.Trim().ToLowerInvariant(), "_");
        lock (_lock)
        {
            if (account == _account)
                return;

            AppendRepeatSummary();
            FlushLocked();
            _account = account;
            _lastLine = null;
            _lastLineTime = null;
        }
    }

    public void Add(LogType type, string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
            return;

        string text = message.TrimEnd();
        string time = DateTime.Now.ToString("HH:mm:ss");
        lock (_lock)
        {
            if (!IsEnabled())
            {
                if (_enabled == false || _pending.Length > 1_000_000)
                    _pending.Clear();
                return;
            }

            string key = type == LogType.Debug ? "[debug] " + text : text;
            if (key == _lastLine)
            {
                _lastLineTime = time;
                if (++_repeats >= RepeatSummaryEvery)
                    AppendRepeatSummary();
                return;
            }

            AppendRepeatSummary();
            _lastLine = key;
            _pending.Append(StartsWithTime().IsMatch(text) ? key : $"[{time}] {key}").Append('\n');
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_disposed)
                FlushLocked();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            if (_disposed)
                return;

            AppendRepeatSummary();
            _disposed = true;
            FlushLocked();
        }
    }

    private void AppendRepeatSummary()
    {
        if (_repeats == 0)
            return;

        _pending.Append($"[{_lastLineTime}] (previous line repeated {_repeats} more time{(_repeats == 1 ? string.Empty : "s")})\n");
        _repeats = 0;
    }

    private void FlushLocked()
    {
        if (_pending.Length == 0)
            return;

        if (!IsEnabled())
        {
            if (_enabled == false || _pending.Length > 1_000_000)
                _pending.Clear();
            return;
        }

        string text = _pending.ToString();
        _pending.Clear();
        try
        {
            Directory.CreateDirectory(_directory);
            ClientFileSources.RestrictDirectoryPermissions(_directory);
            if (!_cleanedUp)
            {
                _cleanedUp = true;
                DeleteOldFiles();
            }
            string currentLogPath = CurrentFile();
            File.AppendAllText(currentLogPath, text, Encoding.UTF8);
            ClientFileSources.RestrictFilePermissions(currentLogPath);
        }
        catch (IOException)
        {
            // A logging failure must not break the client or recursively log itself.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private bool IsEnabled()
    {
        if (_enabled is not null && DateTime.Now - _enabledReadAt < SettingRefreshInterval)
            return _enabled.Value;

        try
        {
            _enabled = _settings.Get<bool>("SaveLogsToFile");
            _enabledReadAt = DateTime.Now;
            return _enabled.Value;
        }
        catch
        {
            return false;
        }
    }

    private string CurrentFile()
    {
        string name = $"{_account}_{DateTime.Now:yyyy-MM-dd}";
        string path = Path.Combine(_directory, name + ".log");
        for (int part = 2; File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes; part++)
            path = Path.Combine(_directory, $"{name}_{part}.log");
        return path;
    }

    private void DeleteOldFiles()
    {
        DateTime cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (string file in Directory.EnumerateFiles(_directory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\]")]
    private static partial Regex StartsWithTime();

    [GeneratedRegex(@"[^a-z0-9_.-]")]
    private static partial Regex InvalidFileNameChars();
}
