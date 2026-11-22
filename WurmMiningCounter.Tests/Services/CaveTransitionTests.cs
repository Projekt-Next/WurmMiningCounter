using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class CaveTransitionTests
{
    private const string ProspectingStart = "You start to gather fragments of the rock.";
    private const string Iron = "You would mine iron ore here.";
    private const string Zinc = "You would mine zinc ore here.";
    private const string Quality = "It has a max quality of 12.5.";
    private const string Remaining = "You will be able to mine here 1103 more times.";
    private const string PlainRock = "You would mine stone shards here.";
    private const string MineWall = "You start to mine forwards.";

    [Theory]
    [InlineData("You enter the cave.")]
    [InlineData("[07:04:02] You enter the cave.")]
    [InlineData("You leave the cave.")]
    [InlineData("[07:03:56] You leave the cave.")]
    public void CaveTransition_ClearsVeinAttemptAndNearbyForCurrentProfile(string transition)
    {
        var nearby = new NearbyScanProcessor();
        var processor = new VeinContextProcessor(
            invalidateNearbyScan: profileId => nearby.Invalidate(profileId, "Alice"));
        ProcessCompleteVein(processor, "Alice", 1, Iron);
        ProcessNearby(nearby, "Alice", 5, "There is iron nearby.");
        Process(processor, "Alice", 6, MineWall);

        Process(processor, "Alice", 7, transition);
        ProcessNearby(nearby, "Alice", 8, "You will find flint here!");

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.Empty(nearby.GetSnapshots());
    }

    [Theory]
    [InlineData("You enter the cave.")]
    [InlineData("You leave the cave.")]
    public void CaveTransition_ClearsPlainRockResultAndAllowsNewProspecting(string transition)
    {
        var processor = new VeinContextProcessor();
        var results = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => results.Add(args);
        Process(processor, "Alice", 1, ProspectingStart);
        Process(processor, "Alice", 2, PlainRock);

        Process(processor, "Alice", 3, transition);
        ProcessCompleteVein(processor, "Alice", 4, Zinc);

        Assert.Collection(
            results,
            changed => Assert.NotNull(changed.Result),
            changed => Assert.Null(changed.Result));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
    }

    [Fact]
    public void CaveTransition_DoesNotMatchPartialOrSimilarMessagesAndIsIdempotent()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteVein(processor, "Alice", 1, Iron);
        ProcessCompleteVein(processor, "Bob", 1, Zinc);

        Process(processor, "Alice", 5, "You enter the cave now.");
        Process(processor, "Alice", 6, "Someone says: You leave the cave.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));

        Process(processor, "Alice", 7, "You enter the cave.");
        Process(processor, "Alice", 8, "You leave the cave.");

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bob));
        Assert.Equal(VeinMaterial.Zinc, bob!.Material);
    }

    [Fact]
    public void CaveTransition_DoesNotChangeMiningOrInspirationCounters()
    {
        var mining = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        mining.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        inspirations.Add(EventTransportTestProfileIds.Alice, "Alice", MiningMaterial.Ore);
        var processor = new VeinContextProcessor();
        ProcessCompleteVein(processor, "Alice", 1, Iron);

        Process(processor, "Alice", 5, "You leave the cave.");

        Assert.Equal(1, mining.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public async Task CaveTransition_IsAppliedInOrderWithinTheSameContextBatch()
    {
        var nearby = new NearbyScanProcessor();
        var processor = new VeinContextProcessor(
            invalidateNearbyScan: profileId => nearby.Invalidate(profileId, "Alice"));
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(processor);
        coordinator.RegisterConsumer(nearby);

        coordinator.AcceptOutput(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", 1,
        [
            "There is iron nearby.",
            "You enter the cave.",
            ProspectingStart,
            Zinc,
            Quality,
            Remaining
        ]));
        await coordinator.CompleteAsync();

        Assert.Empty(nearby.GetSnapshots());
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
    }

    private static void ProcessCompleteVein(
        VeinContextProcessor processor,
        string profileName,
        long firstSequence,
        string material)
    {
        Process(processor, profileName, firstSequence, ProspectingStart);
        Process(processor, profileName, firstSequence + 1, material);
        Process(processor, profileName, firstSequence + 2, Quality);
        Process(processor, profileName, firstSequence + 3, Remaining);
    }

    private static void Process(VeinContextProcessor processor, string profileName, long sequence, string text) =>
        processor.Process(new OrderedEventLine(EventTransportTestProfileIds.For(profileName), profileName, sequence,
            new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text)));

    private static void ProcessNearby(NearbyScanProcessor processor, string profileName, long sequence, string text) =>
        processor.Process(new OrderedEventLine(EventTransportTestProfileIds.For(profileName), profileName, sequence,
            new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text)));
}
