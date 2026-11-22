namespace WurmMiningCounter.MiningStatistics;

/// <summary>Immutable state for one observed skill in the current application session.</summary>
public sealed record SkillStateSnapshot(
    string SkillName,
    decimal CurrentValue,
    decimal? LastGain,
    decimal SessionGain,
    long PositiveUpdateCount = 0);
