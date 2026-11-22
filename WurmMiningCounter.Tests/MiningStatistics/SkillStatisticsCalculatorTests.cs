using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class SkillStatisticsCalculatorTests
{
    [Theory]
    [InlineData(82.37, 82.50)]
    [InlineData(82.50, 83.00)]
    [InlineData(98.76, 99.00)]
    [InlineData(99.00, 99.01)]
    [InlineData(99.126, 99.13)]
    [InlineData(99.99, 100.00)]
    public void NextMiningMilestone_ReturnsNextStrictlyGreaterBoundary(decimal current, decimal expected) =>
        Assert.Equal(expected, SkillStatisticsCalculator.GetNextMiningMilestone(current));

    [Fact]
    public void NextMiningMilestone_AtOrAbove100_IsUnavailable() =>
        Assert.Null(SkillStatisticsCalculator.GetNextMiningMilestone(100m));

    [Fact]
    public void Calculate_GainPerHourUsesObservationStartTime()
    {
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var mining = new SkillStateSnapshot("Mining", 82.37m, 0.01m, 0.30m);

        var result = SkillStatisticsCalculator.Calculate(mining, start, start.AddMinutes(30));

        Assert.Equal(0.60m, result.GainPerHour);
    }

    [Fact]
    public void Calculate_PositiveRateReturnsMilestoneEta()
    {
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var mining = new SkillStateSnapshot("Mining", 82.37m, 0.01m, 0.30m);

        var result = SkillStatisticsCalculator.Calculate(mining, start, start.AddHours(1));

        Assert.Equal(82.50m, result.NextMilestone);
        Assert.Equal(TimeSpan.FromMinutes(26), result.TimeToMilestone);
    }

    [Fact]
    public void Calculate_ZeroGainHasNoEta()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var mining = new SkillStateSnapshot("Mining", 82.37m, null, 0m);

        var result = SkillStatisticsCalculator.Calculate(mining, now, now.AddHours(1));

        Assert.Equal(0m, result.GainPerHour);
        Assert.Null(result.TimeToMilestone);
    }
}
