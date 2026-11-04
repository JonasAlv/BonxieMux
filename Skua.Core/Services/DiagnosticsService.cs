using Skua.Core.Interfaces;
using Skua.Core.Models.Diagnostics;
using System.Diagnostics;
using System.Text.Json;

namespace Skua.Core.Services;

public sealed class DiagnosticsService : IDiagnosticsService
{
    private const int MaxEvents = 512;
    private const int MaxSnapshots = 120;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly PeriodicTimer _timer = new(SampleInterval);
    private readonly Queue<DiagnosticEvent> _events = new(MaxEvents);
    private readonly Queue<DiagnosticSnapshot> _snapshots = new(MaxSnapshots);
    private readonly bool _environmentEnabled;
    private Task? _samplingTask;
    private DiagnosticSnapshot? _latestSnapshot;
    private int _enabled;
    private int _started;
    private int _disposed;

    public DiagnosticsService()
    {
        _environmentEnabled = IsTruthy(Environment.GetEnvironmentVariable("SKUA_DEBUG_MONITORING"));
        _enabled = _environmentEnabled ? 1 : 0;
    }

    public bool Enabled => Volatile.Read(ref _enabled) == 1;

    public DiagnosticSnapshot? LatestSnapshot
    {
        get
        {
            lock (_sync)
                return _latestSnapshot;
        }
    }

    public void Start()
    {
        if (!Enabled || Volatile.Read(ref _disposed) == 1)
            return;

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            CaptureSnapshot();
            _samplingTask = SampleLoopAsync(_shutdown.Token);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        Volatile.Write(ref _enabled, enabled ? 1 : 0);
        if (enabled)
            Start();
    }

    public void RecordEvent(
        string category,
        string name,
        double? value = null,
        string? unit = null,
        string? correlationId = null)
    {
        if (!Enabled || Volatile.Read(ref _disposed) == 1 || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            DiagnosticEvent diagnosticEvent = new(
                DateTimeOffset.UtcNow,
                category,
                name,
                value,
                unit,
                correlationId);

            lock (_sync)
            {
                if (_events.Count >= MaxEvents)
                    _events.Dequeue();
                _events.Enqueue(diagnosticEvent);
            }
        }
        catch
        {
            // Diagnostics must never affect the script runtime.
        }
    }

    public IDiagnosticActivity BeginActivity(
        string category,
        string name,
        string? correlationId = null)
    {
        if (!Enabled || Volatile.Read(ref _disposed) == 1)
            return NoopDiagnosticActivity.Instance;

        return new TimedDiagnosticActivity(this, category, name, correlationId);
    }

    public IReadOnlyList<DiagnosticSnapshot> GetRecentSnapshots()
    {
        lock (_sync)
            return _snapshots.ToArray();
    }

    public IReadOnlyList<DiagnosticEvent> GetRecentEvents()
    {
        lock (_sync)
            return _events.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _shutdown.Cancel();
        _timer.Dispose();

        if (_samplingTask is not null)
        {
            try
            {
                await _samplingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        _shutdown.Dispose();
    }

    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An export path is required.", nameof(path));

        DiagnosticSnapshot? latestSnapshot;
        DiagnosticSnapshot[] snapshots;
        DiagnosticEvent[] events;
        lock (_sync)
        {
            latestSnapshot = _latestSnapshot;
            snapshots = _snapshots.ToArray();
            events = _events.ToArray();
        }

        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(
            stream,
            new
            {
                ExportedAt = DateTimeOffset.UtcNow,
                LatestSnapshot = latestSnapshot,
                Snapshots = snapshots,
                Events = events
            },
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (Enabled)
                    CaptureSnapshot();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordEvent("Diagnostics", "SamplerFault", unit: exception.GetType().Name);
        }
    }

    private void CaptureSnapshot()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();

            ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            GCMemoryInfo memory = GC.GetGCMemoryInfo();

            DiagnosticSnapshot snapshot = new(
                DateTimeOffset.UtcNow,
                process.Id,
                (long)process.TotalProcessorTime.TotalMilliseconds,
                process.WorkingSet64,
                memory.HeapSizeBytes,
                memory.TotalCommittedBytes,
                memory.FragmentedBytes,
                GC.GetTotalAllocatedBytes(false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                process.Threads.Count,
                availableWorkers,
                maxWorkers);

            lock (_sync)
            {
                _latestSnapshot = snapshot;
                if (_snapshots.Count >= MaxSnapshots)
                    _snapshots.Dequeue();
                _snapshots.Enqueue(snapshot);
            }
        }
        catch (Exception exception)
        {
            RecordEvent("Diagnostics", "SnapshotFault", unit: exception.GetType().Name);
        }
    }

    private static bool IsTruthy(string? value)
    {
        return value is "1" or "true" or "TRUE" or "True";
    }

    private sealed class TimedDiagnosticActivity : IDiagnosticActivity
    {
        private readonly DiagnosticsService _owner;
        private readonly string _category;
        private readonly string _name;
        private readonly string? _correlationId;
        private readonly long _startTimestamp = Stopwatch.GetTimestamp();
        private int _disposed;

        public TimedDiagnosticActivity(
            DiagnosticsService owner,
            string category,
            string name,
            string? correlationId)
        {
            _owner = owner;
            _category = category;
            _name = name;
            _correlationId = correlationId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            double elapsedMilliseconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            _owner.RecordEvent(_category, _name, elapsedMilliseconds, "ms", _correlationId);
        }
    }

    private sealed class NoopDiagnosticActivity : IDiagnosticActivity
    {
        public static NoopDiagnosticActivity Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
