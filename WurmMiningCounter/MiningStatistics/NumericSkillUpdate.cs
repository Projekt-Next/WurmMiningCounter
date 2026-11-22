namespace WurmMiningCounter.MiningStatistics;

public sealed record NumericSkillUpdate(
    TimeOnly Timestamp,
    string SkillName,
    NumericSkillUpdateDirection Direction,
    decimal Delta,
    decimal CurrentValue)
{
    public decimal SignedDelta => Direction == NumericSkillUpdateDirection.Increased
        ? Delta
        : -Delta;
}
