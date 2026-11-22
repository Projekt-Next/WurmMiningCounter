using WurmMiningCounter.MiningStatistics;

namespace WurmMiningCounter.Tests.MiningStatistics;

public sealed class SkillStateTrackerTests
{
    [Fact]
    public void SeedCurrent_SetsOnlyCurrent()
    {
        var tracker = new SkillStateTracker();

        tracker.SeedCurrent("Mining", 82.456789m);

        var skill = Assert.Single(tracker.GetSnapshot());
        Assert.Equal("Mining", skill.SkillName);
        Assert.Equal(82.456789m, skill.CurrentValue);
        Assert.Null(skill.LastGain);
        Assert.Equal(0m, skill.SessionGain);
        Assert.Equal(0, skill.PositiveUpdateCount);
    }

    [Fact]
    public void FirstIncrease_SetsCurrentLastAndSessionGain()
    {
        var tracker = new SkillStateTracker();

        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Increased, 0.01m, 82.41m));

        Assert.Equal(new SkillStateSnapshot("Mining", 82.41m, 0.01m, 0.01m, 1), Assert.Single(tracker.GetSnapshot()));
    }

    [Fact]
    public void MultipleIncreases_ReplacesLastGainAndAccumulatesSessionGain()
    {
        var tracker = new SkillStateTracker();

        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Increased, 0.01m, 82.41m));
        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Increased, 0.005m, 82.415m));

        Assert.Equal(new SkillStateSnapshot("Mining", 82.415m, 0.005m, 0.015m, 2), Assert.Single(tracker.GetSnapshot()));
    }

    [Fact]
    public void DecreaseAfterIncrease_UpdatesCurrentButPreservesPositiveGains()
    {
        var tracker = new SkillStateTracker();
        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Increased, 0.01m, 82.41m));

        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Decreased, 0.002m, 82.408m));

        Assert.Equal(new SkillStateSnapshot("Mining", 82.408m, 0.01m, 0.01m, 1), Assert.Single(tracker.GetSnapshot()));
    }

    [Fact]
    public void FirstUpdateDecrease_LeavesLastGainNullAndSessionGainZero()
    {
        var tracker = new SkillStateTracker();

        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Decreased, 0.002m, 82.398m));

        Assert.Equal(new SkillStateSnapshot("Mining", 82.398m, null, 0m, 0), Assert.Single(tracker.GetSnapshot()));
    }

    [Fact]
    public void SeedThenIncrease_DoesNotIncludeBootstrapInSessionGain()
    {
        var tracker = new SkillStateTracker();
        tracker.SeedCurrent("Mining", 82.4m);

        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Increased, 0.01m, 82.41m));

        Assert.Equal(new SkillStateSnapshot("Mining", 82.41m, 0.01m, 0.01m, 1), Assert.Single(tracker.GetSnapshot()));
    }

    [Fact]
    public void MultipleSkills_AreIndependent()
    {
        var tracker = new SkillStateTracker();
        tracker.SeedCurrent("Mining", 80m);
        tracker.Apply(Update("Prospecting", NumericSkillUpdateDirection.Increased, 0.1m, 30.1m));
        tracker.Apply(Update("Body Strength", NumericSkillUpdateDirection.Decreased, 0.2m, 20m));

        Assert.Equal(
        [
            new SkillStateSnapshot("Body Strength", 20m, null, 0m, 0),
            new SkillStateSnapshot("Mining", 80m, null, 0m),
            new SkillStateSnapshot("Prospecting", 30.1m, 0.1m, 0.1m, 1)
        ],
        tracker.GetSnapshot());
    }

    [Fact]
    public void Snapshot_IsImmutableAcrossLaterUpdates()
    {
        var tracker = new SkillStateTracker();
        tracker.SeedCurrent("Mining", 82.4m);
        var before = tracker.GetSnapshot();

        tracker.Apply(Update("Mining", NumericSkillUpdateDirection.Increased, 0.01m, 82.41m));

        Assert.Equal(new SkillStateSnapshot("Mining", 82.4m, null, 0m, 0), Assert.Single(before));
        Assert.Equal(new SkillStateSnapshot("Mining", 82.41m, 0.01m, 0.01m, 1), Assert.Single(tracker.GetSnapshot()));
    }

    [Fact]
    public void Snapshot_HasDeterministicOrdering()
    {
        var tracker = new SkillStateTracker();
        tracker.SeedCurrent("Prospecting", 1m);
        tracker.SeedCurrent("Mining", 2m);
        tracker.SeedCurrent("Body Strength", 3m);

        Assert.Equal(["Body Strength", "Mining", "Prospecting"], tracker.GetSnapshot().Select(skill => skill.SkillName));
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_AreSafe()
    {
        var tracker = new SkillStateTracker();
        using var barrier = new Barrier(3);

        var miningWriter = Task.Run(() => Write("Mining", 80m));
        var prospectingWriter = Task.Run(() => Write("Prospecting", 30m));
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                barrier.SignalAndWait();
                var snapshot = tracker.GetSnapshot();
                Assert.True(snapshot.SequenceEqual(snapshot.OrderBy(skill => skill.SkillName, StringComparer.Ordinal)));
                barrier.SignalAndWait();
            }
        });

        await Task.WhenAll(miningWriter, prospectingWriter, reader);

        Assert.Equal(new SkillStateSnapshot("Mining", 90m, 0.1m, 10m, 100), tracker.GetSnapshot()[0]);
        Assert.Equal(new SkillStateSnapshot("Prospecting", 40m, 0.1m, 10m, 100), tracker.GetSnapshot()[1]);

        void Write(string skillName, decimal initialValue)
        {
            for (var index = 0; index < 100; index++)
            {
                barrier.SignalAndWait();
                tracker.Apply(Update(skillName, NumericSkillUpdateDirection.Increased, 0.1m, initialValue + ((index + 1) * 0.1m)));
                barrier.SignalAndWait();
            }
        }
    }

    private static NumericSkillUpdate Update(
        string skillName,
        NumericSkillUpdateDirection direction,
        decimal delta,
        decimal currentValue) => new(new TimeOnly(1, 0), skillName, direction, delta, currentValue);
}
