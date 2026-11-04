namespace Skua.Core.Models.Diagnostics;

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    string Category,
    string Name,
    double? Value,
    string? Unit,
    string? CorrelationId);
