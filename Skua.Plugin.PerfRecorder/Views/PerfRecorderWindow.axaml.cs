using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Skua.Plugin.PerfRecorder.Models;
using Skua.Plugin.PerfRecorder.Services;
using Skua.Shared.Avalonia.Theming;

namespace Skua.Plugin.PerfRecorder.Views;

public partial class PerfRecorderWindow : Window
{
    private readonly PerfTelemetryService _service;
    private string? _lastSavedReportPath;

    public PerfRecorderWindow()
    {
        InitializeComponent();
        _service = null!;
    }

    public PerfRecorderWindow(PerfTelemetryService service)
    {
        _service = service;
        InitializeComponent();
        Skua.Shared.Avalonia.Services.ShellWindowHelper.ConfigureFramelessWindow(this);
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(this);

        Opened += (_, _) =>
        {
            _service.TelemetryUpdated += OnTelemetryUpdated;
            _service.AcquirePolling();
            if (_service.LatestState is { } existing)
                ApplyState(existing);
        };

        Closing += (_, _) =>
        {
            _service.TelemetryUpdated -= OnTelemetryUpdated;
            _service.ReleasePolling();
        };
    }

    private void OnTelemetryUpdated(PerfLiveState state)
    {
        Dispatcher.UIThread.Post(() => ApplyState(state));
    }

    private void ApplyState(PerfLiveState state)
    {
        MugetsuPerfSnapshot? mug = state.Mugetsu;
        HostPerfSnapshot? host = state.Host;

        TxtTransportMode.Text = state.TransportMode;
        TxtGraphicsApi.Text = state.GraphicsBackend;
        TxtGpuAdapter.Text = mug?.GpuAdapter != null
            ? $"{mug.GpuAdapter} ({mug.GpuType ?? "?"})"
            : "--";
        TxtSurfaceFormat.Text = mug?.SurfaceFormat ?? "--";
        TxtInterpolation.Text = mug?.InterpolationState ?? "--";

        TxtMugPid.Text = state.MugetsuProc.Pid > 0 ? state.MugetsuProc.Pid.ToString(CultureInfo.InvariantCulture) : "--";
        TxtMugCpu.Text = state.MugetsuProc.Pid > 0 ? $"{state.MugetsuProc.CpuPercent:F1}%" : "--";
        TxtMugRam.Text = state.MugetsuProc.Pid > 0 ? $"{state.MugetsuProc.RssMb:F0} MB" : "--";

        TxtHostPid.Text = state.IsHostSeparateProcess && state.HostProc.Pid > 0
            ? state.HostProc.Pid.ToString(CultureInfo.InvariantCulture)
            : (state.IsHostSeparateProcess ? "--" : "same");
        TxtHostCpu.Text = state.IsHostSeparateProcess && state.HostProc.Pid > 0 ? $"{state.HostProc.CpuPercent:F1}%" : "--";
        TxtHostRam.Text = state.IsHostSeparateProcess && state.HostProc.Pid > 0 ? $"{state.HostProc.RssMb:F0} MB" : "--";

        TxtTabPid.Text = state.TabProc.Pid > 0 ? state.TabProc.Pid.ToString(CultureInfo.InvariantCulture) : "--";
        TxtTabCpu.Text = state.TabProc.Pid > 0 ? $"{state.TabProc.CpuPercent:F1}%" : "--";
        TxtTabRam.Text = state.TabProc.Pid > 0 ? $"{state.TabProc.RssMb:F0} MB" : "--";

        double totalCpu = state.MugetsuProc.CpuPercent + state.HostProc.CpuPercent + state.TabProc.CpuPercent;
        double totalRam = state.MugetsuProc.RssMb + state.HostProc.RssMb + state.TabProc.RssMb;
        TxtRustMem.Text = mug is { RustHeapMb: > 0 } ? $"Heap {mug.RustHeapMb} MB" : "Heap --";
        TxtTotalCpu.Text = $"{totalCpu:F1}%";
        TxtTotalRam.Text = $"{totalRam:F0} MB";

        if (host != null)
        {
            TxtHostFps.Text = $"{host.HostPresentedFps:F1} FPS";
            TxtHostOnePctLow.Text = $"{host.OnePercentLowFps:F1} FPS";
            TxtHostFrameMs.Text = $"{host.AvgFrameMs:F2} ms / {host.MaxFrameMs:F2} ms";
            TxtHostTotalFrames.Text = host.TotalPresentedFrames.ToString("N0", CultureInfo.InvariantCulture);
        }
        else
        {
            TxtHostFps.Text = "--";
            TxtHostOnePctLow.Text = "--";
            TxtHostFrameMs.Text = "--";
            TxtHostTotalFrames.Text = "--";
        }

        if (mug != null && mug.WindowCount > 0)
        {
            TxtMugFpsTriple.Text = $"{mug.GameFps:F1} / {mug.DrawnFps:F1} / {mug.PresentedFps:F1} FPS";
            TxtMugFrameMs.Text = $"{mug.FrameAvgMs:F1} ms / {mug.FrameMaxMs:F1} ms";
            TxtMugRenderMs.Text = $"{mug.RenderAvgMs:F1} ms / {mug.RenderMaxMs:F1} ms";
            TxtMugHitches.Text = $"gap {mug.WorstGapMs:F0} ms | hitches {mug.Hitches} | slow {mug.SlowFrameCount}/{mug.SlowRenderCount}";
        }
        else
        {
            TxtMugFpsTriple.Text = "waiting for 5s window...";
            TxtMugFrameMs.Text = "--";
            TxtMugRenderMs.Text = "--";
            TxtMugHitches.Text = "--";
        }

        TxtRenderBreakdown.Text = !string.IsNullOrWhiteSpace(mug?.RenderTimeBreakdown)
            ? $"render: {mug.RenderTimeBreakdown}"
            : "render time per drawn frame: waiting for first 5s summary...";
        TxtPerDrawn.Text = !string.IsNullOrWhiteSpace(mug?.PerDrawnFrameCounters)
            ? $"per drawn frame: {mug.PerDrawnFrameCounters}"
            : "per drawn frame: --";

        if (!string.IsNullOrWhiteSpace(mug?.MemoryReport) || !string.IsNullOrWhiteSpace(mug?.ObjectsReport))
        {
            string memPart = !string.IsNullOrWhiteSpace(mug?.MemoryReport) ? $"memory: {mug.MemoryReport}" : string.Empty;
            string objPart = !string.IsNullOrWhiteSpace(mug?.ObjectsReport) ? $"objects: {mug.ObjectsReport}" : string.Empty;
            TxtMemoryObjects.Text = string.Join(Environment.NewLine, new[] { objPart, memPart }.Where(x => !string.IsNullOrEmpty(x)));
        }

        if (mug?.RecentPerfLogs is { Count: > 0 } logs)
        {
            TxtPerfLogStream.Text = string.Join(Environment.NewLine, logs);
            LogScrollViewer.ScrollToEnd();
        }

        UpdateRecordingControls(state.IsRecording, state.RecordingElapsed, state.RecordedSamples);
    }

