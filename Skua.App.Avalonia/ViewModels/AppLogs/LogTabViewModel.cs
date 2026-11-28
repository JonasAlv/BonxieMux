using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Skua.App.Avalonia.ViewModels.AppLogs;

public partial class LogTabViewModel : ObservableRecipient
{
    private const int MaxUiEntries = 2000;
    private readonly ILogService _logService;
    private readonly IDispatcherService _dispatcherService;
    private readonly LogType _logType;
    private readonly List<LogEntry> _allLogs = new();

    private string _searchText = string.Empty;
    private LogFilterMode _filterMode = LogFilterMode.All;

    public LogTabViewModel(string title, ILogService logService, IDispatcherService dispatcherService, LogType logType)
    {
        Title = title;
        _logService = logService;
        _dispatcherService = dispatcherService;
        _logType = logType;

        // Hydrate any logs that were recorded before this view was constructed
        List<string> existing = _logService.GetLogs(_logType);
        int start = Math.Max(0, existing.Count - MaxUiEntries);
        for (int i = start; i < existing.Count; i++)
        {
            LogEntry entry = new(existing[i], _logType);
            _allLogs.Add(entry);
            Logs.Add(entry);
        }

        Messenger.Register<LogTabViewModel, AddLogMessage>(this, AddLog);
    }

    public string Title { get; }
    public RangedObservableCollection<LogEntry> Logs { get; } = new();

    public int TotalCount => _allLogs.Count;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
                RebuildFilteredLogs();
        }
    }

    public LogFilterMode FilterMode
    {
        get => _filterMode;
        set
        {
            if (SetProperty(ref _filterMode, value))
            {
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterIssues));
                OnPropertyChanged(nameof(IsFilterSystem));
                RebuildFilteredLogs();
            }
        }
    }

    public bool IsFilterAll => _filterMode == LogFilterMode.All;
    public bool IsFilterIssues => _filterMode == LogFilterMode.Issues;
    public bool IsFilterSystem => _filterMode == LogFilterMode.System;

    [RelayCommand]
    private void SetFilterAll() => FilterMode = LogFilterMode.All;

    [RelayCommand]
    private void SetFilterIssues() => FilterMode = LogFilterMode.Issues;

    [RelayCommand]
    private void SetFilterSystem() => FilterMode = LogFilterMode.System;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void SaveLog()
    {
        Ioc.Default.GetRequiredService<IFileDialogService>().SaveText(LogsToString(Logs));
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logService.ClearLog(_logType);
        _allLogs.Clear();
        Logs.Clear();
        OnPropertyChanged(nameof(TotalCount));
    }

    [RelayCommand]
    private void CopyLog()
    {
        Ioc.Default.GetRequiredService<IClipboardService>().SetText(LogsToString(Logs));
    }

    private void RebuildFilteredLogs()
    {
        Logs.Clear();
        List<LogEntry> matching = _allLogs.Where(e => e.Matches(_searchText, _filterMode)).ToList();
        if (matching.Count > 0)
            Logs.AddRange(matching);
    }

    private void AddLog(LogTabViewModel recipient, AddLogMessage message)
    {
        if (message.LogType != recipient._logType)
            return;

        LogEntry entry = new(message.Text, recipient._logType);
        _dispatcherService.Invoke(() =>
        {
            recipient._allLogs.Add(entry);
            if (recipient._allLogs.Count > MaxUiEntries)
            {
                LogEntry removed = recipient._allLogs[0];
                recipient._allLogs.RemoveAt(0);
                if (recipient.Logs.Count > 0 && ReferenceEquals(recipient.Logs[0], removed))
                    recipient.Logs.RemoveAt(0);
            }

            if (entry.Matches(recipient._searchText, recipient._filterMode))
                recipient.Logs.Add(entry);

            recipient.OnPropertyChanged(nameof(TotalCount));
        });
    }

    private static string LogsToString(IEnumerable<LogEntry> logs)
    {
        return string.Join(Environment.NewLine, logs.Select(l => l.RawText));
    }
}
