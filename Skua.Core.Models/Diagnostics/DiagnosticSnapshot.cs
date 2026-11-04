namespace Skua.Core.Models.Diagnostics;

public sealed record DiagnosticSnapshot(
    DateTimeOffset Timestamp,
    long ProcessId,
    long ProcessCpuMilliseconds,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long ManagedCommittedBytes,
    long ManagedFragmentedBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int ThreadPoolAvailableWorkers,
    int ThreadPoolMaxWorkers);
