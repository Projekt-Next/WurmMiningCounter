namespace WurmMiningCounter.Models;

public sealed record ProfileMiningSessionSnapshot(
    PlayerProfileId ProfileId,
    string ProfileName,
    long TotalCount,
    IReadOnlyDictionary<MiningMaterial, long> Materials);
