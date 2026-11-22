using WurmMiningCounter.Models;

namespace WurmMiningCounter.MiningStatistics;

/// <summary>Immutable notification emitted after one profile's observable live snapshot changes.</summary>
public sealed class LiveSkillProfileChangedEventArgs(PlayerProfileId profileId, LiveSkillProfileSnapshot snapshot) : EventArgs
{
    public PlayerProfileId ProfileId { get; } = profileId;

    public LiveSkillProfileSnapshot Snapshot { get; } = snapshot;
}
