using System.Collections.Immutable;

namespace WurmMiningCounter.Models;

public sealed record NearbyScanSnapshot(
    PlayerProfileId ProfileId,
    string ProfileName,
    string RawDescription,
    IReadOnlyList<NearbyProspectingEntry> Entries,
    bool IsOnlyRock)
{
    public ImmutableArray<ProspectingAugment> Augments { get; init; } =
        ImmutableArray<ProspectingAugment>.Empty;
}
