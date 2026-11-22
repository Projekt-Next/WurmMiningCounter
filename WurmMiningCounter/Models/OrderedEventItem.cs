namespace WurmMiningCounter.Models;

public abstract record OrderedEventItem(
    PlayerProfileId ProfileId,
    string ProfileName,
    long SequenceNumber);
