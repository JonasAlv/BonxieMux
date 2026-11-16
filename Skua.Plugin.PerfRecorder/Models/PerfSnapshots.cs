using System;
using System.Collections.Generic;

namespace Skua.Plugin.PerfRecorder.Models;

public sealed class HostPerfSnapshot
{
    public int HostPid { get; set; }
    public int ChildPid { get; set; }
    public string Mode { get; set; } = string.Empty;
    public double HostPresentedFps { get; set; }
    public double AvgFrameMs { get; set; }
    public double MaxFrameMs { get; set; }
    public double OnePercentLowFps { get; set; }
    public long TotalPresentedFrames { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MugetsuPerfSnapshot
{
    public int ChildPid { get; set; }
    public int MugetsuPid { get; set; }
    public string TransportMode { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? LastPerfUpdateUtc { get; set; }
    public string? PreferredBackend { get; set; }
    public string? GraphicsApi { get; set; }
    public string? GpuAdapter { get; set; }
    public string? GpuType { get; set; }
    public string? SurfaceFormat { get; set; }
    public string? InterpolationState { get; set; }
    public double GameFps { get; set; }
    public double DrawnFps { get; set; }
    public double PresentedFps { get; set; }
    public double FrameAvgMs { get; set; }
    public double FrameMaxMs { get; set; }
    public double WorstGapMs { get; set; }
    public int Hitches { get; set; }
    public double RenderAvgMs { get; set; }
    public double RenderMaxMs { get; set; }
    public int WindowCount { get; set; }
    public string? PerDrawnFrameCounters { get; set; }
    public string? RenderTimeBreakdown { get; set; }
    public string? InterpolationReplay { get; set; }
    public string? ObjectsReport { get; set; }
    public string? MemoryReport { get; set; }
    public long ProcessCommitMb { get; set; }
    public long RustHeapMb { get; set; }
    public int SlowFrameCount { get; set; }
    public int SlowRenderCount { get; set; }
    public string? LastSlowEvent { get; set; }
    public List<string> RecentPerfLogs { get; set; } = new();
}

public readonly record struct ProcUsage(int Pid, double CpuPercent, double RssMb);

public sealed class PerfLiveState
{
    public DateTime SampledAtLocal { get; init; }
    public HostPerfSnapshot? Host { get; init; }
    public MugetsuPerfSnapshot? Mugetsu { get; init; }
    public ProcUsage MugetsuProc { get; init; }
    public ProcUsage HostProc { get; init; }
    public ProcUsage TabProc { get; init; }
    public bool IsHostSeparateProcess { get; init; }
    public string TransportMode { get; init; } = "Unknown";
    public string GraphicsBackend { get; init; } = "Waiting for Mugetsu...";
    public bool IsRecording { get; init; }
    public TimeSpan RecordingElapsed { get; init; }
    public int RecordedSamples { get; init; }
    public int RecordedMugetsuLogCount { get; init; }
}

public sealed class PerfSampleRow
{
    public TimeSpan Elapsed { get; init; }
    public DateTime TimestampLocal { get; init; }
    public string TransportMode { get; init; } = string.Empty;
    public string GraphicsApi { get; init; } = string.Empty;
    public double HostPresentedFps { get; init; }
    public double HostAvgMs { get; init; }
    public double HostMaxMs { get; init; }
    public double HostOnePercentLowFps { get; init; }
    public long TotalPresentedFrames { get; init; }
    public double MugetsuGameFps { get; init; }
    public double MugetsuDrawnFps { get; init; }
    public double MugetsuPresentedFps { get; init; }
    public double MugetsuFrameAvgMs { get; init; }
    public double MugetsuFrameMaxMs { get; init; }
    public double MugetsuWorstGapMs { get; init; }
    public int MugetsuHitches { get; init; }
    public int MugetsuWindowCount { get; init; }
    public double MugetsuRenderAvgMs { get; init; }
    public double MugetsuRenderMaxMs { get; init; }
    public long MugetsuCommitMb { get; init; }
    public long MugetsuRustHeapMb { get; init; }
    public double MugetsuCpuPct { get; init; }
    public double MugetsuRssMb { get; init; }
    public double HostCpuPct { get; init; }
    public double HostRssMb { get; init; }
    public double TabCpuPct { get; init; }
    public double TabRssMb { get; init; }
}
