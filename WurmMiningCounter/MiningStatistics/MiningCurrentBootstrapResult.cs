namespace WurmMiningCounter.MiningStatistics;

public enum MiningCurrentBootstrapStatus
{
    Found,
    NotFound,
    ContinuityLost,
    InvalidBoundary
}

/// <summary>Result of finding Current Mining in the complete history before a startup boundary.</summary>
public sealed record MiningCurrentBootstrapResult(
    MiningCurrentBootstrapStatus Status,
    decimal? CurrentValue = null,
    MiningSkillsSourceContinuityLossReason? ContinuityLossReason = null)
{
    public static MiningCurrentBootstrapResult NotFound { get; } = new(MiningCurrentBootstrapStatus.NotFound);

    public static MiningCurrentBootstrapResult InvalidBoundary { get; } = new(MiningCurrentBootstrapStatus.InvalidBoundary);
}
