namespace WurmMiningCounter.Models;

public sealed record StreamContinuityLost(
    PlayerProfileId ProfileId,
    string ProfileName,
    long SequenceNumber,
    StreamGapReason Reason,
    string? AffectedFilePath)
    : OrderedEventItem(ProfileId, ProfileName, SequenceNumber);
