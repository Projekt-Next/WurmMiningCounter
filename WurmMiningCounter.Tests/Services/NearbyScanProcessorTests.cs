using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class NearbyScanProcessorTests
{
    private const string Nearby = "There is iron (normal quality) nearby.";
    private const string NearbyOnlyRock = "There is only rock nearby.";
    private const string Salt = "You will find salt here!";
    private const string Flint = "You will find flint here!";
    private const string ProspectingStart = "You start to gather fragments of the rock.";
    private const string RockSalt = "You would mine rock salt here.";
    private const string Iron = "You would mine iron ore here.";
    private const string Quality = "It has a max quality of 12.5.";
    private const string Remaining = "You will be able to mine here 1103 more times.";

    [Fact]
    public void Process_ReplacesPreviousScanAndPreservesOrderedEntries()
    {
        var processor = new NearbyScanProcessor();

        ProcessLine(processor, "Alice", 1, "There is iron (normal quality) and iron (good quality) nearby.");
        ProcessLine(processor, "Alice", 2, "There is only rock nearby.");

        var snapshot = Assert.Single(processor.GetSnapshots());
        Assert.Equal("Alice", snapshot.ProfileName);
        Assert.True(snapshot.IsOnlyRock);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void Process_KeepsProfilesIsolated()
    {
        var processor = new NearbyScanProcessor();

        ProcessLine(processor, "Alice", 1, "There is iron nearby.");
        ProcessLine(processor, "Bob", 1, "There is rock salt (good quality) nearby.");

        var snapshots = processor.GetSnapshots();
        Assert.Equal(2, snapshots.Length);
        Assert.Equal("iron", snapshots.Single(snapshot => snapshot.ProfileName == "Alice").Entries.Single().MaterialText);
        Assert.Equal("rock salt", snapshots.Single(snapshot => snapshot.ProfileName == "Bob").Entries.Single().MaterialText);
    }

    [Theory]
    [InlineData("Logging started 2026-08-23")]
    [InlineData("Reconnecting Alice! Independence.")]
    public void Process_LoggingBoundaryClearsCurrentProfile(string boundary)
    {
        var processor = new NearbyScanProcessor();
        ProcessLine(processor, "Alice", 1, "There is iron nearby.");

        ProcessLine(processor, "Alice", 2, boundary);

        Assert.Empty(processor.GetSnapshots());
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("profile")]
    public void Process_ControlBoundaryClearsOnlyAffectedProfile(string kind)
    {
        var processor = new NearbyScanProcessor();
        ProcessLine(processor, "Alice", 1, "There is iron nearby.");
        ProcessLine(processor, "Bob", 1, "There is tin nearby.");

        OrderedEventItem boundary = kind switch
        {
            "stream" => new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 2, StreamGapReason.Replaced, null),
            _ => new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 2)
        };
        processor.Process(boundary);

        var snapshot = Assert.Single(processor.GetSnapshots());
        Assert.Equal("Bob", snapshot.ProfileName);
    }

    [Fact]
    public void Process_DoesNotAffectVeinContextOrMiningSemantics()
    {
        var processor = new NearbyScanProcessor();

        ProcessLine(processor, "Alice", 1, "There is iron (normal quality) nearby.");

        Assert.Single(processor.GetSnapshots());
    }

    [Theory]
    [InlineData(Nearby, Salt, ProspectingAugment.Salt)]
    [InlineData(Nearby, Flint, ProspectingAugment.Flint)]
    [InlineData(NearbyOnlyRock, Salt, ProspectingAugment.Salt)]
    [InlineData(NearbyOnlyRock, Flint, ProspectingAugment.Flint)]
    public void Process_EligibleNearbySnapshotAcceptsConfirmedAugment(
        string nearby,
        string augmentLine,
        ProspectingAugment expectedAugment)
    {
        var processor = new NearbyScanProcessor();

        ProcessLine(processor, "Alice", 1, nearby);
        ProcessLine(processor, "Alice", 2, augmentLine);

        var snapshot = Assert.Single(processor.GetSnapshots());
        Assert.Equal([expectedAugment], snapshot.Augments);
    }

    [Fact]
    public void Process_NearbySaltThenFlintUsesImmutableReplacementInFirstSeenOrder()
    {
        var processor = new NearbyScanProcessor();
        var events = new List<NearbyScanChangedEventArgs>();
        processor.NearbyScanChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Nearby);
        ProcessLine(processor, "Alice", 2, Salt);
        ProcessLine(processor, "Alice", 3, Flint);

        Assert.Collection(
            events,
            changed => Assert.Empty(changed.Snapshot!.Augments),
            changed => Assert.Equal([ProspectingAugment.Salt], changed.Snapshot!.Augments),
            changed => Assert.Equal(
                [ProspectingAugment.Salt, ProspectingAugment.Flint],
                changed.Snapshot!.Augments));
    }

    [Fact]
    public void Process_DuplicateAugmentIsNoOpAndKeepsTailEligible()
    {
        var processor = new NearbyScanProcessor();
        var events = new List<NearbyScanChangedEventArgs>();
        processor.NearbyScanChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Nearby);
        ProcessLine(processor, "Alice", 2, Flint);
        ProcessLine(processor, "Alice", 3, Flint);
        ProcessLine(processor, "Alice", 4, Salt);

        Assert.Equal(3, events.Count);
        Assert.Equal(
            [ProspectingAugment.Flint, ProspectingAugment.Salt],
            Assert.Single(processor.GetSnapshots()).Augments);
    }

    [Fact]
    public void Process_NonAugmentClosesEligibilityWithoutClearingPresentedSnapshot()
    {
        var processor = new NearbyScanProcessor();

        ProcessLine(processor, "Alice", 1, Nearby);
        ProcessLine(processor, "Alice", 2, "Some unrelated message.");
        ProcessLine(processor, "Alice", 3, Flint);

        var snapshot = Assert.Single(processor.GetSnapshots());
        Assert.Empty(snapshot.Augments);
    }

    [Fact]
    public void Process_AugmentOwnershipIsProfileIsolated()
    {
        var processor = new NearbyScanProcessor();

        ProcessLine(processor, "Alice", 1, Nearby);
        ProcessLine(processor, "Bob", 1, Salt);
        ProcessLine(processor, "Alice", 2, Flint);

        var snapshot = Assert.Single(processor.GetSnapshots());
        Assert.Equal("Alice", snapshot.ProfileName);
        Assert.Equal([ProspectingAugment.Flint], snapshot.Augments);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("profile")]
    [InlineData("invalidate")]
    public void Process_ResetOrContinuityLossPreventsLateAugment(string boundary)
    {
        var processor = new NearbyScanProcessor();
        ProcessLine(processor, "Alice", 1, Nearby);

        switch (boundary)
        {
            case "stream":
                processor.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 2, StreamGapReason.Replaced, null));
                break;
            case "profile":
                processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 2));
                break;
            default:
                processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");
                break;
        }
        ProcessLine(processor, "Alice", 3, Salt);

        Assert.Empty(processor.GetSnapshots());
    }

    [Fact]
    public void Process_RockSaltDirectSequenceCannotReceivePreviousNearbyAugment()
    {
        var processor = new NearbyScanProcessor();
        var veinProcessor = new VeinContextProcessor(invalidateNearbyScan: profileId => processor.Invalidate(profileId, "Alice"));
        var events = new List<NearbyScanChangedEventArgs>();
        processor.NearbyScanChanged += (_, args) => events.Add(args);

        ProcessLine(veinProcessor, processor, "Alice", 1, Nearby);
        ProcessLine(veinProcessor, processor, "Alice", 2, ProspectingStart);
        ProcessLine(veinProcessor, processor, "Alice", 3, RockSalt);
        ProcessLine(veinProcessor, processor, "Alice", 4, Quality);
        ProcessLine(veinProcessor, processor, "Alice", 5, Salt);

        Assert.Collection(
            events,
            changed => Assert.Empty(changed.Snapshot!.Augments),
            changed => Assert.Null(changed.Snapshot));
        Assert.Empty(processor.GetSnapshots());
        Assert.True(veinProcessor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.RockSalt, context!.Material);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_DirectVeinSequenceCannotReceivePreviousNearbyAugment()
    {
        var processor = new NearbyScanProcessor();
        var veinProcessor = new VeinContextProcessor(invalidateNearbyScan: profileId => processor.Invalidate(profileId, "Alice"));
        var events = new List<NearbyScanChangedEventArgs>();
        processor.NearbyScanChanged += (_, args) => events.Add(args);

        ProcessLine(veinProcessor, processor, "Alice", 1, Nearby);
        ProcessLine(veinProcessor, processor, "Alice", 2, ProspectingStart);
        ProcessLine(veinProcessor, processor, "Alice", 3, Iron);
        ProcessLine(veinProcessor, processor, "Alice", 4, Quality);
        ProcessLine(veinProcessor, processor, "Alice", 5, Remaining);
        ProcessLine(veinProcessor, processor, "Alice", 6, Flint);

        Assert.Collection(
            events,
            changed => Assert.Empty(changed.Snapshot!.Augments),
            changed => Assert.Null(changed.Snapshot));
        Assert.Empty(processor.GetSnapshots());
        Assert.True(veinProcessor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    private static void ProcessLine(NearbyScanProcessor processor, string profileName, long sequenceNumber, string text) =>
        processor.Process(new OrderedEventLine(EventTransportTestProfileIds.For(profileName), profileName, sequenceNumber, new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "event.txt", text)));

    [Fact]
    public void SameNameProfiles_KeepNearbyAndVeinContextIndependentAcrossInvalidate()
    {
        var first = PlayerProfileId.CreateNew(); var second = PlayerProfileId.CreateNew(); const string name = "Twin";
        var processor = new NearbyScanProcessor();
        var vein = new VeinContextProcessor();
        void Process(PlayerProfileId id, long sequence, string text)
        {
            var line = new OrderedEventLine(id, name, sequence, new EventLogLine(id, name, "event.txt", text));
            processor.Process(line); vein.Process(line);
        }
        Process(first, 1, "There is iron nearby."); Process(second, 1, "There is zinc nearby.");
        Process(first, 2, "You start to gather fragments of the rock."); Process(first, 3, "You would mine iron ore here."); Process(first, 4, "It has a max quality of 12.5."); Process(first, 5, "You will be able to mine here 1103 more times.");
        Process(second, 2, "You start to gather fragments of the rock."); Process(second, 3, "You would mine zinc ore here."); Process(second, 4, "It has a max quality of 12.5."); Process(second, 5, "You will be able to mine here 1103 more times.");
        Assert.Equal("iron", processor.GetSnapshots().Single(snapshot => snapshot.ProfileId == first).Entries.Single().MaterialText);
        Assert.Equal("zinc", processor.GetSnapshots().Single(snapshot => snapshot.ProfileId == second).Entries.Single().MaterialText);
        processor.Invalidate(first, name);
        vein.Invalidate(first, name);
        Assert.DoesNotContain(processor.GetSnapshots(), snapshot => snapshot.ProfileId == first);
        Assert.Equal("zinc", processor.GetSnapshots().Single(snapshot => snapshot.ProfileId == second).Entries.Single().MaterialText);
        Assert.False(vein.TryGetActiveContext(first, out _));
        Assert.True(vein.TryGetActiveContext(second, out var context));
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
    }

    private static void ProcessLine(
        VeinContextProcessor veinProcessor,
        NearbyScanProcessor nearbyProcessor,
        string profileName,
        long sequenceNumber,
        string text)
    {
        var line = new OrderedEventLine(EventTransportTestProfileIds.For(profileName), profileName, sequenceNumber, new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "event.txt", text));
        veinProcessor.Process(line);
        nearbyProcessor.Process(line);
    }
}
