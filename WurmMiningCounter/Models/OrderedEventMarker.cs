namespace WurmMiningCounter.Models;

/// <summary>Ordered, consumer-specific marker carried by an existing profile event queue.</summary>
public sealed record OrderedEventMarker(
    PlayerProfileId ProfileId,
    string ProfileName,
    long SequenceNumber,
    Guid MarkerId,
    string Kind)
    : OrderedEventItem(ProfileId, ProfileName, SequenceNumber);
