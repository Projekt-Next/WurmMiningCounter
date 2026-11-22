using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class MiningSessionStatisticsCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsDerivedSessionStatistics()
    {
        var result = MiningSessionStatisticsCalculator.Calculate(100, 25, 0.050m, TimeSpan.FromHours(2));

        Assert.Equal(25m, result.TickRatePercent);
        Assert.Equal(4m, result.ActionsPerTick);
        Assert.Equal(0.002m, result.AverageGainPerTick);
        Assert.Equal(12.5m, result.TicksPerHour);
    }

    [Theory]
    [InlineData(25, 6.25)]
    [InlineData(48, 12)]
    [InlineData(77, 19.25)]
    [InlineData(100, 25)]
    public void Projection_NormalizesToSelectedActions(long selectedActions, decimal expected) =>
        Assert.Equal(expected, MiningSessionStatisticsCalculator.CalculateTicksPerSelectedActions(200, 50, selectedActions));

    [Fact]
    public void Calculate_ZeroDenominatorsAreUnavailableExceptZeroTicksPerHour()
    {
        var zeroActions = MiningSessionStatisticsCalculator.Calculate(0, 5, 0.01m, TimeSpan.FromHours(1));
        var zeroTicks = MiningSessionStatisticsCalculator.Calculate(5, 0, 0m, TimeSpan.FromHours(1));

        Assert.Null(zeroActions.TickRatePercent);
        Assert.Null(MiningSessionStatisticsCalculator.CalculateTicksPerSelectedActions(0, 5, 100));
        Assert.Null(zeroTicks.ActionsPerTick);
        Assert.Null(zeroTicks.AverageGainPerTick);
        Assert.Equal(0m, zeroTicks.TicksPerHour);
    }

    [Fact]
    public void Calculate_NonPositiveElapsedMakesTicksPerHourUnavailable()
    {
        Assert.Null(MiningSessionStatisticsCalculator.Calculate(10, 2, 0.002m, TimeSpan.Zero).TicksPerHour);
        Assert.Null(MiningSessionStatisticsCalculator.Calculate(10, 2, 0.002m, TimeSpan.FromSeconds(-1)).TicksPerHour);
    }
}
