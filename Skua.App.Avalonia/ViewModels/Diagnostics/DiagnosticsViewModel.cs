using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Models.Diagnostics;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.ViewModels.Diagnostics;

public sealed partial class DiagnosticsViewModel : BotControlViewModelBase, IDisposable
{
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IFileDialogService _fileDialogService;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;

    public DiagnosticsViewModel(
        IDiagnosticsService diagnosticsService,
        IDispatcherService dispatcherService,
        IFileDialogService fileDialogService,
        IDialogService dialogService)
        : base("Diagnostics", 900, 600)
    {
        _diagnosticsService = diagnosticsService;
        _dispatcherService = dispatcherService;
        _fileDialogService = fileDialogService;
        _dialogService = dialogService;
        Refresh();
    }

    [ObservableProperty]
    private string _monitoringStatus = "Monitoring disabled";

    [ObservableProperty]
    private string _lastUpdated = "No snapshot available";

    [ObservableProperty]
    private string _process = "-";

    [ObservableProperty]
    private string _cpuTime = "-";

    [ObservableProperty]
    private string _workingSet = "-";

    [ObservableProperty]
    private string _managedHeap = "-";

    [ObservableProperty]
    private string _totalAllocated = "-";

    [ObservableProperty]
    private string _collections = "-";

    [ObservableProperty]
    private string _threads = "-";

    [ObservableProperty]
    private string _threadPool = "-";
    public ObservableCollection<DiagnosticEventRow> Events { get; } = new();

    protected override void OnActivated()
    {
        base.OnActivated();
        Refresh();

        if (_refreshTask is not null)
            return;

        _refreshCancellation = new CancellationTokenSource();
        _refreshTask = RefreshLoopAsync(_refreshCancellation.Token);
    }

    protected override void OnDeactivated()
    {
        StopRefresh();
        base.OnDeactivated();
    }

    [RelayCommand]
    private async Task Export()
    {
        string? path = _fileDialogService.Save("JSON Files (*.json)|*.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            await _diagnosticsService.ExportAsync(path);
            LastUpdated = $"Exported: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            _dialogService.ShowMessageBox($"Could not export diagnostics.\r\n{exception.Message}", "Diagnostics Export Error");
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        DiagnosticSnapshot? snapshot = _diagnosticsService.LatestSnapshot;
        IReadOnlyList<DiagnosticEvent> events = _diagnosticsService.GetRecentEvents();

        _dispatcherService.Invoke(() => ApplySnapshot(snapshot, events));
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                Refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplySnapshot(DiagnosticSnapshot? snapshot, IReadOnlyList<DiagnosticEvent> events)
    {
        MonitoringStatus = _diagnosticsService.Enabled
            ? "Monitoring enabled"
            : "Monitoring disabled - set SKUA_DEBUG_MONITORING=1 and restart";

        if (snapshot is not null)
        {
            LastUpdated = snapshot.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            Process = $"{snapshot.ProcessId}";
            CpuTime = $"{snapshot.ProcessCpuMilliseconds:N0} ms";
            WorkingSet = FormatBytes(snapshot.WorkingSetBytes);
            ManagedHeap = FormatBytes(snapshot.ManagedHeapBytes);
            TotalAllocated = FormatBytes(snapshot.TotalAllocatedBytes);
            Collections = $"Gen 0: {snapshot.Gen0Collections:N0} | Gen 1: {snapshot.Gen1Collections:N0} | Gen 2: {snapshot.Gen2Collections:N0}";
            Threads = $"{snapshot.ThreadCount:N0}";
            ThreadPool = $"{snapshot.ThreadPoolAvailableWorkers:N0} available / {snapshot.ThreadPoolMaxWorkers:N0} max";
        }

        Events.Clear();
        foreach (DiagnosticEvent diagnosticEvent in events)
            Events.Add(DiagnosticEventRow.From(diagnosticEvent));
    }

    public void Dispose()
    {
        StopRefresh();
        GC.SuppressFinalize(this);
    }

    private void StopRefresh()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _refreshTask = null;
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:N1} MB";
}

public sealed record DiagnosticEventRow(string Timestamp, string Category, string Name, string Value)
{
    public static DiagnosticEventRow From(DiagnosticEvent diagnosticEvent)
    {
        string value = diagnosticEvent.Value is double numericValue
            ? $"{numericValue:N2} {diagnosticEvent.Unit}".TrimEnd()
            : string.Empty;

        return new(
            diagnosticEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss"),
            diagnosticEvent.Category,
            diagnosticEvent.Name,
            value);
    }
}
