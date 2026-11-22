namespace WurmMiningCounter.Models;

public sealed record OrderedEventLine(
    PlayerProfileId ProfileId,
    string ProfileName,
    long SequenceNumber,
    EventLogLine Line)
    : OrderedEventItem(ProfileId, ProfileName, SequenceNumber);
