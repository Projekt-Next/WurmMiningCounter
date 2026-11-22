using WurmMiningCounter.Models;

namespace WurmMiningCounter.MiningStatistics;

public enum LiveSkillSourceState
{
    Unknown,
    WaitingForSource,
    Observing,
    Incomplete
}

/// <summary>Immutable live skill state for one profile.</summary>
public sealed record LiveSkillProfileSnapshot(
    PlayerProfileId ProfileId,
    string ProfileName,
    string? ActiveSourcePath,
    LiveSkillSourceState SourceState,
    IReadOnlyList<SkillStateSnapshot> Skills)
{
    public DateTimeOffset ObservationStartedAtUtc { get; init; }
    public string? PresentationRootPath { get; init; }
    public string PresentationGroupName { get; init; } = "Unknown installation";

    public bool IsSourceKnown => ActiveSourcePath is not null;

    public bool IsHealthy => SourceState == LiveSkillSourceState.Observing;
}
