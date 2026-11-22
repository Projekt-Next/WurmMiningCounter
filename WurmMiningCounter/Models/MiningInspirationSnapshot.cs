using System.Collections.ObjectModel;

namespace WurmMiningCounter.Models;

public sealed record MiningInspirationSnapshot(
    PlayerProfileId ProfileId,
    string ProfileName,
    long TotalCount,
    IReadOnlyDictionary<MiningMaterial, long> PrimaryYieldCounts)
{
    public static MiningInspirationSnapshot Empty { get; } = new(
        default,
        string.Empty,
        0,
        new ReadOnlyDictionary<MiningMaterial, long>(new Dictionary<MiningMaterial, long>()));
}
