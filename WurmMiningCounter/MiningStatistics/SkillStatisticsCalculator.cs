namespace WurmMiningCounter.MiningStatistics;

public sealed record MiningSkillStatistics(
    decimal? CurrentValue,
    decimal? LastGain,
    decimal SessionGain,
    long PositiveUpdateCount,
    decimal GainPerHour,
    decimal? NextMilestone,
    TimeSpan? TimeToMilestone);

public static class SkillStatisticsCalculator
{
    public static MiningSkillStatistics Calculate(
        SkillStateSnapshot? mining,
        DateTimeOffset observationStartedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (mining is null)
            return new MiningSkillStatistics(null, null, 0m, 0, 0m, null, null);

        var elapsed = nowUtc - observationStartedAtUtc;
        var gainPerHour = mining.SessionGain > 0m && elapsed > TimeSpan.Zero
            ? mining.SessionGain * TimeSpan.TicksPerHour / elapsed.Ticks
            : 0m;
        var milestone = GetNextMiningMilestone(mining.CurrentValue);
        var eta = gainPerHour > 0m && milestone is { } target
            ? CalculateEta(target - mining.CurrentValue, gainPerHour)
            : null;

        return new MiningSkillStatistics(
            mining.CurrentValue,
            mining.LastGain,
            mining.SessionGain,
            mining.PositiveUpdateCount,
            gainPerHour,
            milestone,
            eta);
    }

    public static decimal? GetNextMiningMilestone(decimal currentValue)
    {
        if (currentValue >= 100m)
            return null;

        var step = currentValue < 99m ? 0.50m : 0.01m;
        return (decimal.Floor(currentValue / step) + 1m) * step;
    }

    private static TimeSpan? CalculateEta(decimal remainingSkill, decimal gainPerHour)
    {
        var ticks = decimal.Ceiling(remainingSkill * TimeSpan.TicksPerHour / gainPerHour);
        return ticks > long.MaxValue ? null : TimeSpan.FromTicks(decimal.ToInt64(ticks));
    }
}
