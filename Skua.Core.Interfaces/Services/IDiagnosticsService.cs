using Skua.Core.Models.Diagnostics;

namespace Skua.Core.Interfaces;

public interface IDiagnosticsService : IAsyncDisposable
{
    bool Enabled { get; }

    void Start();

    void SetEnabled(bool enabled);

    void RecordEvent(
        string category,
        string name,
        double? value = null,
        string? unit = null,
        string? correlationId = null);

    IDiagnosticActivity BeginActivity(
        string category,
        string name,
        string? correlationId = null);

    DiagnosticSnapshot? LatestSnapshot { get; }

    IReadOnlyList<DiagnosticSnapshot> GetRecentSnapshots();

    IReadOnlyList<DiagnosticEvent> GetRecentEvents();

    Task ExportAsync(string path, CancellationToken cancellationToken = default);
}
