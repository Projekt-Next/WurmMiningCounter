namespace WurmMiningCounter.Models;

public sealed record ResetProfileBarrier(
    PlayerProfileId ProfileId,
    string ProfileName,
    long SequenceNumber)
    : OrderedEventItem(ProfileId, ProfileName, SequenceNumber);
