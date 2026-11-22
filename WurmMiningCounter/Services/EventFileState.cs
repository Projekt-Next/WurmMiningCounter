using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

internal sealed class EventFileState
{
    public required ProfileSourceOwner Owner { get; set; }
    public PlayerProfileId ProfileId => Owner.Identity.Id;
    public required string ProfileName { get; init; }
    public required string CanonicalPath { get; init; }
    public string? LogicalLogsDirectoryPath { get; init; }
    public string? PhysicalLogsDirectoryKey { get; init; }
    public bool LogsBindingLost { get; set; }
    public bool ConflictStatusReported { get; set; }
    public long ReadOffset { get; set; }
    public string PendingText { get; set; } = string.Empty;
    public EventFileFingerprint? Fingerprint { get; set; }
    public EventFileContinuitySignature? ContinuitySignature { get; set; }
    public long Generation { get; set; }
    public bool BaselinePending { get; set; }
    public bool VerificationPending { get; set; }
    public bool DisappearancePending { get; set; }
    public bool Missing { get; set; }
    public bool OwnerRebindPending { get; set; }
    public Exception? TerminalFault { get; set; }
}
