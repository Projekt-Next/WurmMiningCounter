namespace WurmMiningCounter.Models;

public sealed record EventLogReadResult(
    IReadOnlyList<string> Lines,
    long NewPosition,
    EventFileFingerprint? Fingerprint,
    bool RequiresRetry,
    string PendingText)
{
    public StreamGapReason? ContinuityLossReason { get; init; }
}
