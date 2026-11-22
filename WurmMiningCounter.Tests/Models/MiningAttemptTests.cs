using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Models;

public sealed class MiningAttemptTests
{
    [Fact]
    public void WallAttemptStoresBoundVeinContextAndActivePhase()
    {
        var context = new ActiveVeinContext(
            VeinMaterial.Iron,
            new QualityMeasurement.ExactMax(12.5m),
            new RemainingMeasurement.Exact(1103),
            VeinConfidence.Synchronized);

        var attempt = new MiningAttempt(
            MiningActionContext.Wall,
            context,
            MiningAttemptPhase.Active);

        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Same(context, attempt.BoundVeinContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
    }

    [Fact]
    public void FloorAttemptAllowsNoBoundVeinContext()
    {
        var attempt = new MiningAttempt(
            MiningActionContext.Floor,
            null,
            MiningAttemptPhase.Active);

        Assert.Equal(MiningActionContext.Floor, attempt.ActionContext);
        Assert.Null(attempt.BoundVeinContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
    }

    [Fact]
    public void WallAttemptStoresWallBrokenAwaitingPrimaryPhase()
    {
        var attempt = new MiningAttempt(
            MiningActionContext.Wall,
            null,
            MiningAttemptPhase.WallBrokenAwaitingPrimary);

        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
    }

    [Fact]
    public void AttemptStoresCompletedPhase()
    {
        var attempt = new MiningAttempt(
            MiningActionContext.Surface,
            null,
            MiningAttemptPhase.Completed);

        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
    }
}
