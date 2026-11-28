using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Skua.Core.Services;

public class LogService : ObservableRecipient, ILogService, IDisposable
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private readonly DebugListener _debugListener;
    private const int MaxLogEntries = 5000;
    private readonly object _debugLock = new();
    private readonly object _scriptLock = new();
    private readonly object _flashLock = new();
    private readonly object _mugetsuLock = new();
    private readonly LogFileWriter _fileWriter;

    public LogService(ISettingsService settingsService)
    {
        _fileWriter = new LogFileWriter(settingsService, Path.Combine(ClientFileSources.BonxieMuxDIR, "logs"));
        _debugListener = new DebugListener(this);
        Trace.Listeners.Add(_debugListener);
        Messenger.Register<LogService, FlashErrorMessage>(this, LogFlashError);
    }

    public void SetAccountName(string? accountName) => _fileWriter.SetAccount(accountName);

    private void LogFlashError(LogService recipient, FlashErrorMessage message)
    {
        bool isLogin = string.Equals(message.Function, "login", StringComparison.OrdinalIgnoreCase);
        string formattedArgs = message.Args.Length > 0
            ? $"= {{{string.Join(",", message.Args.Select((a, idx) => isLogin && idx > 0 ? "***" : a?.ToString()))}}}"
            : string.Empty;
        recipient.FlashLog($"{message.Function} Args[{message.Args.Length}] {formattedArgs}");
    }

    private readonly List<string> _debugLogs = new();
    private readonly List<string> _scriptLogs = new();
    private readonly List<string> _flashLogs = new();
    private readonly List<string> _mugetsuLogs = new();

    public void DebugLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Contains("PlatformImpl is null"))
            return;

        lock (_debugLock)
        {
            if (_debugLogs.Count >= MaxLogEntries)
                _debugLogs.RemoveAt(0);
            _debugLogs.Add(message);
        }
        Messenger.Send(new LogsChangedMessage(LogType.Debug));
        Messenger.Send(new AddLogMessage(LogType.Debug, message));
        _fileWriter.Add(LogType.Debug, message);
    }

    public void FlashLog(string? message)
    {
        if (message is null)
            return;
        lock (_flashLock)
        {
            if (_flashLogs.Count >= MaxLogEntries)
                _flashLogs.RemoveAt(0);
            _flashLogs.Add(message);
        }
        Messenger.Send(new LogsChangedMessage(LogType.Flash));
        Messenger.Send(new AddLogMessage(LogType.Flash, message));
    }

    public void ScriptLog(string? message)
    {
        if (message is null)
            return;
        lock (_scriptLock)
        {
            if (_scriptLogs.Count >= MaxLogEntries)
                _scriptLogs.RemoveAt(0);
            _scriptLogs.Add(message);
        }
        Messenger.Send(new LogsChangedMessage(LogType.Script));
        Messenger.Send(new AddLogMessage(LogType.Script, message));
        _fileWriter.Add(LogType.Script, message);
    }

    public void MugetsuLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        string clean = AnsiEscapeRegex.Replace(message, string.Empty).TrimEnd();
        if (string.IsNullOrWhiteSpace(clean) || IsHarmlessMugetsuStartupNoise(clean))
            return;
        lock (_mugetsuLock)
        {
            if (_mugetsuLogs.Count >= MaxLogEntries)
                _mugetsuLogs.RemoveAt(0);
            _mugetsuLogs.Add(clean);
        }
        Messenger.Send(new LogsChangedMessage(LogType.Mugetsu));
        Messenger.Send(new AddLogMessage(LogType.Mugetsu, clean));
        if (ShouldPersistMugetsuLine(clean))
            _fileWriter.Add(LogType.Mugetsu, clean);
    }

    private static bool IsHarmlessMugetsuStartupNoise(string line) =>
        line.Contains("Failed to register [SansSerif] as Proportional", StringComparison.Ordinal)
        || line.Contains("ruffle_desktop::backends::ui::fontconfig: Skipping font", StringComparison.Ordinal)
        || line.Contains("ruffle_core::library: Failed to load device font from file", StringComparison.Ordinal);

    private static bool ShouldPersistMugetsuLine(string line)
    {
        // Skip repetitive 5-second perf telemetry lines on disk (still parsed in memory for PerfRecorder).
        if (line.Contains("ruffle_aqw_perf:", StringComparison.Ordinal))
            return false;

        // Always persist warnings, errors, panics, bridge lifecycle, and startup/graphics info.
        return line.Contains(" WARN ", StringComparison.Ordinal)
            || line.Contains(" ERROR ", StringComparison.Ordinal)
            || line.Contains("panic", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Skua bridge", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Using graphics API", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Using preferred backend", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Using fallback backend", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Mugetsu ", StringComparison.Ordinal);
    }

    public void ClearLog(LogType logType)
    {
        switch (logType)
        {
            case LogType.Debug:
                lock (_debugLock)
                    _debugLogs.Clear();
                break;

            case LogType.Script:
                lock (_scriptLock)
                    _scriptLogs.Clear();
                break;

            case LogType.Flash:
                lock (_flashLock)
                    _flashLogs.Clear();
                break;

            case LogType.Mugetsu:
                lock (_mugetsuLock)
                    _mugetsuLogs.Clear();
                break;
        }
        Messenger.Send(new LogsChangedMessage(logType));
    }

    public List<string> GetLogs(LogType logType)
    {
        switch (logType)
        {
            case LogType.Debug:
                lock (_debugLock) return new List<string>(_debugLogs);
            case LogType.Script:
                lock (_scriptLock) return new List<string>(_scriptLogs);
            case LogType.Flash:
                lock (_flashLock) return new List<string>(_flashLogs);
            case LogType.Mugetsu:
                lock (_mugetsuLock) return new List<string>(_mugetsuLogs);
            default:
                return new();
        }
    }

    private bool _disposed = false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _fileWriter.Dispose();

            // Remove the trace listener
            if (_debugListener != null)
            {
                Trace.Listeners.Remove(_debugListener);
            }

            // Unregister from messenger
            Messenger.UnregisterAll(this);

            // Clear log collections
            _debugLogs.Clear();
            _scriptLogs.Clear();
            _flashLogs.Clear();
            _mugetsuLogs.Clear();
        }

        _disposed = true;
    }

    ~LogService()
    {
        Dispose(false);
    }
}

public class DebugListener : TraceListener
{
    private readonly ILogService _logService;

    public DebugListener(ILogService logService)
    {
        _logService = logService;
    }

    public override void Write(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Contains("PlatformImpl is null"))
            return;
        _logService.DebugLog(message);
    }

    public override void WriteLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Contains("PlatformImpl is null"))
            return;
        _logService.DebugLog(message);
    }
}
