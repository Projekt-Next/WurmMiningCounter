namespace WurmMiningCounter.MiningStatistics;

public sealed record MiningSkillsSourceReadResult(
    IReadOnlyList<string> Lines,
    MiningSkillsSourceContinuityLossReason? ContinuityLossReason)
{
    public bool HasContinuityLoss => ContinuityLossReason is not null;
}
