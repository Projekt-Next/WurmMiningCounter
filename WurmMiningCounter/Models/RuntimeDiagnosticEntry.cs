namespace WurmMiningCounter.Models;

public enum RuntimeDiagnosticLevel
{
    Info,
    Warning,
    Error
}

public sealed record RuntimeDiagnosticEntry(
    DateTimeOffset Timestamp,
    RuntimeDiagnosticLevel Level,
    string Source,
    string Message,
    long Sequence = 0)
{
    public DateTimeOffset LocalTimestamp => Timestamp.ToLocalTime();
}
