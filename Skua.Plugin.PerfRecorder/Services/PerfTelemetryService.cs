using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Skua.Core.Models;
using Skua.Plugin.PerfRecorder.Models;

namespace Skua.Plugin.PerfRecorder.Services;

public sealed class PerfTelemetryService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly double ClockTicksPerSecond = QueryClockTicksPerSecond();

    [DllImport("libc", EntryPoint = "sysconf")]
    private static extern long SysConf(int name);

    private static double QueryClockTicksPerSecond()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const int ScClkTck = 2;
                long val = SysConf(ScClkTck);
                if (val > 0)
                    return val;
            }
        }
        catch { }
        return 100.0;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, (ulong ProcTicks, long WallTicks)> _prevCpu = new();
    private readonly List<PerfSampleRow> _samples = new();
    private readonly List<string> _recordedPerfLogs = new();
    private readonly HashSet<string> _seenLogLines = new(StringComparer.Ordinal);

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _activeConsumers;
    private bool _isRecording;
    private DateTime _recordingStartLocal;
    private long _recordingStartTimestamp;
    private int _startSlowFrames;
    private int _startSlowRenders;
    private PerfLiveState? _latestState;

    public event Action<PerfLiveState>? TelemetryUpdated;

    public PerfLiveState? LatestState
    {
        get { lock (_lock) return _latestState; }
    }

    public bool IsRecording
    {
        get { lock (_lock) return _isRecording; }
    }

    public string ReportsDirectory => Path.Combine(ClientFileSources.SkuaDIR, "perf-reports");

    public void AcquirePolling()
    {
        lock (_lock)
        {
            _activeConsumers++;
            EnsurePollingStartedLocked();
        }
    }

    public void ReleasePolling()
    {
        CancellationTokenSource? toStop = null;
        lock (_lock)
        {
            if (_activeConsumers > 0)
                _activeConsumers--;
            if (_activeConsumers == 0 && !_isRecording)
            {
                toStop = _pollCts;
                _pollCts = null;
            }
        }

        if (toStop != null)
        {
            try { toStop.Cancel(); } catch { }
            toStop.Dispose();
        }
    }

    public void StartRecording()
    {
        PerfLiveState immediate = CollectOnceLockedState();
        lock (_lock)
        {
            _samples.Clear();
            _recordedPerfLogs.Clear();
            _seenLogLines.Clear();
            _isRecording = true;
            _recordingStartLocal = DateTime.Now;
            _recordingStartTimestamp = Stopwatch.GetTimestamp();
            _startSlowFrames = immediate.Mugetsu?.SlowFrameCount ?? 0;
            _startSlowRenders = immediate.Mugetsu?.SlowRenderCount ?? 0;

            if (immediate.Mugetsu?.RecentPerfLogs is { Count: > 0 } existingLogs)
            {
                foreach (string line in existingLogs)
                {
                    if (_seenLogLines.Add(line))
                        _recordedPerfLogs.Add(line);
                }
            }

            RecordSampleLocked(immediate);
            EnsurePollingStartedLocked();
        }

        TelemetryUpdated?.Invoke(LatestState ?? immediate);
    }

    public string StopAndSaveReport(string? customLabel)
    {
        List<PerfSampleRow> samples;
        List<string> rawLogs;
        PerfLiveState? finalState;
        DateTime startLocal;
        DateTime endLocal = DateTime.Now;
        int slowFramesDelta;
        int slowRendersDelta;
        CancellationTokenSource? toStop = null;

        lock (_lock)
        {
            finalState = _latestState ?? SampleInternalLocked();
            startLocal = _recordingStartLocal == default ? endLocal : _recordingStartLocal;
            samples = new List<PerfSampleRow>(_samples);
            rawLogs = new List<string>(_recordedPerfLogs);
            slowFramesDelta = Math.Max(0, (finalState.Mugetsu?.SlowFrameCount ?? 0) - _startSlowFrames);
            slowRendersDelta = Math.Max(0, (finalState.Mugetsu?.SlowRenderCount ?? 0) - _startSlowRenders);

            _isRecording = false;
            if (_activeConsumers == 0)
            {
                toStop = _pollCts;
                _pollCts = null;
            }
        }

        if (toStop != null)
        {
            try { toStop.Cancel(); } catch { }
            toStop.Dispose();
        }

        string path = WriteReportFile(
            customLabel,
            startLocal,
            endLocal,
            finalState,
            samples,
            rawLogs,
            slowFramesDelta,
            slowRendersDelta);

        TelemetryUpdated?.Invoke(SampleNow());
        return path;
    }

    public string SaveSnapshotReport(string? customLabel)
    {
        PerfLiveState state = SampleNow();
        DateTime now = DateTime.Now;
        List<PerfSampleRow> rows = new() { BuildSampleRow(state, TimeSpan.Zero) };
        List<string> logs = state.Mugetsu?.RecentPerfLogs != null
            ? new List<string>(state.Mugetsu.RecentPerfLogs)
            : new List<string>();

        return WriteReportFile(
            string.IsNullOrWhiteSpace(customLabel) ? "Instant Snapshot" : customLabel,
            now,
            now,
            state,
            rows,
            logs,
            state.Mugetsu?.SlowFrameCount ?? 0,
            state.Mugetsu?.SlowRenderCount ?? 0);
    }

    public PerfLiveState SampleNow()
    {
        PerfLiveState state;
        lock (_lock)
        {
            state = SampleInternalLocked();
        }
        TelemetryUpdated?.Invoke(state);
        return state;
    }

    private PerfLiveState CollectOnceLockedState()
    {
        lock (_lock)
            return SampleInternalLocked();
    }

    private void EnsurePollingStartedLocked()
    {
        if (_pollCts != null)
            return;
        _pollCts = new CancellationTokenSource();
        CancellationToken ct = _pollCts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(ct), ct);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            PerfLiveState initial;
            lock (_lock)
                initial = SampleInternalLocked();
            TelemetryUpdated?.Invoke(initial);

            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                PerfLiveState state;
                lock (_lock)
                {
                    state = SampleInternalLocked();
                    if (_isRecording)
                        RecordSampleLocked(state);
                }
                TelemetryUpdated?.Invoke(state);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private PerfLiveState SampleInternalLocked()
    {
        int childPid = Environment.ProcessId;
        HostPerfSnapshot? hostSnap = ReadJsonFile<HostPerfSnapshot>(GetShmFilePath($"skua-host-perf-{childPid}.json"));
        MugetsuPerfSnapshot? mugetsuSnap = ReadJsonFile<MugetsuPerfSnapshot>(GetShmFilePath($"skua-mugetsu-perf-{childPid}.json"));

        if (hostSnap != null && (DateTime.UtcNow - hostSnap.UpdatedAtUtc).TotalSeconds > 3.0)
            hostSnap.HostPresentedFps = 0;

        int mugetsuPid = mugetsuSnap?.MugetsuPid ?? 0;
        int hostPid = hostSnap?.HostPid ?? 0;
        bool separateHost = hostPid > 0 && hostPid != childPid;

        ProcUsage mugetsuProc = mugetsuPid > 0 ? SampleProcessLocked(mugetsuPid) : default;
        ProcUsage hostProc = separateHost ? SampleProcessLocked(hostPid) : default;
        ProcUsage tabProc = SampleProcessLocked(childPid);

        string mode = !string.IsNullOrWhiteSpace(mugetsuSnap?.TransportMode)
            ? mugetsuSnap.TransportMode
            : !string.IsNullOrWhiteSpace(hostSnap?.Mode)
                ? hostSnap.Mode
                : "Unknown";

        string backend = !string.IsNullOrWhiteSpace(mugetsuSnap?.GraphicsApi)
            ? mugetsuSnap.GraphicsApi
            : !string.IsNullOrWhiteSpace(mugetsuSnap?.PreferredBackend)
                ? mugetsuSnap.PreferredBackend
                : "Waiting for Mugetsu...";

        if (_isRecording && mugetsuSnap?.RecentPerfLogs is { Count: > 0 } logs)
        {
            foreach (string line in logs)
            {
                if (_seenLogLines.Add(line))
                    _recordedPerfLogs.Add(line);
            }
        }

        TimeSpan elapsed = _isRecording && _recordingStartTimestamp != 0
            ? TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - _recordingStartTimestamp) / Stopwatch.Frequency)
            : TimeSpan.Zero;

        _latestState = new PerfLiveState
        {
            SampledAtLocal = DateTime.Now,
            Host = hostSnap,
            Mugetsu = mugetsuSnap,
            MugetsuProc = mugetsuProc,
            HostProc = hostProc,
            TabProc = tabProc,
            IsHostSeparateProcess = separateHost,
            TransportMode = mode,
            GraphicsBackend = backend,
            IsRecording = _isRecording,
            RecordingElapsed = elapsed,
            RecordedSamples = _samples.Count,
            RecordedMugetsuLogCount = _recordedPerfLogs.Count
        };
        return _latestState;
    }

    private void RecordSampleLocked(PerfLiveState state)
    {
        TimeSpan elapsed = _recordingStartTimestamp != 0
            ? TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - _recordingStartTimestamp) / Stopwatch.Frequency)
            : TimeSpan.Zero;
        _samples.Add(BuildSampleRow(state, elapsed));
    }

    private static PerfSampleRow BuildSampleRow(PerfLiveState state, TimeSpan elapsed) => new()
    {
        Elapsed = elapsed,
        TimestampLocal = state.SampledAtLocal,
        TransportMode = state.TransportMode,
        GraphicsApi = state.GraphicsBackend,
        HostPresentedFps = state.Host?.HostPresentedFps ?? 0,
        HostAvgMs = state.Host?.AvgFrameMs ?? 0,
        HostMaxMs = state.Host?.MaxFrameMs ?? 0,
        HostOnePercentLowFps = state.Host?.OnePercentLowFps ?? 0,
        TotalPresentedFrames = state.Host?.TotalPresentedFrames ?? 0,
        MugetsuGameFps = state.Mugetsu?.GameFps ?? 0,
        MugetsuDrawnFps = state.Mugetsu?.DrawnFps ?? 0,
        MugetsuPresentedFps = state.Mugetsu?.PresentedFps ?? 0,
        MugetsuFrameAvgMs = state.Mugetsu?.FrameAvgMs ?? 0,
        MugetsuFrameMaxMs = state.Mugetsu?.FrameMaxMs ?? 0,
        MugetsuWorstGapMs = state.Mugetsu?.WorstGapMs ?? 0,
        MugetsuHitches = state.Mugetsu?.Hitches ?? 0,
        MugetsuWindowCount = state.Mugetsu?.WindowCount ?? 0,
        MugetsuRenderAvgMs = state.Mugetsu?.RenderAvgMs ?? 0,
        MugetsuRenderMaxMs = state.Mugetsu?.RenderMaxMs ?? 0,
        MugetsuCommitMb = state.Mugetsu?.ProcessCommitMb ?? 0,
        MugetsuRustHeapMb = state.Mugetsu?.RustHeapMb ?? 0,
        MugetsuCpuPct = state.MugetsuProc.CpuPercent,
        MugetsuRssMb = state.MugetsuProc.RssMb,
        HostCpuPct = state.HostProc.CpuPercent,
        HostRssMb = state.HostProc.RssMb,
        TabCpuPct = state.TabProc.CpuPercent,
        TabRssMb = state.TabProc.RssMb
    };

    private ProcUsage SampleProcessLocked(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsLinux())
            return default;

        double rssMb = ReadProcRssMb(pid);
        if (!TryReadProcCpuTicks(pid, out ulong procTicks))
            return new ProcUsage(pid, 0, rssMb);

        long nowTicks = Stopwatch.GetTimestamp();
        double cpuPct = 0;
        if (_prevCpu.TryGetValue(pid, out var prev))
        {
            double wallSec = (double)(nowTicks - prev.WallTicks) / Stopwatch.Frequency;
            if (wallSec > 0.05 && procTicks >= prev.ProcTicks)
            {
                double procSec = (procTicks - prev.ProcTicks) / ClockTicksPerSecond;
                cpuPct = Math.Max(0, procSec / wallSec * 100.0);
            }
        }
        _prevCpu[pid] = (procTicks, nowTicks);
        return new ProcUsage(pid, cpuPct, rssMb);
    }

    private static bool TryReadProcCpuTicks(int pid, out ulong totalTicks)
    {
        totalTicks = 0;
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int rparen = stat.LastIndexOf(')');
            if (rparen < 0 || rparen + 2 >= stat.Length)
                return false;

            string[] fields = stat[(rparen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 12)
                return false;

            if (ulong.TryParse(fields[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong utime) &&
                ulong.TryParse(fields[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong stime))
            {
                totalTicks = utime + stime;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static double ReadProcRssMb(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    continue;
                string num = new(line.Where(char.IsDigit).ToArray());
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double kb))
                    return kb / 1024.0;
            }
        }
        catch { }
        return 0;
    }

    private static string GetShmFilePath(string name) =>
        Path.Combine(Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(), name);

    private static T? ReadJsonFile<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private string WriteReportFile(
        string? customLabel,
        DateTime startLocal,
        DateTime endLocal,
        PerfLiveState finalState,
        List<PerfSampleRow> samples,
        List<string> rawLogs,
        int slowFramesDelta,
        int slowRendersDelta)
    {
        Directory.CreateDirectory(ReportsDirectory);

        string modeToken = SanitizeFileToken(finalState.TransportMode.Replace("Wayland ", string.Empty, StringComparison.OrdinalIgnoreCase));
        string apiToken = SanitizeFileToken(finalState.GraphicsBackend);
        string labelToken = SanitizeFileToken(customLabel);
        string prefix = string.IsNullOrEmpty(labelToken) ? "perf" : $"perf-{labelToken}";
        string fileName = $"{prefix}-{modeToken}-{apiToken}-{endLocal:yyyyMMdd-HHmmss}.txt";
        string fullPath = Path.Combine(ReportsDirectory, fileName);

        MugetsuPerfSnapshot? mug = finalState.Mugetsu;
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine(" BONXIEMUX + MUGETSU PERFORMANCE BENCHMARK REPORT");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Test Label        : {(string.IsNullOrWhiteSpace(customLabel) ? "(none)" : customLabel.Trim())}");
        sb.AppendLine($"Started           : {startLocal:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Finished          : {endLocal:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Duration          : {(endLocal - startLocal):hh\\:mm\\:ss} ({samples.Count} samples)");
        sb.AppendLine($"Transport Mode    : {finalState.TransportMode}");
        sb.AppendLine($"Graphics API      : {finalState.GraphicsBackend}");
        sb.AppendLine($"GPU Adapter       : {mug?.GpuAdapter ?? "N/A"} ({mug?.GpuType ?? "?"})");
        sb.AppendLine($"Preferred Backend : {mug?.PreferredBackend ?? "N/A"}");
        sb.AppendLine($"Surface Format    : {mug?.SurfaceFormat ?? "N/A"}");
        sb.AppendLine($"Interpolation     : {mug?.InterpolationState ?? "N/A"}");
        sb.AppendLine($"Process PIDs      : Mugetsu={mug?.MugetsuPid ?? 0}, Host={finalState.Host?.HostPid ?? 0}, Tab={Environment.ProcessId}");
        sb.AppendLine();

        AppendSummarySection(sb, finalState, samples, slowFramesDelta, slowRendersDelta);
        AppendBreakdownSection(sb, mug);
        AppendRawLogsSection(sb, rawLogs);
        AppendTimeSeriesSection(sb, samples);

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
        return fullPath;
    }

    private static void AppendSummarySection(
        StringBuilder sb,
        PerfLiveState finalState,
        List<PerfSampleRow> samples,
        int slowFramesDelta,
        int slowRendersDelta)
    {
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(" AGGREGATED SUMMARY");
        sb.AppendLine("--------------------------------------------------------------------------------");

        if (samples.Count == 0)
        {
            sb.AppendLine("No samples recorded.");
            sb.AppendLine();
            return;
        }

        var hostActive = samples.Where(s => s.HostPresentedFps > 0).ToList();
        var mugActive = samples.Where(s => s.MugetsuWindowCount > 0)
            .GroupBy(s => s.MugetsuWindowCount)
            .Select(g => g.Last())
            .ToList();

        if (hostActive.Count > 0)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"Host Presented FPS  : avg {hostActive.Average(s => s.HostPresentedFps):F1} | min {hostActive.Min(s => s.HostPresentedFps):F1} | max {hostActive.Max(s => s.HostPresentedFps):F1} | 1% low avg {hostActive.Average(s => s.HostOnePercentLowFps):F1}"));
            sb.AppendLine(FormattableString.Invariant(
                $"Host Frame Interval : avg {hostActive.Average(s => s.HostAvgMs):F2} ms | worst max {hostActive.Max(s => s.HostMaxMs):F2} ms | total presented frames: {samples.Last().TotalPresentedFrames - samples.First().TotalPresentedFrames}"));
        }
        else
        {
            sb.AppendLine("Host Presented FPS  : no active frames captured");
        }

        if (mugActive.Count > 0)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"Mugetsu Game FPS    : avg {mugActive.Average(s => s.MugetsuGameFps):F1} | min {mugActive.Min(s => s.MugetsuGameFps):F1} | max {mugActive.Max(s => s.MugetsuGameFps):F1}"));
            sb.AppendLine(FormattableString.Invariant(
                $"Mugetsu Drawn FPS   : avg {mugActive.Average(s => s.MugetsuDrawnFps):F1} | min {mugActive.Min(s => s.MugetsuDrawnFps):F1} | max {mugActive.Max(s => s.MugetsuDrawnFps):F1}"));
            sb.AppendLine(FormattableString.Invariant(
                $"Mugetsu Presented   : avg {mugActive.Average(s => s.MugetsuPresentedFps):F1} | min {mugActive.Min(s => s.MugetsuPresentedFps):F1} | max {mugActive.Max(s => s.MugetsuPresentedFps):F1}"));
            sb.AppendLine(FormattableString.Invariant(
                $"Mugetsu Frame Time  : avg {mugActive.Average(s => s.MugetsuFrameAvgMs):F2} ms | worst max {mugActive.Max(s => s.MugetsuFrameMaxMs):F2} ms"));
            sb.AppendLine(FormattableString.Invariant(
                $"Mugetsu Render Time : avg {mugActive.Average(s => s.MugetsuRenderAvgMs):F2} ms | worst max {mugActive.Max(s => s.MugetsuRenderMaxMs):F2} ms"));
            sb.AppendLine(FormattableString.Invariant(
                $"Mugetsu Pacing      : {mugActive.Sum(s => s.MugetsuHitches)} hitches across {mugActive.Count} windows | worst gap {mugActive.Max(s => s.MugetsuWorstGapMs):F0} ms | slow frames +{slowFramesDelta} | slow renders +{slowRendersDelta}"));
        }
        else
        {
            sb.AppendLine("Mugetsu Telemetry   : waiting for 5-second ruffle_aqw_perf summary");
        }

        sb.AppendLine(FormattableString.Invariant(
            $"Mugetsu CPU / RAM   : CPU avg {samples.Average(s => s.MugetsuCpuPct):F1}% (peak {samples.Max(s => s.MugetsuCpuPct):F1}%) | RSS avg {samples.Average(s => s.MugetsuRssMb):F0} MB (peak {samples.Max(s => s.MugetsuRssMb):F0} MB) | commit {samples.Max(s => s.MugetsuCommitMb)} MB | rust heap {samples.Max(s => s.MugetsuRustHeapMb)} MB"));
        if (finalState.IsHostSeparateProcess)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"BonxieMux Host      : CPU avg {samples.Average(s => s.HostCpuPct):F1}% (peak {samples.Max(s => s.HostCpuPct):F1}%) | RSS avg {samples.Average(s => s.HostRssMb):F0} MB (peak {samples.Max(s => s.HostRssMb):F0} MB)"));
        }
        sb.AppendLine(FormattableString.Invariant(
            $"BonxieMux Tab       : CPU avg {samples.Average(s => s.TabCpuPct):F1}% (peak {samples.Max(s => s.TabCpuPct):F1}%) | RSS avg {samples.Average(s => s.TabRssMb):F0} MB (peak {samples.Max(s => s.TabRssMb):F0} MB)"));
        sb.AppendLine(FormattableString.Invariant(
            $"Total CPU / RAM     : CPU avg {samples.Average(s => s.MugetsuCpuPct + s.HostCpuPct + s.TabCpuPct):F1}% | RSS peak {samples.Max(s => s.MugetsuRssMb + s.HostRssMb + s.TabRssMb):F0} MB"));
        sb.AppendLine();
    }

    private static void AppendBreakdownSection(StringBuilder sb, MugetsuPerfSnapshot? mug)
    {
        if (mug == null)
            return;

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(" LATEST MUGETSU PIPELINE BREAKDOWN");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($"per drawn frame   : {mug.PerDrawnFrameCounters ?? "N/A"}");
        sb.AppendLine($"render time (ms)  : {mug.RenderTimeBreakdown ?? "N/A"}");
        sb.AppendLine($"interp replay     : {mug.InterpolationReplay ?? "N/A"}");
        sb.AppendLine($"objects           : {mug.ObjectsReport ?? "N/A"}");
        sb.AppendLine($"memory            : {mug.MemoryReport ?? "N/A"}");
        sb.AppendLine($"last slow event   : {mug.LastSlowEvent ?? "none"}");
        sb.AppendLine();
    }

    private static void AppendRawLogsSection(StringBuilder sb, List<string> rawLogs)
    {
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine($" CAPTURED MUGETSU PERF LOGS ({rawLogs.Count} lines)");
        sb.AppendLine("--------------------------------------------------------------------------------");
        if (rawLogs.Count == 0)
        {
            sb.AppendLine("(none captured)");
        }
        else
        {
            foreach (string line in rawLogs)
                sb.AppendLine(line);
        }
        sb.AppendLine();
    }

    private static void AppendTimeSeriesSection(StringBuilder sb, List<PerfSampleRow> samples)
    {
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(" SECOND-BY-SECOND SAMPLES");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("Elapsed  | HostFPS | 1%Low  | Frame(avg/max) | Game  | Drawn | Pres  | Render(avg/max) | Gap/H | MugCPU/MB    | HostCPU/MB   | TabCPU/MB");
        sb.AppendLine("---------+---------+--------+----------------+-------+-------+-------+-----------------+-------+--------------+--------------+-------------");
        foreach (PerfSampleRow s in samples)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"{s.Elapsed:hh\\:mm\\:ss} | {s.HostPresentedFps,7:F1} | {s.HostOnePercentLowFps,6:F1} | {s.MugetsuFrameAvgMs,5:F1}/{s.MugetsuFrameMaxMs,5:F1} ms | {s.MugetsuGameFps,5:F1} | {s.MugetsuDrawnFps,5:F1} | {s.MugetsuPresentedFps,5:F1} | {s.MugetsuRenderAvgMs,5:F1}/{s.MugetsuRenderMaxMs,5:F1} ms | {s.MugetsuWorstGapMs,4:F0}/{s.MugetsuHitches,-2} | {s.MugetsuCpuPct,4:F1}%/{s.MugetsuRssMb,5:F0}M | {s.HostCpuPct,4:F1}%/{s.HostRssMb,5:F0}M | {s.TabCpuPct,4:F1}%/{s.TabRssMb,4:F0}M"));
        }
    }

    private static string SanitizeFileToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;
        var chars = input.Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        string collapsed = new string(chars).Trim('-');
        while (collapsed.Contains("--", StringComparison.Ordinal))
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        return collapsed;
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            _isRecording = false;
            _activeConsumers = 0;
            cts = _pollCts;
            _pollCts = null;
        }
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }
}
