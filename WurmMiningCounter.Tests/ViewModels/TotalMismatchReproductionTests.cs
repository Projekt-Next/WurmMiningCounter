using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class TotalMismatchReproductionTests
{
    [Fact]
    public void EightStoneShardSuccesses_KeepCounterAndProfilePanelTotalsConsistentAtEveryPublication()
    {
        var counter = new MiningSessionCounter();
        var scheduledRefreshes = new List<Action>();
        var viewModel = new MainViewModel(counter, postToUi: scheduledRefreshes.Add);
        var processor = new MiningEventProcessor(new MiningEventParser(), counter);
        var publishedTotals = new List<(long MaterialCount, long TotalCount)>();
        counter.CountsChanged += (_, eventArgs) =>
        {
            Assert.Equal(EventTransportTestProfileIds.ProfileB, eventArgs.ProfileId);
            var snapshot = counter.GetProfileSnapshots()[EventTransportTestProfileIds.ProfileB];
            publishedTotals.Add((snapshot.Materials[MiningMaterial.StoneShards], snapshot.TotalCount));
        };

        ProfilePanelViewModel? previousPanel = null;
        for (var expected = 1; expected <= 8; expected++)
        {
            Assert.True(processor.Process(new EventLogLine(
                EventTransportTestProfileIds.ProfileB,
                "ProfileB",
                "_Event.2026-09-13.txt",
                "You mine some stone shards.")));

            Assert.Single(scheduledRefreshes)();
            scheduledRefreshes.Clear();

            var panel = Assert.Single(viewModel.ProfilePanels);
            var material = Assert.Single(panel.Materials);
            Assert.Equal(MiningMaterial.StoneShards, material.Material);
            Assert.Equal(expected, material.Count);
            Assert.Equal(expected, panel.TotalCount);
            if (previousPanel is not null)
                Assert.NotSame(previousPanel, panel);
            previousPanel = panel;
        }

        Assert.Equal(
            Enumerable.Range(1, 8).Select(value => ((long)value, (long)value)),
            publishedTotals);
        var finalSnapshot = counter.GetProfileSnapshots()[EventTransportTestProfileIds.ProfileB];
        Assert.Equal(8, finalSnapshot.Materials[MiningMaterial.StoneShards]);
        Assert.Equal(8, finalSnapshot.TotalCount);
    }

    [Fact]
    public void PrimaryCounts_RemainIsolatedAcrossProfiles()
    {
        var counter = new MiningSessionCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter);

        foreach (var _ in Enumerable.Range(0, 8))
            processor.Process(new EventLogLine(EventTransportTestProfileIds.ProfileALower, "profilea", "_Event.2026-09-13.txt", "You mine some stone shards."));
        foreach (var _ in Enumerable.Range(0, 3))
            processor.Process(new EventLogLine(EventTransportTestProfileIds.ProfileB, "ProfileB", "_Event.2026-09-13.txt", "You mine some stone shards."));

        var snapshots = counter.GetProfileSnapshots();
        Assert.Equal(8, snapshots[EventTransportTestProfileIds.ProfileALower].Materials[MiningMaterial.StoneShards]);
        Assert.Equal(8, snapshots[EventTransportTestProfileIds.ProfileALower].TotalCount);
        Assert.Equal(3, snapshots[EventTransportTestProfileIds.ProfileB].Materials[MiningMaterial.StoneShards]);
        Assert.Equal(3, snapshots[EventTransportTestProfileIds.ProfileB].TotalCount);
    }
}
