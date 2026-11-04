using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using System.Diagnostics;

namespace Skua.Core.Services;

public class LogService : ObservableRecipient, ILogService, IDisposable
{
    private readonly DebugListener _debugListener;
    private const int MaxLogEntries = 10000;
    private readonly object _debugLock = new();
    private readonly object _scriptLock = new();
    private readonly object _flashLock = new();
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
        }
        Messenger.Send(new LogsChangedMessage(logType));
    }

    public List<string> GetLogs(LogType logType)
    {
        return logType switch
        {
            LogType.Debug => new List<string>(_debugLogs),
            LogType.Script => new List<string>(_scriptLogs),
            LogType.Flash => new List<string>(_flashLogs),
            _ => new()
        };
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
