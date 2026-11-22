using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningInspirationTrackerTests
{
    private const string MineWall = "You start to mine forwards.";
    private const string MineUp = "You start to mine up.";
    private const string MineDown = "You start to mine down.";
    private const string MineFloor = "You start to mine the floor.";
    private const string Moi = "You have a moment of inspiration...";

    [Theory]
    [InlineData("You mine some ore.", MiningMaterial.Ore)]
    [InlineData("You mine some shards.", MiningMaterial.Shards)]
    [InlineData("You mine some stone shards.", MiningMaterial.StoneShards)]
    [InlineData("You mine some rock salt.", MiningMaterial.RockSalt)]
    public void Process_ConfirmedMoiThenPrimaryCountsOneInspirationAndOneMiningYield(
        string primaryYield,
        MiningMaterial expectedMaterial)
    {
        var (processor, counter, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", primaryYield);

        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][expectedMaterial]);
        var inspiration = inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, inspiration.TotalCount);
        Assert.Equal(1, inspiration.PrimaryYieldCounts[expectedMaterial]);
    }

    [Fact]
    public void Process_MoiWithoutActiveMiningAttemptIsIgnored()
    {
        var (processor, counter, inspirations) = CreateProcessor();

        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Theory]
    [InlineData("You send your prayers to Libila.")]
    [InlineData("The field is tended.")]
    [InlineData("An old horse is dead. R.I.P.")]
    [InlineData("You also find a rare coin!")]
    public void Process_NonMiningMoiDoesNotCreateInspiration(string followingLine)
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", Moi);
        Process(processor, "Alice", followingLine);

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_StopClearsMoiBeforeNewAttempt()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You stop mining.");
        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_RejectionClearsMoiBeforeNewAttempt()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You're too busy.");
        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_RejectionBetweenMoiAndPrimaryCancelsCorrelationWithoutEndingEarlierGeneration()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You're too busy.");
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_NewActionStartClearsMoiBeforePrimary()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", MineFloor);
        Process(processor, "Alice", "You mine some stone shards.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_WallWarningBeforeMoiAllowsConfirmedPrimary()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", "The wall will break soon.");
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public void Process_WallWarningBetweenMoiAndPrimaryPreservesCorrelation()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "The wall will break soon.");
        Process(processor, "Alice", "You mine some stone shards.");

        var inspiration = inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, inspiration.TotalCount);
        Assert.Equal(1, inspiration.PrimaryYieldCounts[MiningMaterial.StoneShards]);
    }

    [Fact]
    public void Process_WallWarningWithoutQualifyingPrimaryDoesNotCountMoi()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "The wall will break soon.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_WallBreakBetweenMoiAndPrimaryCancelsCorrelation()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "The wall breaks!");
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_TunnellingChipAwayCancelsMoiBeforeNewGeneration()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", "You start to tunnel.");
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You chip away at the rock.");
        Process(processor, "Alice", "You start to tunnel.");
        Process(processor, "Alice", "You mine some stone shards.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_WallBreakThenMoiCanCorrelateFinalPrimary()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", "The wall breaks!");
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You mine some stone shards.");

        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Theory]
    [InlineData(MineWall)]
    [InlineData(MineUp)]
    [InlineData(MineDown)]
    public void Process_UnboundMiningStartCanCorrelateMoiAndPrimary(string miningStart)
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", miningStart);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public void Process_NonPrimaryResultClearsMoiBeforeLaterPrimary()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You find a ruby!");
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_SessionBoundaryClearsMoi()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "Logging started 2026-08-23");
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void Process_ProfilesAreIsolated()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Bob", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Bob", "You mine some ore.");
        Process(processor, "Alice", "You mine some shards.");

        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.False(inspirations.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Bob));
    }

    [Fact]
    public void Process_MoiDoesNotChangeMiningCounterUntilPrimaryYield()
    {
        var (processor, counter, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);

        Assert.Empty(counter.GetSnapshot());
        Assert.Empty(inspirations.GetProfileSnapshots());

        Process(processor, "Alice", "You mine some ore.");

        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public void ClearProfilePendingPreventsCrossResetCorrelation()
    {
        var (processor, _, inspirations, tracker) = CreateProcessorWithTracker();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        tracker.ClearProfilePending(EventTransportTestProfileIds.Alice);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void ResetClearsPendingMoi()
    {
        var (processor, _, inspirations, tracker) = CreateProcessorWithTracker();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        tracker.Reset();
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void ResetMiningInspirationProfile_ClearsOnlySelectedProfileAndPreservesMiningCounts()
    {
        var (processor, counter, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You mine some ore.");
        Process(processor, "Bob", MineWall);
        Process(processor, "Bob", Moi);
        Process(processor, "Bob", "You mine some shards.");

        processor.ResetMiningInspirationProfile(EventTransportTestProfileIds.Alice);

        Assert.False(inspirations.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Alice));
        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Bob].TotalCount);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Bob].TotalCount);
    }

    [Fact]
    public void ResetMiningInspirationProfile_ClearsOldPendingButAllowsNewMoiInSameActiveAction()
    {
        var (processor, counter, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        processor.ResetMiningInspirationProfile(EventTransportTestProfileIds.Alice);
        Process(processor, "Alice", Moi);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public void ResetMiningInspirationProfile_PreResetPendingCannotCountAfterReset()
    {
        var (processor, _, inspirations) = CreateProcessor();

        Process(processor, "Alice", MineWall);
        Process(processor, "Alice", Moi);
        processor.ResetMiningInspirationProfile(EventTransportTestProfileIds.Alice);
        Process(processor, "Alice", "You mine some ore.");

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public void ResetMiningInspirationProfile_CanBeRepeatedAtZero()
    {
        var (processor, _, inspirations) = CreateProcessor();

        processor.ResetMiningInspirationProfile(EventTransportTestProfileIds.Alice);
        processor.ResetMiningInspirationProfile(EventTransportTestProfileIds.Alice);

        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    private static (MiningEventProcessor Processor, MiningSessionCounter Counter, MiningInspirationCounter Inspirations) CreateProcessor()
    {
        var (processor, counter, inspirations, _) = CreateProcessorWithTracker();
        return (processor, counter, inspirations);
    }

    private static (MiningEventProcessor Processor, MiningSessionCounter Counter, MiningInspirationCounter Inspirations, MiningInspirationTracker Tracker) CreateProcessorWithTracker()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        return (new MiningEventProcessor(new MiningEventParser(), counter, tracker), counter, inspirations, tracker);
    }

    private static void Process(MiningEventProcessor processor, string profileName, string text) =>
        processor.Process(new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "C:\\logs\\Event.txt", text));
}