    private void UpdateRecordingControls(bool isRecording, TimeSpan elapsed, int samples)
    {
        if (isRecording)
        {
            RecordingStatusText.Text = $"REC {elapsed:mm\\:ss} ({samples}s)";
            RecordingStatusText.Foreground = UiTokens.GetBrush(UiTokens.SkuaDangerBrush, Brushes.OrangeRed);
            RecordBtnText.Text = "Stop & Save .txt";
            BtnToggleRecord.Classes.Set("skua-primary", false);
            BtnToggleRecord.Classes.Set("skua-danger", true);
        }
        else
        {
            RecordingStatusText.Text = "IDLE";
            RecordingStatusText.Foreground = UiTokens.GetBrush(UiTokens.SkuaMutedTextBrush, Brushes.Gray);
            RecordBtnText.Text = "Start Recording";
            BtnToggleRecord.Classes.Set("skua-danger", false);
            BtnToggleRecord.Classes.Set("skua-primary", true);
        }
    }

    private void BtnToggleRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (_service.IsRecording)
        {
            string path = _service.StopAndSaveReport(TestLabelBox.Text);
            ShowSavedFile(path);
        }
        else
        {
            _service.StartRecording();
            TxtSavedStatus.Text = "Recording live telemetry every 1s + ruffle_aqw_perf windows...";
            BtnCopyPath.IsVisible = false;
        }
    }

    private void BtnSnapshot_Click(object? sender, RoutedEventArgs e)
    {
        string path = _service.SaveSnapshotReport(TestLabelBox.Text);
        ShowSavedFile(path);
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _service.ReportsDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { dir },
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            TxtSavedStatus.Text = $"Reports dir: {_service.ReportsDirectory} ({ex.Message})";
        }
    }

    private async void BtnCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSavedReportPath))
            return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(_lastSavedReportPath);
                TxtSavedStatus.Text = $"Copied path: {_lastSavedReportPath}";
            }
        }
        catch { }
    }

    private void ShowSavedFile(string path)
    {
        _lastSavedReportPath = path;
        TxtSavedStatus.Text = $"Saved report: {path}";
        BtnCopyPath.IsVisible = true;
    }
}
