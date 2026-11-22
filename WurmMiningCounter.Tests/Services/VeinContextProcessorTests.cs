using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class VeinContextProcessorTests
{
    [Fact]
    public async Task Process_DarkDungeonsLiveFifoClearsPartialResultBeforeNewProspecting()
    {
        var processor = new VeinContextProcessor();
        var snapshots = new List<VeinContextSnapshot?>();
        processor.ActiveContextChanged += (_, args) => snapshots.Add(args.Snapshot);
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(processor);
        coordinator.AcceptOutput(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", 1,
        [
            Start, Iron, Quality, Remaining,
            Start, Zinc, GoodQuality,
            "You see dark dungeons.",
            Start, Copper, Quality, Remaining
        ]));
        await coordinator.CompleteAsync();

        var clearedIndex = snapshots.FindIndex(snapshot => snapshot is null);
        Assert.True(clearedIndex > 0);
        Assert.Equal(VeinMaterial.Zinc, snapshots[clearedIndex - 1]!.Material);
        Assert.Equal(VeinMaterial.Copper, snapshots[clearedIndex + 1]!.Material);
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(VeinMaterial.Copper, context.Material);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
    }

    [Fact]
    public void Process_DarkDungeonsAfterMiningStartDoesNotClearPendingProspectingContext()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, MineForwards);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        ProcessLine(processor, "Alice", 4, "You see dark dungeons.");
        Assert.Same(attempt, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
        Assert.Same(context, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
    }

    [Theory]
    [InlineData("You see dark dungeons.")]
    [InlineData("[12:34:56] You see dark dungeons.")]
    public void Process_DarkDungeonsClearsContextAndAttemptWithoutChangingCountersAndAllowsNewProspecting(string failure)
    {
        var processor = new VeinContextProcessor();
        var counter = new MiningSessionCounter();
        var mining = new MiningEventProcessor(new MiningEventParser(), counter,
            new MiningInspirationTracker(new MiningInspirationCounter()));
        var changes = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => changes.Add(args);
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 2);
        ProcessCompleteSequence(processor, "Bob", 1, Zinc);
        ProcessLine(processor, "Alice", 5, MineForwards);
        mining.Process(new EventLogLine(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", "You mine some ore."));
        ProcessLine(processor, "Alice", 6, "You mine some ore.");

        ProcessLine(processor, "Alice", 7, Start);
        ProcessLine(processor, "Alice", 8, Iron);
        ProcessLine(processor, "Alice", 9, failure);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.Null(changes.Last().Snapshot);
        Assert.Equal(VeinMaterial.Zinc, GetActiveContext(processor, EventTransportTestProfileIds.Bob).Material);
        var snapshot = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, snapshot.TotalCount);
        Assert.Equal(1, snapshot.Materials[MiningMaterial.Ore]);

        ProcessLine(processor, "Alice", 10, MineForwards);
        Assert.Null(GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);
        ProcessLine(processor, "Alice", 11, "You mine some ore.");
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 12, Zinc, 3);
        var newContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(VeinMaterial.Zinc, newContext.Material);
        Assert.Equal(3, Assert.IsType<RemainingMeasurement.Exact>(newContext.Remaining).Value);
    }

    [Fact]
    public void Process_DarkDungeonsClearsPlainRockAndPreservesPendingProspecting()
    {
        var processor = new VeinContextProcessor();
        var changes = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => changes.Add(args);
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Start);
        ProcessLine(processor, "Alice", 4, Iron);
        ProcessLine(processor, "Alice", 5, "You see dark dungeons.");
        Assert.Null(changes.Last().Result);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        ProcessLine(processor, "Alice", 6, Quality);
        ProcessLine(processor, "Alice", 7, Remaining);
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(VeinMaterial.Iron, context.Material);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
    }

    [Fact]
    public void Process_DarkDungeonsPreservesInFlightPlainRockQuality()
    {
        var processor = new VeinContextProcessor();
        var changes = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => changes.Add(args);
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, "You see dark dungeons.");
        Assert.Null(changes.Last().Result);
        ProcessLine(processor, "Alice", 4, GoodQuality);
        Assert.Equal(ProspectingQualityGrade.Good,
            Assert.IsType<QualityMeasurement.Grade>(changes.Last().Result!.Quality).Value);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_DarkDungeonsDuringMiningRemainsInformational()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 2);
        ProcessLine(processor, "Alice", 5, MineForwards);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        ProcessLine(processor, "Alice", 6, "You see dark dungeons.");
        Assert.Same(attempt, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
        ProcessLine(processor, "Alice", 7, "You mine some ore.");
        Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(GetActiveContext(processor, EventTransportTestProfileIds.Alice).Remaining).Value);
    }

    [Theory]
    [InlineData("You would mine adamantine ore here.", VeinMaterial.Adamantine)]
    [InlineData("You would mine glimmersteel ore here.", VeinMaterial.Glimmersteel)]
    public void Process_RareOreExactRemainingDecrementsOnce(string prospecting, VeinMaterial material)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, prospecting, 2);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(material, context.Material);
        Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
    }

    [Theory]
    [InlineData("A vein of black adamantine emerges here.", "You would mine adamantine ore here.", VeinMaterial.Adamantine, "A vein of shiny glimmersteel emerges here.", "You would mine glimmersteel ore here.", VeinMaterial.Glimmersteel)]
    [InlineData("A vein of shiny glimmersteel emerges here.", "You would mine glimmersteel ore here.", VeinMaterial.Glimmersteel, "A vein of black adamantine emerges here.", "You would mine adamantine ore here.", VeinMaterial.Adamantine)]
    public void Process_RareOreDiscoveryMiningProspectingAndTransition(string firstEmergence, string firstProspecting, VeinMaterial first, string secondEmergence, string secondProspecting, VeinMaterial second)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, "You mine some ore.");
        ProcessLine(processor, "Alice", 2, firstEmergence);
        ProcessLine(processor, "Alice", 3, firstEmergence);
        Assert.Equal(first, GetActiveContext(processor, EventTransportTestProfileIds.Alice).Material);
        Assert.IsType<RemainingMeasurement.Unknown>(GetActiveContext(processor, EventTransportTestProfileIds.Alice).Remaining);
        ProcessLine(processor, "Alice", 4, MineForwards);
        ProcessLine(processor, "Alice", 5, "You mine some ore.");
        ProcessLine(processor, "Alice", 6, Start);
        ProcessLine(processor, "Alice", 7, firstProspecting);
        ProcessLine(processor, "Alice", 8, "It is of good quality.");
        ProcessLine(processor, "Alice", 9, "The ore will run out any hour.");
        var firstContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(first, firstContext.Material);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(firstContext.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.RunOutAnyHour, Assert.IsType<RemainingMeasurement.EstimateBand>(firstContext.Remaining).Value);
        ProcessLine(processor, "Alice", 10, secondEmergence);
        Assert.Equal(second, GetActiveContext(processor, EventTransportTestProfileIds.Alice).Material);
        ProcessLine(processor, "Alice", 11, MineForwards);
        ProcessLine(processor, "Alice", 12, "You mine some ore.");
        ProcessLine(processor, "Alice", 13, Start);
        ProcessLine(processor, "Alice", 14, secondProspecting);
        ProcessLine(processor, "Alice", 15, "It is of poor quality.");
        ProcessLine(processor, "Alice", 16, "The ore will run out any hour.");
        var secondContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(second, secondContext.Material);
        Assert.Equal(ProspectingQualityGrade.Poor, Assert.IsType<QualityMeasurement.Grade>(secondContext.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.RunOutAnyHour, Assert.IsType<RemainingMeasurement.EstimateBand>(secondContext.Remaining).Value);
    }

    private const string Start = "You start to gather fragments of the rock.";
    private const string Iron = "You would mine iron ore here.";
    private const string Copper = "You would mine copper ore here.";
    private const string Lead = "You would mine lead ore here.";
    private const string Zinc = "You would mine zinc ore here.";
    private const string Sandstone = "You would mine sandstone shards here.";
    private const string RockSalt = "You would mine rock salt here.";
    private const string SaltRockSalt = "You would mine salt rock salt here.";
    private const string Gold = "You would mine gold ore here.";
    private const string PlainRock = "You would mine stone shards here.";
    private const string StoneRockPlainRock = "You would mine stone rock shards here.";
    private const string Quality = "It has a max quality of 12.5.";
    private const string Remaining = "You will be able to mine here 1103 more times.";
    private const string GoodQuality = "It is of good quality.";
    private const string Salt = "You will find salt here!";
    private const string Flint = "You will find flint here!";
    private const string Nearby = "There is iron (good quality) nearby.";
    private const string FewWeeks = "Only a few weeks mining remain here.";
    private const string Stop = "You stop prospecting.";
    private const string MineForwards = "You start to mine forwards.";
    private const string MineUpwards = "You start to mine upwards.";
    private const string MineUp = "You start to mine up.";
    private const string MineDown = "You start to mine down.";
    private const string MineFloor = "You start to mine the floor.";
    private const string MineCeiling = "You start to mine the ceiling.";
    private const string MineSurface = "You start to mine.";
    private const string MineTunnel = "You start to tunnel.";
    private const string WallBreak = "The wall breaks!";
    private const string WallWillBreakSoon = "The wall will break soon.";
    private const string DeepShaftWarning = "A deep shaft will probably emerge.";
    private const string StopMining = "You stop mining.";
    private const string TooBusy = "You're too busy.";
    private const string ChipAwayAtRock = "You chip away at the rock.";
    private const string SurfaceTooFlat =
        "You realize you can't open a tunnel here, because the surface is too flat.";
    private const string SandstoneExamine =
        "A vein of sand-sized minerals or rock grains, composed of quartz or feldspar, because these are the most common minerals in the Wurm's crust.";
    private const string RockSaltExamine =
        "A vein of crystalized sediment formed over time by evaporation.";
    private const string IronEmergence = "A vein of pure iron emerges here.";
    private const string CopperEmergence = "A vein of pure copper emerges here.";
    private const string LeadEmergence = "A vein of pure lead emerges here.";
    private const string GoldEmergence = "A vein of pure gold emerges here.";
    private const string Marble = "You would mine marble shards here.";
    private const string Slate = "You would mine slate shards here.";
    private const string MarbleEmergence =
        "A vein of an interlocking mosaic of carbonate crystals, otherwise known as marble, emerges here.";
    private const string SlateEmergence =
        "A vein of fine-grained, foliated, homogeneous metamorphic rock, or slate as we know it, emerges here.";

    [Fact]
    public void Process_EmergenceCreatesTypeOnlyContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, IronEmergence);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_GoldEmergenceCreatesTypeOnlyContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, GoldEmergence);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Gold, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_EmergenceInsideProspectingCompletesLaterDirectIronExactResult()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, IronEmergence);
        ProcessLine(processor, "Alice", 3, Iron);
        ProcessLine(processor, "Alice", 4, "It has a max quality of 45.");
        ProcessLine(processor, "Alice", 5, "You will be able to mine here 1681 more times.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.Equal(45m, Assert.IsType<QualityMeasurement.ExactMax>(context.Quality).Value);
        Assert.Equal(1681, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_EmergenceInsideProspectingCompletesLaterDirectCopperGradeEstimateResult()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, CopperEmergence);
        ProcessLine(processor, "Alice", 3, Copper);
        ProcessLine(processor, "Alice", 4, GoodQuality);
        ProcessLine(processor, "Alice", 5, FewWeeks);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Copper, context!.Material);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(context.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_EmergenceInsideProspectingThenPlainRockExactMaxClearsContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, IronEmergence);
        ProcessLine(processor, "Alice", 3, PlainRock);
        ProcessLine(processor, "Alice", 4, Quality);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_EmergenceInsideProspectingThenPlainRockGradeClearsContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, IronEmergence);
        ProcessLine(processor, "Alice", 3, PlainRock);
        ProcessLine(processor, "Alice", 4, "It is of normal quality.");

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_EmergenceAfterDirectMaterialStillDiscardsPartialProspectingSequence()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, LeadEmergence);
        ProcessLine(processor, "Alice", 4, Quality);
        ProcessLine(processor, "Alice", 5, Remaining);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Lead, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_ContradictingEmergenceReplacesExactContextAndCancelsBoundAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Zinc);
        ProcessLine(processor, "Alice", 5, MineForwards);
        Assert.NotNull(GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);

        ProcessLine(processor, "Alice", 6, IronEmergence);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_DescriptiveMarbleEmergenceCancelsBoundSandstoneAttemptAndAllowsProgressiveUpgrade()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithDetails(processor, "Alice", 1, Sandstone, 87m, 5571);
        ProcessLine(processor, "Alice", 5, MineForwards);
        Assert.NotNull(GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);

        ProcessLine(processor, "Alice", 6, MarbleEmergence);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var marbleContext));
        Assert.Equal(VeinMaterial.Marble, marbleContext!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(marbleContext.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(marbleContext.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, marbleContext.Confidence);

        ProcessLine(processor, "Alice", 7, "You mine some shards.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var afterPrimary));
        Assert.Same(marbleContext, afterPrimary);

        ProcessLine(processor, "Alice", 8, Start);
        ProcessLine(processor, "Alice", 9, Marble);
        ProcessLine(processor, "Alice", 10, GoodQuality);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var partialContext));
        Assert.Equal(VeinConfidence.Partial, partialContext!.Confidence);
        ProcessLine(processor, "Alice", 11, FewWeeks);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var synchronizedContext));
        Assert.Equal(VeinMaterial.Marble, synchronizedContext!.Material);
        Assert.Equal(VeinConfidence.Synchronized, synchronizedContext.Confidence);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(synchronizedContext.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(synchronizedContext.Remaining).Value);
    }

    [Fact]
    public void Process_DescriptiveMarbleEmergenceAfterWallBreakCancelsAttemptAndPreservesNewContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithDetails(processor, "Alice", 1, Sandstone, 87m, 5571);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, WallBreak);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);

        ProcessLine(processor, "Alice", 7, MarbleEmergence);
        ProcessLine(processor, "Alice", 8, "You mine some shards.");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Marble, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_DescriptiveSlateEmergenceCreatesIndependentTypeOnlyContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Sandstone);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, SlateEmergence);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Slate, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_SameMaterialEmergencePreservesExactContextAndBoundAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var exactContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, IronEmergence);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(exactContext, context);
        Assert.Same(originalAttempt, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void Process_EmergenceContextsAreIndependentPerProfileAndDoNotBindDepletion()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, IronEmergence);
        ProcessLine(processor, "Bob", 1, LeadEmergence);
        ProcessLine(processor, "Bob", 2, MineForwards);
        ProcessLine(processor, "Bob", 3, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var aliceContext));
        Assert.Equal(VeinMaterial.Iron, aliceContext!.Material);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bobContext));
        Assert.Equal(VeinMaterial.Lead, bobContext!.Material);
        Assert.Null(GetActiveAttempt(processor, EventTransportTestProfileIds.Bob).BoundVeinContext);
    }

    [Fact]
    public void Process_CompleteSequenceCreatesSynchronizedActiveContext()
    {
        var processor = new VeinContextProcessor();

        ProcessCompleteSequence(processor, "Alice", 1, Zinc);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotNull(context);
        Assert.Equal(VeinMaterial.Zinc, context.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(context.Quality).Value);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_CompleteGoldSequenceReplacesPriorContextWithSynchronizedGoldContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        ProcessCompleteSequence(processor, "Alice", 5, Gold);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Gold, context!.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(context.Quality).Value);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_CompleteEstimatedSequenceCreatesSynchronizedActiveContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, FewWeeks);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotNull(context);
        Assert.Equal(VeinMaterial.Iron, context.Material);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(context.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_LocalMiningDoesNotDecrementEstimatedRemaining()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, FewWeeks);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var synchronizedContext));

        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");

        Assert.Null(GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(synchronizedContext, context);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_ReprospectingReplacesEstimatedContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, FewWeeks);
        ProcessCompleteSequence(processor, "Alice", 5, Zinc);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
        Assert.IsType<QualityMeasurement.ExactMax>(context.Quality);
        Assert.IsType<RemainingMeasurement.Exact>(context.Remaining);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_MaterialCreatesTypeOnlyActiveContextBeforeProspectingCompletes()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_QualityUpgradesTypeOnlyContextToPartial()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(context.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.Partial, context.Confidence);
    }

    [Fact]
    public void Process_RockSaltGradeAndDirectSaltCreatesSynchronizedUnknownRemainingContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, RockSalt);
        ProcessLine(processor, "Alice", 3, "It is of very good quality.");
        ProcessLine(processor, "Alice", 4, Salt);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.RockSalt, context!.Material);
        Assert.Equal(
            ProspectingQualityGrade.VeryGood,
            Assert.IsType<QualityMeasurement.Grade>(context.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_SaltRockSaltAliasGradeAndDirectSaltReplacesPriorContextWithSynchronizedRockSalt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, SaltRockSalt);
        ProcessLine(processor, "Alice", 7, "It is of poor quality.");
        ProcessLine(processor, "Alice", 8, Salt);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(originalContext, context);
        Assert.Equal(VeinMaterial.RockSalt, context!.Material);
        Assert.Equal(ProspectingQualityGrade.Poor, Assert.IsType<QualityMeasurement.Grade>(context.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_ExactMaxAndEstimateBandCreateSynchronizedActiveContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, FewWeeks);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(context!.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_ProgressivelyUpgradesContextFromTypeOnlyToPartialToSynchronized()
    {
        var processor = new VeinContextProcessor();
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, FewWeeks);

        Assert.Equal(
            [VeinConfidence.TypeOnly, VeinConfidence.Partial, VeinConfidence.Synchronized],
            events.Select(args => args.Snapshot!.Confidence));
    }

    [Fact]
    public void Process_ReprospectingAfterPartialDoesNotMixOldQualityWithNewMaterial()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, Start);
        ProcessLine(processor, "Alice", 5, Zinc);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_PartialContextIsIndependentFromAnotherProfile()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Bob", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Bob", 2, Zinc);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Bob", 3, Quality);
        ProcessLine(processor, "Bob", 4, Remaining);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var aliceContext));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bobContext));
        Assert.Equal(VeinMaterial.Iron, aliceContext!.Material);
        Assert.Equal(VeinConfidence.Partial, aliceContext.Confidence);
        Assert.IsType<RemainingMeasurement.Unknown>(aliceContext.Remaining);
        Assert.Equal(VeinMaterial.Zinc, bobContext!.Material);
        Assert.Equal(VeinConfidence.Synchronized, bobContext.Confidence);
        Assert.IsType<RemainingMeasurement.Exact>(bobContext.Remaining);
    }

    [Fact]
    public void Process_EstimatedProspectingReplacesTypeOnlyContextWithSynchronizedContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var typeOnlyContext));
        Assert.Equal(VeinConfidence.TypeOnly, typeOnlyContext!.Confidence);

        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, FewWeeks);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(typeOnlyContext, context);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(context!.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_ExactProspectingReplacesTypeOnlyContextWithSynchronizedContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Sandstone);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var typeOnlyContext));
        Assert.Equal(VeinConfidence.TypeOnly, typeOnlyContext!.Confidence);

        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Remaining);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(typeOnlyContext, context);
        Assert.IsType<QualityMeasurement.ExactMax>(context!.Quality);
        Assert.IsType<RemainingMeasurement.Exact>(context.Remaining);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_TypeOnlyContextDoesNotBindWallAttemptOrDecrement()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Lead);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var typeOnlyContext));

        ProcessLine(processor, "Alice", 3, MineForwards);
        ProcessLine(processor, "Alice", 4, "You mine some ore.");

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Null(attempt.BoundVeinContext);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(typeOnlyContext, context);
        Assert.IsType<RemainingMeasurement.Unknown>(context!.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_StopProspectingDoesNotCreateAnotherContextAfterTypeOnlyMaterial()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, Zinc);
        ProcessLine(processor, "Alice", 7, Stop);
        ProcessLine(processor, "Alice", 8, Quality);
        ProcessLine(processor, "Alice", 9, Remaining);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(originalContext, context);
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_ProfilesHaveIndependentAssemblersAndContexts()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Bob", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Bob", 2, Zinc);
        ProcessLine(processor, "Bob", 3, Quality);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Remaining);
        ProcessLine(processor, "Bob", 4, Remaining);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var aliceContext));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bobContext));
        Assert.Equal(VeinMaterial.Iron, aliceContext!.Material);
        Assert.Equal(VeinMaterial.Zinc, bobContext!.Material);
    }

    [Fact]
    public void GetActiveContextSnapshots_ReturnsEmptyWhenNoContextExists()
    {
        var processor = new VeinContextProcessor();

        var snapshots = processor.GetActiveContextSnapshots();

        Assert.Empty(snapshots);
    }

    [Fact]
    public void GetActiveContextSnapshots_ReturnsSnapshotsForMultipleProfiles()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessCompleteSequence(processor, "Bob", 1, Zinc);

        var snapshots = processor.GetActiveContextSnapshots()
            .ToDictionary(snapshot => snapshot.ProfileName, StringComparer.Ordinal);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(VeinMaterial.Iron, snapshots["Alice"].Material);
        Assert.Equal(VeinMaterial.Zinc, snapshots["Bob"].Material);
    }

    [Fact]
    public void GetActiveContextSnapshots_OmitsProfilesWithoutActiveContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Bob", 1, "Some unrelated message.");

        var snapshot = Assert.Single(processor.GetActiveContextSnapshots());

        Assert.Equal("Alice", snapshot.ProfileName);
    }

    [Fact]
    public void GetActiveContextSnapshots_CopiesCurrentContextValues()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Zinc);
        ProcessLine(processor, "Alice", 3, "It has a max quality of 47.5.");
        ProcessLine(processor, "Alice", 4, "You will be able to mine here 27 more times.");

        var snapshot = Assert.Single(processor.GetActiveContextSnapshots());

        Assert.Equal("Alice", snapshot.ProfileName);
        Assert.Equal(VeinMaterial.Zinc, snapshot.Material);
        Assert.Equal(47.5m, Assert.IsType<QualityMeasurement.ExactMax>(snapshot.Quality).Value);
        Assert.Equal(27, Assert.IsType<RemainingMeasurement.Exact>(snapshot.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, snapshot.Confidence);
    }

    [Fact]
    public void ActiveContextChanged_EmitsSnapshotWhenContextIsCreated()
    {
        var processor = new VeinContextProcessor();
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        Assert.Collection(
            events,
            contextChanged =>
            {
                Assert.Equal(EventTransportTestProfileIds.Alice, contextChanged.ProfileId);
                Assert.NotNull(contextChanged.Snapshot);
                Assert.Equal(VeinMaterial.Iron, contextChanged.Snapshot!.Material);
                Assert.Equal(VeinConfidence.TypeOnly, contextChanged.Snapshot.Confidence);
            },
            contextChanged =>
            {
                Assert.Equal(EventTransportTestProfileIds.Alice, contextChanged.ProfileId);
                Assert.NotNull(contextChanged.Snapshot);
                Assert.Equal(VeinMaterial.Iron, contextChanged.Snapshot!.Material);
                Assert.Equal(VeinConfidence.Partial, contextChanged.Snapshot.Confidence);
            },
            contextChanged =>
            {
                Assert.Equal(EventTransportTestProfileIds.Alice, contextChanged.ProfileId);
                Assert.NotNull(contextChanged.Snapshot);
                Assert.Equal(VeinMaterial.Iron, contextChanged.Snapshot!.Material);
                Assert.Equal(VeinConfidence.Synchronized, contextChanged.Snapshot.Confidence);
            });
    }

    [Fact]
    public void ActiveContextChanged_EmitsTrackingSnapshotAfterGuardedDecrement()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");

        var contextChanged = Assert.Single(events);
        Assert.NotNull(contextChanged.Snapshot);
        Assert.True(contextChanged.IsLocalExactDecrement);
        Assert.Equal(1102, Assert.IsType<RemainingMeasurement.Exact>(contextChanged.Snapshot!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, contextChanged.Snapshot.Confidence);
    }

    [Fact]
    public void ActiveContextChanged_EmitsNullSnapshotWhenContextIsCleared()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");

        var contextChanged = Assert.Single(events);
        Assert.Equal(EventTransportTestProfileIds.Alice, contextChanged.ProfileId);
        Assert.Null(contextChanged.Snapshot);
    }

    [Fact]
    public void ActiveContextChanged_DoesNotEmitForMatchingExamineOrNoOp()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Sandstone);
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 5, SandstoneExamine);
        ProcessLine(processor, "Alice", 6, "Some unrelated message.");

        Assert.Empty(events);
    }

    [Fact]
    public void Process_StreamContinuityLostClearsOnlyAffectedProfile()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessCompleteSequence(processor, "Bob", 1, Zinc);

        processor.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 5, StreamGapReason.Truncated, null));

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var aliceContext));
        Assert.Null(aliceContext);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bobContext));
        Assert.Equal(VeinMaterial.Zinc, bobContext!.Material);
    }

    [Fact]
    public void Process_ResetProfileBarrierClearsPartialAndActiveContextForProfile()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, Zinc);

        processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 7));
        ProcessLine(processor, "Alice", 8, Quality);
        ProcessLine(processor, "Alice", 9, Remaining);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_ResetProfileBarrierClearsOnlyTargetProfileContextPendingProspectingAndMiningAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessCompleteSequence(processor, "Bob", 1, Zinc);
        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, Lead);
        ProcessLine(processor, "Alice", 7, MineForwards);
        Assert.True(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));

        processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 8));
        ProcessLine(processor, "Alice", 9, Quality);
        ProcessLine(processor, "Alice", 10, Remaining);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var aliceContext));
        Assert.Null(aliceContext);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var aliceAttempt));
        Assert.Null(aliceAttempt);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bobContext));
        Assert.Equal(VeinMaterial.Zinc, bobContext!.Material);
    }

    [Fact]
    public void Process_ResetProfileBarrierEmitsNullSnapshotWhenContextExists()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 5));

        var contextChanged = Assert.Single(events);
        Assert.Equal(EventTransportTestProfileIds.Alice, contextChanged.ProfileId);
        Assert.Null(contextChanged.Snapshot);
    }

    [Fact]
    public void Invalidate_ClearsStateAndIsIdempotent()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");
        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_ProfileCanCreateNewContextAfterStateIsCleared()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        processor.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 5, StreamGapReason.Replaced, null));

        ProcessCompleteSequence(processor, "Alice", 6, Zinc);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Zinc, context!.Material);
    }

    [Fact]
    public void Process_SandstoneExamineWithoutContextCreatesTypeOnlyContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, SandstoneExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotNull(context);
        Assert.Equal(VeinMaterial.Sandstone, context.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_RockSaltExamineWithoutContextCreatesTypeOnlyContext()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, RockSaltExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.RockSalt, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_MatchingSandstoneExamineDoesNotChangeExistingContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Sandstone);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, SandstoneExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        Assert.Equal(VeinConfidence.Synchronized, context!.Confidence);
        Assert.IsType<QualityMeasurement.ExactMax>(context.Quality);
        Assert.IsType<RemainingMeasurement.Exact>(context.Remaining);
        Assert.Same(originalAttempt, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void Process_MatchingRockSaltExamineDoesNotChangeExistingContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, RockSalt);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, RockSaltExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        Assert.Equal(VeinConfidence.Synchronized, context!.Confidence);
        Assert.IsType<QualityMeasurement.ExactMax>(context.Quality);
        Assert.IsType<RemainingMeasurement.Exact>(context.Remaining);
    }

    [Fact]
    public void Process_ConflictingSandstoneExamineReplacesContextWithTypeOnly()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, SandstoneExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(originalContext, context);
        Assert.Equal(VeinMaterial.Sandstone, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_ConflictingRockSaltExamineReplacesContextWithTypeOnly()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, RockSaltExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(originalContext, context);
        Assert.Equal(VeinMaterial.RockSalt, context!.Material);
        Assert.IsType<QualityMeasurement.Unknown>(context.Quality);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
    }

    [Fact]
    public void Process_FinalizedPlainRockClearsActiveContextWithoutRetargetingWallAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, Start);
        ProcessLine(processor, "Alice", 7, PlainRock);
        ProcessLine(processor, "Alice", 8, Quality);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Same(originalContext, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_FinalizedStoneRockPlainRockClearsActiveContextWithoutCreatingNewContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, StoneRockPlainRock);
        ProcessLine(processor, "Alice", 7, Quality);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.NotNull(originalContext);
    }

    [Theory]
    [InlineData(PlainRock)]
    [InlineData(StoneRockPlainRock)]
    public void Process_FinalizedPlainRockWithGradeClearsActiveContextWithoutCreatingNewContext(
        string plainRock)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, plainRock);
        ProcessLine(processor, "Alice", 7, "It is of normal quality.");

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.NotNull(originalContext);
    }

    [Fact]
    public void Process_FinalizedPlainRockWithGradePublishesPresentationResultWithoutCreatingContext()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, "It is of acceptable quality.");

        Assert.Collection(
            events,
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality),
            resultChanged => Assert.Equal(
                ProspectingQualityGrade.Acceptable,
                Assert.IsType<QualityMeasurement.Grade>(resultChanged.Result!.Quality).Value));
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_FinalizedPlainRockWithExactMaxPublishesPresentationResultWithoutCreatingContext()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);

        Assert.Collection(
            events,
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality),
            resultChanged => Assert.Equal(
                12.5m,
                Assert.IsType<QualityMeasurement.ExactMax>(resultChanged.Result!.Quality).Value));
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_SynchronizedVeinAfterPlainRockClearsPresentationResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessCompleteSequence(processor, "Alice", 4, Iron);

        Assert.Collection(
            events,
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality),
            resultChanged => Assert.IsType<QualityMeasurement.ExactMax>(resultChanged.Result!.Quality),
            resultChanged => Assert.Null(resultChanged.Result));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinConfidence.Synchronized, context!.Confidence);
    }

    [Fact]
    public void Process_NewMaterialOnlyPlainRockReplacesPreviousQualityWithoutStaleQuality()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, "It is of acceptable quality.");
        ProcessLine(processor, "Alice", 4, Start);
        ProcessLine(processor, "Alice", 5, PlainRock);

        Assert.Collection(
            events,
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality),
            resultChanged => Assert.Equal(
                ProspectingQualityGrade.Acceptable,
                Assert.IsType<QualityMeasurement.Grade>(resultChanged.Result!.Quality).Value),
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality));
    }

    [Fact]
    public void Process_RepeatedMaterialOnlyPlainRockResultsDoNotInheritQuality()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Start);
        ProcessLine(processor, "Alice", 4, StoneRockPlainRock);
        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, PlainRock);

        Assert.IsType<QualityMeasurement.Unknown>(Assert.Single(events).Result!.Quality);
    }

    [Fact]
    public void Process_MaterialOnlyPlainRockClearsExistingVeinAndPublishesPresentationResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);
        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, PlainRock);

        var result = Assert.IsType<ProspectingResult.PlainRock>(Assert.Single(events).Result);
        Assert.IsType<QualityMeasurement.Unknown>(result.Quality);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_EmergenceThenMaterialOnlyPlainRockClearsContextAndPublishesResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, IronEmergence);
        ProcessLine(processor, "Alice", 3, PlainRock);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.IsType<QualityMeasurement.Unknown>(Assert.Single(events).Result!.Quality);
    }

    [Fact]
    public void Invalidate_ClearsPlainRockPresentationResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);

        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");

        Assert.Collection(
            events,
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality),
            resultChanged => Assert.IsType<QualityMeasurement.ExactMax>(resultChanged.Result!.Quality),
            resultChanged => Assert.Null(resultChanged.Result));
    }

    [Fact]
    public void Process_SurfaceMiningClearsPlainRockPresentationResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);

        ProcessLine(processor, "Alice", 4, MineSurface);

        Assert.Collection(
            events,
            resultChanged => Assert.IsType<QualityMeasurement.Unknown>(resultChanged.Result!.Quality),
            resultChanged => Assert.IsType<QualityMeasurement.ExactMax>(resultChanged.Result!.Quality),
            resultChanged => Assert.Null(resultChanged.Result));
    }

    [Fact]
    public void Process_SessionBoundaryDiscardsPlainRockPartialBeforeQuality()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, PlainRock);

        ProcessLine(processor, "Alice", 7, "Logging started 2026-08-21");
        ProcessLine(processor, "Alice", 8, Quality);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_PlainRockExactMaxAugmentsReplaceFinalizedResultInFirstSeenOrder()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Salt);
        ProcessLine(processor, "Alice", 5, Flint);

        Assert.Collection(
            events,
            changed => Assert.Empty(changed.Result!.Augments),
            changed => Assert.Empty(changed.Result!.Augments),
            changed => Assert.Equal([ProspectingAugment.Salt], changed.Result!.Augments),
            changed => Assert.Equal(
                [ProspectingAugment.Salt, ProspectingAugment.Flint],
                changed.Result!.Augments));
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(events[^1].Result!.Quality).Value);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_PlainRockGradeFlintReplacesFinalizedResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, StoneRockPlainRock);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, Flint);

        var result = Assert.IsType<ProspectingResult.PlainRock>(events[^1].Result);
        Assert.Equal(ProspectingQualityGrade.Good, Assert.IsType<QualityMeasurement.Grade>(result.Quality).Value);
        Assert.Equal([ProspectingAugment.Flint], result.Augments);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_PlainRockDuplicateAugmentIsIdempotentAndKeepsTailEligible()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Salt);
        ProcessLine(processor, "Alice", 5, Salt);
        ProcessLine(processor, "Alice", 6, Flint);

        Assert.Equal(4, events.Count);
        Assert.Equal(
            [ProspectingAugment.Salt, ProspectingAugment.Flint],
            events[^1].Result!.Augments);
    }

    [Fact]
    public void Process_PlainRockFlintDuplicateKeepsTailEligibleForLaterSalt()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Flint);
        ProcessLine(processor, "Alice", 5, Flint);
        ProcessLine(processor, "Alice", 6, Salt);

        Assert.Equal(4, events.Count);
        Assert.Equal(
            [ProspectingAugment.Flint, ProspectingAugment.Salt],
            events[^1].Result!.Augments);
    }

    [Fact]
    public void Process_NewRockSaltSequenceAfterPlainRockUsesExistingDirectSaltFlow()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Start);
        ProcessLine(processor, "Alice", 5, RockSalt);
        ProcessLine(processor, "Alice", 6, Quality);
        ProcessLine(processor, "Alice", 7, Salt);

        Assert.Collection(
            events,
            changed => Assert.Empty(changed.Result!.Augments),
            changed => Assert.Empty(changed.Result!.Augments),
            changed => Assert.Null(changed.Result));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.RockSalt, context!.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(context.Quality).Value);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Fact]
    public void Process_NewDirectVeinSequenceAfterPlainRockDoesNotUpgradeOldResult()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Start);
        ProcessLine(processor, "Alice", 5, Iron);
        ProcessLine(processor, "Alice", 6, Quality);
        ProcessLine(processor, "Alice", 7, Remaining);
        ProcessLine(processor, "Alice", 8, Flint);

        Assert.Collection(
            events,
            changed => Assert.Empty(changed.Result!.Augments),
            changed => Assert.Empty(changed.Result!.Augments),
            changed => Assert.Null(changed.Result));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
    }

    [Theory]
    [InlineData("Some unrelated message.")]
    [InlineData(Start)]
    [InlineData(MineForwards)]
    [InlineData(Nearby)]
    [InlineData("Logging started 2026-08-21")]
    [InlineData("Reconnecting Alice! Independence.")]
    public void Process_NonAugmentBoundaryBlocksLatePlainRockAugment(string boundary)
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, boundary);
        ProcessLine(processor, "Alice", 5, Flint);

        Assert.All(
            events.Where(changed => changed.Result is not null),
            changed => Assert.Empty(changed.Result!.Augments));
    }

    [Theory]
    [InlineData(MineFloor)]
    [InlineData(MineCeiling)]
    [InlineData(MineSurface)]
    [InlineData(MineTunnel)]
    public void Process_MiningStartVariantBlocksLatePlainRockAugment(string miningStart)
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, miningStart);
        ProcessLine(processor, "Alice", 5, Flint);

        Assert.All(
            events.Where(changed => changed.Result is not null),
            changed => Assert.Empty(changed.Result!.Augments));
    }

    [Fact]
    public void Process_MaterialOnlyPlainRockDoesNotAcceptAugment()
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Salt);

        Assert.Single(events);
        Assert.IsType<QualityMeasurement.Unknown>(events[0].Result!.Quality);
        Assert.Empty(events[0].Result!.Augments);
    }

    [Theory]
    [InlineData("continuity")]
    [InlineData("session-reset")]
    [InlineData("profile-reset")]
    [InlineData("invalidate")]
    public void Process_StateResetBlocksLatePlainRockAugment(string resetKind)
    {
        var processor = new VeinContextProcessor();
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        switch (resetKind)
        {
            case "continuity":
                processor.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 4, StreamGapReason.Truncated, null));
                break;
            case "session-reset":
                processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 4));
                break;
            case "profile-reset":
                processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 4));
                break;
            default:
                processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");
                break;
        }
        ProcessLine(processor, "Alice", 5, Flint);

        Assert.Null(events[^1].Result);
    }

    [Fact]
    public void Process_PlainRockAugmentIsProfileIsolatedAndDoesNotInvalidateNearby()
    {
        var invalidations = 0;
        var processor = new VeinContextProcessor(invalidateNearbyScan: _ => invalidations++);
        var events = new List<PlainRockProspectingResultChangedEventArgs>();
        processor.PlainRockProspectingResultChanged += (_, args) => events.Add(args);

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, PlainRock);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Bob", 1, Salt);
        ProcessLine(processor, "Alice", 4, Flint);

        Assert.Equal([ProspectingAugment.Flint], events[^1].Result!.Augments);
        Assert.Equal(0, invalidations);
    }

    [Fact]
    public void Process_QualifiedDirectVeinSaltReplacesActiveContextAndSnapshot()
    {
        var processor = new VeinContextProcessor();
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Salt);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.Equal(12.5m, Assert.IsType<QualityMeasurement.ExactMax>(context.Quality).Value);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
        Assert.Equal([ProspectingAugment.Salt], context.Augments);
        Assert.Equal([ProspectingAugment.Salt], events[^1].Snapshot!.Augments);
    }

    [Fact]
    public void Process_DirectSaltDuplicateIsNoOp()
    {
        var processor = new VeinContextProcessor();
        var events = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => events.Add(args);

        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Salt);
        ProcessLine(processor, "Alice", 6, Salt);

        Assert.Equal(4, events.Count);
        Assert.Equal([ProspectingAugment.Salt], GetActiveContext(processor, EventTransportTestProfileIds.Alice).Augments);
    }

    [Fact]
    public void Process_DirectGradeOrMissingRemainingDoesNotAcceptSalt()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        ProcessLine(processor, "Alice", 3, GoodQuality);
        ProcessLine(processor, "Alice", 4, Remaining);
        ProcessLine(processor, "Alice", 5, Salt);

        Assert.Empty(GetActiveContext(processor, EventTransportTestProfileIds.Alice).Augments);

        ProcessLine(processor, "Bob", 1, Start);
        ProcessLine(processor, "Bob", 2, Iron);
        ProcessLine(processor, "Bob", 3, Quality);
        ProcessLine(processor, "Bob", 4, Salt);

        Assert.Empty(GetActiveContext(processor, EventTransportTestProfileIds.Bob).Augments);
    }

    [Fact]
    public void Process_DirectFlintClosesSaltEligibility()
    {
        var processor = new VeinContextProcessor();

        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Flint);
        ProcessLine(processor, "Alice", 6, Salt);

        Assert.Empty(GetActiveContext(processor, EventTransportTestProfileIds.Alice).Augments);
    }

    [Theory]
    [InlineData("Some unrelated message.")]
    [InlineData(MineForwards)]
    [InlineData(Stop)]
    public void Process_NonSaltBoundaryBlocksLateDirectSalt(string boundary)
    {
        var processor = new VeinContextProcessor();

        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, boundary);
        ProcessLine(processor, "Alice", 6, Salt);

        Assert.Empty(GetActiveContext(processor, EventTransportTestProfileIds.Alice).Augments);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("invalidate")]
    public void Process_ResetOrContinuityLossBlocksLateDirectSalt(string resetKind)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        switch (resetKind)
        {
            case "stream":
                processor.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 5, StreamGapReason.Truncated, null));
                break;
            case "session":
                processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 5));
                break;
            case "profile":
                processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 5));
                break;
            default:
                processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");
                break;
        }
        ProcessLine(processor, "Alice", 6, Salt);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_DirectSaltOwnershipIsProfileIsolated()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        ProcessLine(processor, "Bob", 1, Salt);
        ProcessLine(processor, "Alice", 5, Salt);

        Assert.Equal([ProspectingAugment.Salt], GetActiveContext(processor, EventTransportTestProfileIds.Alice).Augments);
    }

    [Fact]
    public void Process_RockSaltDirectSaltDoesNotUseNormalDirectSaltUpgrade()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, RockSalt);
        ProcessLine(processor, "Alice", 3, Quality);
        ProcessLine(processor, "Alice", 4, Salt);

        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(VeinMaterial.RockSalt, context.Material);
        Assert.Empty(context.Augments);
    }

    [Fact]
    public void Process_PlainRockAndNearbyCloseDirectSaltEligibility()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);

        ProcessLine(processor, "Alice", 5, Nearby);
        ProcessLine(processor, "Alice", 6, Salt);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));

        ProcessCompleteSequence(processor, "Bob", 1, Iron);
        ProcessLine(processor, "Bob", 5, Start);
        ProcessLine(processor, "Bob", 6, PlainRock);
        ProcessLine(processor, "Bob", 7, Quality);
        ProcessLine(processor, "Bob", 8, Salt);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out _));
    }

    [Fact]
    public void Process_DirectSaltDoesNotChangeActiveMiningAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, Salt);

        Assert.Same(attempt, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
        Assert.Empty(GetActiveContext(processor, EventTransportTestProfileIds.Alice).Augments);
    }

    [Fact]
    public void Process_WallStartBindsQualifiedContextSnapshot()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));

        ProcessLine(processor, "Alice", 5, MineForwards);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Same(context, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_WallStartWithoutContextCreatesUnboundAttempt()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, MineUpwards);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_WallStartDoesNotBindTypeOnlyContext()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, SandstoneExamine);

        ProcessLine(processor, "Alice", 2, MineForwards);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_FloorStartCreatesUnboundAttemptWithoutChangingContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, MineFloor);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Floor, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_CeilingStartCreatesUnboundAttemptWithoutChangingContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 5, MineCeiling);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Ceiling, attempt.ActionContext);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_CeilingStartClearsNearbyScanWithoutChangingVeinContext()
    {
        var nearbyScanProcessor = new NearbyScanProcessor();
        var processor = new VeinContextProcessor(invalidateNearbyScan: profileId => nearbyScanProcessor.Invalidate(profileId, "Alice"));
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessNearbyLine(nearbyScanProcessor, "Alice", 1, "There is iron nearby.");

        ProcessLine(processor, "Alice", 5, MineCeiling);

        Assert.Empty(nearbyScanProcessor.GetSnapshots());
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Ceiling, attempt.ActionContext);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_SynchronizedDirectProspectingClearsNearbyScanAfterCreatingContext()
    {
        var nearbyScanProcessor = new NearbyScanProcessor();
        var processor = new VeinContextProcessor(invalidateNearbyScan: profileId => nearbyScanProcessor.Invalidate(profileId, "Alice"));
        ProcessNearbyLine(nearbyScanProcessor, "Alice", 1, "There is iron nearby.");

        ProcessCompleteSequence(processor, "Alice", 2, Iron);

        Assert.Empty(nearbyScanProcessor.GetSnapshots());
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(VeinMaterial.Iron, context!.Material);
        Assert.IsType<RemainingMeasurement.Exact>(context.Remaining);
    }

    [Fact]
    public void Process_ProspectingStartAloneDoesNotClearNearbyScan()
    {
        var nearbyScanProcessor = new NearbyScanProcessor();
        var processor = new VeinContextProcessor(invalidateNearbyScan: profileId => nearbyScanProcessor.Invalidate(profileId, "Alice"));
        ProcessNearbyLine(nearbyScanProcessor, "Alice", 1, "There is iron nearby.");

        ProcessLine(processor, "Alice", 2, Start);

        Assert.Single(nearbyScanProcessor.GetSnapshots());
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_SurfaceStartClearsAllVeinState()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, Zinc);

        ProcessLine(processor, "Alice", 7, MineSurface);
        ProcessLine(processor, "Alice", 8, Quality);
        ProcessLine(processor, "Alice", 9, Remaining);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_TunnellingStartClearsAllVeinState()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, Start);
        ProcessLine(processor, "Alice", 6, Zinc);

        ProcessLine(processor, "Alice", 7, MineTunnel);
        ProcessLine(processor, "Alice", 8, Quality);
        ProcessLine(processor, "Alice", 9, Remaining);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Theory]
    [InlineData("There is iron (normal quality) nearby.")]
    [InlineData("There is only rock nearby.")]
    public void Process_NearbyScanClearsAllVeinState(string nearbyScan)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, Start);
        ProcessLine(processor, "Alice", 7, Zinc);

        ProcessLine(processor, "Alice", 8, nearbyScan);
        ProcessLine(processor, "Alice", 9, Quality);
        ProcessLine(processor, "Alice", 10, Remaining);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_SurfaceInvalidationDoesNotAffectAnotherProfile()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessCompleteSequence(processor, "Bob", 1, Zinc);
        ProcessLine(processor, "Bob", 5, MineForwards);

        ProcessLine(processor, "Alice", 6, MineSurface);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Bob, out var bobContext));
        Assert.Equal(VeinMaterial.Zinc, bobContext!.Material);
        Assert.True(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Bob, out var bobAttempt));
        Assert.NotNull(bobAttempt!.BoundVeinContext);
    }

    [Theory]
    [InlineData("After you finish tunnelling you will start tunnelling again.")]
    [InlineData("OtherPlayer starts tunnelling.")]
    public void Process_TunnellingQueueOrThirdPersonMessageDoesNotClearState(string message)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, message);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        Assert.Same(originalAttempt, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void Process_SurfaceInvalidationDoesNotChangeMiningCountersOrTotal()
    {
        var veinProcessor = new VeinContextProcessor();
        ProcessCompleteSequence(veinProcessor, "Alice", 1, Iron);
        ProcessLine(veinProcessor, "Alice", 5, MineForwards);

        var miningCounter = new MiningSessionCounter();
        var miningProcessor = new MiningEventProcessor(
            new MiningEventParser(),
            miningCounter,
            new MiningInspirationTracker(new MiningInspirationCounter()));
        miningProcessor.Process(new EventLogLine(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", "You mine some ore."));

        ProcessLine(veinProcessor, "Alice", 6, MineSurface);

        var snapshot = miningCounter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, snapshot.TotalCount);
        Assert.Equal(1, snapshot.Materials[MiningMaterial.Ore]);
    }

    [Fact]
    public void Process_NewActionStartReplacesPreviousAttemptWithoutBindingInheritance()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        var wallAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.NotNull(wallAttempt.BoundVeinContext);

        ProcessLine(processor, "Alice", 6, MineFloor);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.NotSame(wallAttempt, attempt);
        Assert.Equal(MiningActionContext.Floor, attempt.ActionContext);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_ContradictingProspectingAfterWallStartCancelsBoundAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);

        ProcessCompleteSequence(processor, "Alice", 6, Zinc);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var activeContext));
        Assert.Equal(VeinMaterial.Zinc, activeContext!.Material);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_ConflictingSandstoneExamineAfterWallStartCancelsBoundAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);

        ProcessLine(processor, "Alice", 6, SandstoneExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var activeContext));
        Assert.Equal(VeinMaterial.Sandstone, activeContext!.Material);
        Assert.Equal(VeinConfidence.TypeOnly, activeContext.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_ConflictingRockSaltExamineAfterWallStartCancelsBoundAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);

        ProcessLine(processor, "Alice", 6, RockSaltExamine);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var activeContext));
        Assert.Equal(VeinMaterial.RockSalt, activeContext!.Material);
        Assert.Equal(VeinConfidence.TypeOnly, activeContext.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_ResetProfileBarrierClearsActiveMiningAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);

        processor.Process(new ResetProfileBarrier(EventTransportTestProfileIds.Alice, "Alice", 2));

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_StreamContinuityLostClearsActiveMiningAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);

        processor.Process(new StreamContinuityLost(EventTransportTestProfileIds.Alice, "Alice", 2, StreamGapReason.Replaced, null));

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Theory]
    [InlineData("Logging started 2026-08-21")]
    [InlineData("[12:00:00] Logging started 2026-08-21")]
    [InlineData("Reconnecting Alice! Independence.")]
    [InlineData("[12:00:00] Reconnecting Alice! Independence.")]
    public void Process_SessionBoundaryClearsPartialContextAndMiningAttempt(string boundary)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, Start);
        ProcessLine(processor, "Alice", 7, Zinc);

        ProcessLine(processor, "Alice", 8, boundary);
        ProcessLine(processor, "Alice", 9, Quality);
        ProcessLine(processor, "Alice", 10, Remaining);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Invalidate_ClearsActiveMiningAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);

        processor.Invalidate(EventTransportTestProfileIds.Alice, "Alice");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_ProfilesHaveIndependentMiningAttempts()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Bob", 1, MineFloor);

        var aliceAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        var bobAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Bob);
        Assert.Equal(MiningActionContext.Wall, aliceAttempt.ActionContext);
        Assert.NotNull(aliceAttempt.BoundVeinContext);
        Assert.Equal(MiningActionContext.Floor, bobAttempt.ActionContext);
        Assert.Null(bobAttempt.BoundVeinContext);
    }

    [Fact]
    public void Process_WallBreakMovesActiveWallAttemptToAwaitingPrimaryWithoutChangingBinding()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var boundContext));
        ProcessLine(processor, "Alice", 5, MineForwards);

        ProcessLine(processor, "Alice", 6, WallBreak);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
        Assert.Same(boundContext, attempt.BoundVeinContext);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_WallBreakMovesUnboundActiveWallAttemptToAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineUpwards);

        ProcessLine(processor, "Alice", 2, WallBreak);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Theory]
    [InlineData(MineFloor, MiningActionContext.Floor)]
    [InlineData(MineCeiling, MiningActionContext.Ceiling)]
    public void Process_WallBreakDoesNotChangeNonWallActiveAttempt(
        string actionStart,
        MiningActionContext expectedContext)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, actionStart);

        ProcessLine(processor, "Alice", 2, WallBreak);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(expectedContext, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
    }

    [Fact]
    public void Process_WallBreakWithoutAttemptIsNoOp()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, WallBreak);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_WallBreakDoesNotChangeAttemptAlreadyAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, WallBreak);

        ProcessLine(processor, "Alice", 3, WallBreak);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
    }

    [Theory]
    [InlineData(WallWillBreakSoon)]
    [InlineData(DeepShaftWarning)]
    public void Process_InformationalWarningDoesNotChangeActiveWallAttempt(string warning)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, warning);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Same(originalAttempt.BoundVeinContext, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_InformationalWarningDoesNotChangeWallAttemptAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, WallBreak);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 3, DeepShaftWarning);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
    }

    [Fact]
    public void Process_InformationalWarningWithoutAttemptIsNoOp()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, WallWillBreakSoon);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_PreStartRejectionWithoutAttemptIsNoOp()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, TooBusy);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_PreStartRejectionDoesNotChangeActiveBoundWallAttemptOrContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, TooBusy);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Same(originalAttempt.BoundVeinContext, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_PreStartRejectionDoesNotChangeWallAttemptAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, WallBreak);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 7, TooBusy);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
        Assert.Same(originalContext, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_NoYieldTerminalCompletesActiveWallAttemptWithoutChangingBinding()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var boundContext));
        ProcessLine(processor, "Alice", 5, MineForwards);

        ProcessLine(processor, "Alice", 6, StopMining);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Same(boundContext, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_NoYieldTerminalCompletesWallAttemptAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, WallBreak);

        ProcessLine(processor, "Alice", 3, StopMining);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_NoYieldTerminalCompletesFloorAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineFloor);

        ProcessLine(processor, "Alice", 2, StopMining);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Floor, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_NoYieldTerminalWithoutAttemptIsNoOp()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, StopMining);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Theory]
    [InlineData(MineForwards, MiningActionContext.Wall)]
    [InlineData(MineFloor, MiningActionContext.Floor)]
    [InlineData(MineCeiling, MiningActionContext.Ceiling)]
    public void Process_ChipAwayAtRockIsNoOpOutsideSurfaceAndTunnelling(
        string actionStart,
        MiningActionContext expectedContext)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, actionStart);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 2, ChipAwayAtRock);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(expectedContext, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
    }

    [Fact]
    public void Process_ChipAwayAtRockDoesNotChangeWallAttemptAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, WallBreak);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 3, ChipAwayAtRock);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
    }

    [Fact]
    public void Process_ChipAwayAtRockWithoutAttemptIsNoOp()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, ChipAwayAtRock);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Fact]
    public void Process_SurfaceTooFlatAfterSurfaceInvalidationIsNoOp()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineSurface);

        ProcessLine(processor, "Alice", 2, SurfaceTooFlat);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Theory]
    [InlineData(MineForwards, MiningActionContext.Wall)]
    [InlineData(MineFloor, MiningActionContext.Floor)]
    [InlineData(MineCeiling, MiningActionContext.Ceiling)]
    public void Process_SurfaceTooFlatIsNoOpOutsideSurface(
        string actionStart,
        MiningActionContext expectedContext)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, actionStart);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 2, SurfaceTooFlat);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(expectedContext, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
    }

    [Fact]
    public void Process_SurfaceTooFlatDoesNotChangeWallAttemptAwaitingPrimary()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, WallBreak);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 3, SurfaceTooFlat);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary, attempt.Phase);
    }

    [Fact]
    public void Process_SurfaceTooFlatWithoutAttemptIsNoOp()
    {
        var processor = new VeinContextProcessor();

        ProcessLine(processor, "Alice", 1, SurfaceTooFlat);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
    }

    [Theory]
    [InlineData(MineFloor, "You mine some stone shards.")]
    [InlineData(MineFloor, "You mine some ore.")]
    [InlineData(MineFloor, "You mine some shards.")]
    [InlineData(MineFloor, "You mine some rock salt.")]
    [InlineData(MineCeiling, "You mine some stone shards.")]
    [InlineData(MineCeiling, "You mine some ore.")]
    [InlineData(MineCeiling, "You mine some shards.")]
    [InlineData(MineCeiling, "You mine some rock salt.")]
    public void Process_PrimaryCompletesNonWallAttempt(string actionStart, string primary)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, actionStart);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 2, primary);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Equal(originalAttempt.ActionContext, attempt.ActionContext);
        Assert.Same(originalAttempt.BoundVeinContext, attempt.BoundVeinContext);
    }

    [Theory]
    [InlineData(Iron, "You mine some ore.")]
    [InlineData(Sandstone, "You mine some shards.")]
    [InlineData(RockSalt, "You mine some rock salt.")]
    public void Process_CompatiblePrimaryCompletesBoundWallAttempt(string prospectingMaterial, string primary)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, prospectingMaterial);
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, primary);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Same(originalAttempt.BoundVeinContext, attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_CompatiblePrimaryAfterWallBreakCompletesOnlyPendingAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        var changes = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => changes.Add(args);
        ProcessLine(processor, "Alice", 6, WallBreak);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        var wallBreakChange = Assert.Single(changes);
        Assert.False(wallBreakChange.IsLocalExactDecrement);

        ProcessLine(processor, "Alice", 7, "You mine some ore.");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.Same(originalContext, originalAttempt.BoundVeinContext);
        Assert.Single(changes);
    }

    [Fact]
    public void Process_LatePrimaryAfterWallBreakPreservesNewSameMaterialContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, WallBreak);
        Assert.Equal(MiningAttemptPhase.WallBrokenAwaitingPrimary,
            GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);

        ProcessCompleteSequence(processor, "Alice", 7, Iron);
        var newContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        var changes = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => changes.Add(args);

        ProcessLine(processor, "Alice", 11, "You mine some ore.");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.Same(newContext, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(newContext.Remaining).Value);
        Assert.Empty(changes);
    }

    [Fact]
    public void Process_WallBreakAfterSameMaterialContextReplacementPreservesNewContextAndCancelsOldAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        var boundContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        ProcessLine(processor, "Alice", 5, MineForwards);
        Assert.Same(boundContext, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);

        ProcessCompleteSequenceWithRemaining(processor, "Alice", 6, Iron, 47);
        var newContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.NotSame(boundContext, newContext);
        Assert.Same(boundContext, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);
        var changes = new List<VeinContextChangedEventArgs>();
        processor.ActiveContextChanged += (_, args) => changes.Add(args);

        ProcessLine(processor, "Alice", 10, WallBreak);

        Assert.Same(newContext, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
        Assert.Equal(47, Assert.IsType<RemainingMeasurement.Exact>(newContext.Remaining).Value);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.Empty(changes);

        ProcessLine(processor, "Alice", 11, "You mine some ore.");

        Assert.Same(newContext, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
        Assert.Equal(47, Assert.IsType<RemainingMeasurement.Exact>(newContext.Remaining).Value);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.Empty(changes);
    }

    [Fact]
    public void Process_WallBreakAfterContextWasClearedCancelsOldAttemptWithoutAlert()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        var boundContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, Start);
        ProcessLine(processor, "Alice", 7, PlainRock);
        ProcessLine(processor, "Alice", 8, Quality);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        Assert.Same(boundContext, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);

        ProcessLine(processor, "Alice", 9, WallBreak);

        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));

        ProcessCompleteSequenceWithRemaining(processor, "Alice", 10, Iron, 47);
        var newContext = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        ProcessLine(processor, "Alice", 14, "You mine some ore.");

        Assert.Same(newContext, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
        Assert.Equal(47, Assert.IsType<RemainingMeasurement.Exact>(newContext.Remaining).Value);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Theory]
    [InlineData(MineForwards)]
    [InlineData(MineUp)]
    [InlineData(MineDown)]
    public void Process_CompatiblePrimaryDecrementsBoundWallExactRemainingAndTransitionsToTracking(string miningStart)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, miningStart);

        ProcessLine(processor, "Alice", 6, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(originalContext, context);
        Assert.Equal(1102, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, context.Confidence);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Same(originalContext, attempt.BoundVeinContext);
    }

    [Theory]
    [InlineData(MineUp)]
    [InlineData(MineDown)]
    public void Process_RealDirectionalWallAliasPreservesTypeOnlyContextAndCreatesUnboundAttempt(string miningStart)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, Start);
        ProcessLine(processor, "Alice", 2, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));

        ProcessLine(processor, "Alice", 3, miningStart);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Null(attempt.BoundVeinContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
    }

    [Fact]
    public void Process_CompatiblePrimaryDecrementsTrackingBoundWallContextAndKeepsTracking()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var trackingContext));
        ProcessLine(processor, "Alice", 7, MineForwards);

        ProcessLine(processor, "Alice", 8, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.NotSame(trackingContext, context);
        Assert.Equal(1101, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, context.Confidence);
    }

    [Fact]
    public void Process_ConsecutiveBoundWallAttemptsEachDecrementTheirCurrentSnapshotOnce()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 3);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var synchronizedContext));
        Assert.Equal(VeinConfidence.Synchronized, synchronizedContext!.Confidence);
        Assert.Equal(3, Assert.IsType<RemainingMeasurement.Exact>(synchronizedContext.Remaining).Value);

        ProcessLine(processor, "Alice", 5, MineForwards);
        var firstAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(synchronizedContext, firstAttempt.BoundVeinContext);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var firstTrackingContext));
        Assert.NotSame(synchronizedContext, firstTrackingContext);
        Assert.Equal(2, Assert.IsType<RemainingMeasurement.Exact>(firstTrackingContext!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, firstTrackingContext.Confidence);
        Assert.Equal(3, Assert.IsType<RemainingMeasurement.Exact>(synchronizedContext.Remaining).Value);
        Assert.Equal(MiningAttemptPhase.Completed, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);

        ProcessLine(processor, "Alice", 7, MineForwards);
        var secondAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(firstTrackingContext, secondAttempt.BoundVeinContext);
        ProcessLine(processor, "Alice", 8, "You mine some ore.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var secondTrackingContext));
        Assert.NotSame(firstTrackingContext, secondTrackingContext);
        Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(secondTrackingContext!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, secondTrackingContext.Confidence);
        Assert.Equal(2, Assert.IsType<RemainingMeasurement.Exact>(firstTrackingContext.Remaining).Value);
        Assert.Equal(MiningAttemptPhase.Completed, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);

        ProcessLine(processor, "Alice", 9, MineForwards);
        var thirdAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(secondTrackingContext, thirdAttempt.BoundVeinContext);
        ProcessLine(processor, "Alice", 10, "You mine some ore.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var finalContext));
        Assert.NotSame(secondTrackingContext, finalContext);
        Assert.Equal(0, Assert.IsType<RemainingMeasurement.Exact>(finalContext!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, finalContext.Confidence);
        Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(secondTrackingContext.Remaining).Value);
        Assert.Equal(MiningAttemptPhase.Completed, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);
    }

    [Fact]
    public void Process_CompatiblePrimaryDecrementsExactOneToZeroAndTransitionsToTracking()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 1);
        ProcessLine(processor, "Alice", 5, MineForwards);

        ProcessLine(processor, "Alice", 6, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(0, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, context.Confidence);
        Assert.Equal(MiningAttemptPhase.Completed, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);
    }

    [Fact]
    public void Process_CompatibleFinalPrimaryAfterWallBreakAtExactOneClearsContextAndAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 1);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, WallBreak);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out _));

        ProcessLine(processor, "Alice", 7, "You mine some ore.");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_PrimaryAfterWallBreakDestructionCannotDecrementAgain()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, WallBreak);
        ProcessLine(processor, "Alice", 7, "You mine some ore.");

        ProcessLine(processor, "Alice", 8, "You mine some ore.");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Fact]
    public void Process_PrimaryWithExactZeroUnboundWallAttemptCompletesWithoutDecrement()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 0);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var originalContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        Assert.Null(GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);

        ProcessLine(processor, "Alice", 6, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(originalContext, context);
        Assert.Equal(0, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
        Assert.Equal(MiningAttemptPhase.Completed, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).Phase);
    }

    [Fact]
    public void Process_WallStartWithTrackingExactZeroCreatesUnboundAttemptWithoutChangingContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 1);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var zeroContext));
        Assert.Equal(0, Assert.IsType<RemainingMeasurement.Exact>(zeroContext!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, zeroContext.Confidence);

        ProcessLine(processor, "Alice", 7, MineForwards);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(zeroContext, context);
        Assert.Equal(0, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, context.Confidence);
        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningActionContext.Wall, attempt.ActionContext);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_PrimaryAfterContradictingReprospectingDoesNotChangeReplacementContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessCompleteSequence(processor, "Alice", 6, Zinc);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var replacementContext));

        ProcessLine(processor, "Alice", 10, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(replacementContext, context);
        Assert.Equal(1103, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_ContradictingReprospectingReplacesTrackingContextAndCancelsBoundAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithDetails(processor, "Alice", 1, Iron, 12.5m, 3);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some ore.");
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var trackingContext));
        Assert.Equal(VeinConfidence.Tracking, trackingContext!.Confidence);
        Assert.Equal(2, Assert.IsType<RemainingMeasurement.Exact>(trackingContext.Remaining).Value);

        ProcessLine(processor, "Alice", 7, MineForwards);
        Assert.Same(trackingContext, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice).BoundVeinContext);
        ProcessCompleteSequenceWithDetails(processor, "Alice", 8, Zinc, 45m, 17);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var replacementContext));
        Assert.NotSame(trackingContext, replacementContext);
        Assert.Equal(VeinMaterial.Zinc, replacementContext!.Material);
        Assert.Equal(45m, Assert.IsType<QualityMeasurement.ExactMax>(replacementContext.Quality).Value);
        Assert.Equal(17, Assert.IsType<RemainingMeasurement.Exact>(replacementContext.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, replacementContext.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));

        ProcessLine(processor, "Alice", 12, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var contextAfterPrimary));
        Assert.Same(replacementContext, contextAfterPrimary);
        Assert.Equal(VeinMaterial.Zinc, contextAfterPrimary!.Material);
        Assert.Equal(45m, Assert.IsType<QualityMeasurement.ExactMax>(contextAfterPrimary.Quality).Value);
        Assert.Equal(17, Assert.IsType<RemainingMeasurement.Exact>(contextAfterPrimary.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, contextAfterPrimary.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Fact]
    public void Process_PrimaryAfterConflictingExamineDoesNotChangeReplacementContext()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, SandstoneExamine);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var replacementContext));

        ProcessLine(processor, "Alice", 7, "You mine some ore.");

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Same(replacementContext, context);
        Assert.Equal(VeinMaterial.Sandstone, context!.Material);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
    }

    [Theory]
    [InlineData("You mine some ore.")]
    [InlineData("You mine some shards.")]
    [InlineData("You mine some rock salt.")]
    [InlineData("You mine some stone shards.")]
    public void Process_PrimaryCompletesUnboundWallAttempt(string primary)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 2, primary);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.Completed, attempt.Phase);
        Assert.Equal(originalAttempt.ActionContext, attempt.ActionContext);
        Assert.Null(attempt.BoundVeinContext);
    }

    [Fact]
    public void Process_UnboundOreCompletesBeforeSilverEmergesWithoutRetroactiveBinding()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, "You mine some ore.");
        var completed = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(MiningAttemptPhase.Completed, completed.Phase);
        Assert.Null(completed.BoundVeinContext);

        ProcessLine(processor, "Alice", 3, "A vein of pure silver emerges here.");
        Assert.Same(completed, GetActiveAttempt(processor, EventTransportTestProfileIds.Alice));
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Equal(VeinMaterial.Silver, context.Material);
        Assert.Equal(VeinConfidence.TypeOnly, context.Confidence);
        Assert.IsType<RemainingMeasurement.Unknown>(context.Remaining);
    }

    [Theory]
    [InlineData("You mine some ore.")]
    [InlineData("You mine some shards.")]
    [InlineData("You mine some rock salt.")]
    [InlineData("You mine some stone shards.")]
    public void Process_UnboundFinalPrimaryAfterWallBreakClosesAttemptAndPreservesNewContext(string primary)
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, WallBreak);
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 3, Iron, 2);
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        ProcessLine(processor, "Alice", 7, primary);
        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out _));
        Assert.Same(context, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
        Assert.Equal(2, Assert.IsType<RemainingMeasurement.Exact>(context.Remaining).Value);
        ProcessLine(processor, "Alice", 8, primary);
        Assert.Same(context, GetActiveContext(processor, EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public void Process_NewWallStartAfterUnboundCompletionBindsOnlyCurrentContext()
    {
        var processor = new VeinContextProcessor();
        ProcessLine(processor, "Alice", 1, MineForwards);
        ProcessLine(processor, "Alice", 2, "You mine some ore.");
        var completed = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 3, Iron, 2);
        var context = GetActiveContext(processor, EventTransportTestProfileIds.Alice);
        Assert.Null(completed.BoundVeinContext);
        ProcessLine(processor, "Alice", 7, MineForwards);
        var next = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.NotSame(completed, next);
        Assert.Same(context, next.BoundVeinContext);
        Assert.Equal(MiningAttemptPhase.Active, next.Phase);
    }

    [Theory]
    [InlineData(false, "You mine some stone shards.")]
    [InlineData(false, "You mine some shards.")]
    [InlineData(false, "You mine some rock salt.")]
    [InlineData(true, "You mine some stone shards.")]
    [InlineData(true, "You mine some shards.")]
    [InlineData(true, "You mine some rock salt.")]
    public void Process_IncompatiblePrimaryClearsBoundWallStateInBothWallPhases(
        bool wallBreakOccurred,
        string primary)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var boundContext));
        ProcessLine(processor, "Alice", 5, MineForwards);
        if (wallBreakOccurred)
        {
            ProcessLine(processor, "Alice", 6, WallBreak);
        }

        ProcessLine(processor, "Alice", wallBreakOccurred ? 7 : 6, primary);

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
        Assert.NotNull(boundContext);
    }

    [Fact]
    public void Process_CompatiblePrimaryAfterIncompatibleWallPrimaryCannotCompleteOldAttempt()
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequence(processor, "Alice", 1, Iron);
        ProcessLine(processor, "Alice", 5, MineForwards);
        ProcessLine(processor, "Alice", 6, "You mine some stone shards.");

        ProcessLine(processor, "Alice", 7, "You mine some ore.");

        Assert.False(processor.TryGetActiveMiningAttempt(EventTransportTestProfileIds.Alice, out var attempt));
        Assert.Null(attempt);
        Assert.False(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Null(context);
    }

    [Theory]
    [InlineData("You mine some salt.")]
    [InlineData("You find flint!")]
    [InlineData("You get a source crystal.")]
    [InlineData("You mine some pink crystals.")]
    [InlineData("You find a sapphire!")]
    [InlineData("You find a diamond!")]
    [InlineData("You find an emerald!")]
    [InlineData("You find a ruby!")]
    [InlineData("You find an opal!")]
    public void Process_BonusOrByproductDoesNotChangeAttempt(string bonus)
    {
        var processor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(processor, "Alice", 1, Iron, 2);
        ProcessLine(processor, "Alice", 5, MineForwards);
        var originalAttempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);

        ProcessLine(processor, "Alice", 6, bonus);

        var attempt = GetActiveAttempt(processor, EventTransportTestProfileIds.Alice);
        Assert.Same(originalAttempt, attempt);
        Assert.Equal(MiningAttemptPhase.Active, attempt.Phase);
        Assert.Same(originalAttempt.BoundVeinContext, attempt.BoundVeinContext);
        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(2, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
    }

    [Theory]
    [InlineData(MineForwards)]
    [InlineData(MineUp)]
    [InlineData(MineDown)]
    public void Process_ExactBoundVeinMoiAndPrimaryDecrementsRemainingExactlyOnce(string miningStart)
    {
        var veinProcessor = new VeinContextProcessor();
        ProcessCompleteSequenceWithRemaining(veinProcessor, "Alice", 1, Iron, 2);

        var miningCounter = new MiningSessionCounter();
        var inspirationCounter = new MiningInspirationCounter();
        var miningProcessor = new MiningEventProcessor(
            new MiningEventParser(),
            miningCounter,
            new MiningInspirationTracker(inspirationCounter));

        const string moi = "You have a moment of inspiration...";
        const string primary = "You mine some ore.";
        ProcessLine(veinProcessor, "Alice", 5, miningStart);
        miningProcessor.Process(new EventLogLine(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", miningStart));
        ProcessLine(veinProcessor, "Alice", 6, moi);
        miningProcessor.Process(new EventLogLine(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", moi));
        ProcessLine(veinProcessor, "Alice", 7, primary);
        miningProcessor.Process(new EventLogLine(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", primary));

        Assert.True(veinProcessor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var context));
        Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(context!.Remaining).Value);
        Assert.Equal(1, miningCounter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, inspirationCounter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    private static void ProcessCompleteSequence(
        VeinContextProcessor processor,
        string profileName,
        long firstSequenceNumber,
        string material)
    {
        ProcessLine(processor, profileName, firstSequenceNumber, Start);
        ProcessLine(processor, profileName, firstSequenceNumber + 1, material);
        ProcessLine(processor, profileName, firstSequenceNumber + 2, Quality);
        ProcessLine(processor, profileName, firstSequenceNumber + 3, Remaining);
    }

    private static void ProcessCompleteSequenceWithRemaining(
        VeinContextProcessor processor,
        string profileName,
        long firstSequenceNumber,
        string material,
        int remaining)
    {
        ProcessLine(processor, profileName, firstSequenceNumber, Start);
        ProcessLine(processor, profileName, firstSequenceNumber + 1, material);
        ProcessLine(processor, profileName, firstSequenceNumber + 2, Quality);
        ProcessLine(
            processor,
            profileName,
            firstSequenceNumber + 3,
            $"You will be able to mine here {remaining} more times.");
    }

    private static void ProcessCompleteSequenceWithDetails(
        VeinContextProcessor processor,
        string profileName,
        long firstSequenceNumber,
        string material,
        decimal quality,
        int remaining)
    {
        ProcessLine(processor, profileName, firstSequenceNumber, Start);
        ProcessLine(processor, profileName, firstSequenceNumber + 1, material);
        ProcessLine(processor, profileName, firstSequenceNumber + 2, $"It has a max quality of {quality}.");
        ProcessLine(
            processor,
            profileName,
            firstSequenceNumber + 3,
            $"You will be able to mine here {remaining} more times.");
    }

    private static void ProcessLine(
        VeinContextProcessor processor,
        string profileName,
        long sequenceNumber,
        string text)
    {
        processor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.For(profileName),
            profileName,
            sequenceNumber,
            new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text)));
    }

    private static void ProcessNearbyLine(
        NearbyScanProcessor processor,
        string profileName,
        long sequenceNumber,
        string text)
    {
        processor.Process(new OrderedEventLine(
            EventTransportTestProfileIds.For(profileName),
            profileName,
            sequenceNumber,
            new EventLogLine(EventTransportTestProfileIds.For(profileName), profileName, "Event.txt", text)));
    }

    private static MiningAttempt GetActiveAttempt(VeinContextProcessor processor, PlayerProfileId profileId)
    {
        Assert.True(processor.TryGetActiveMiningAttempt(profileId, out var attempt));
        return Assert.IsType<MiningAttempt>(attempt);
    }

    private static ActiveVeinContext GetActiveContext(VeinContextProcessor processor, PlayerProfileId profileId)
    {
        Assert.True(processor.TryGetActiveContext(profileId, out var context));
        return Assert.IsType<ActiveVeinContext>(context);
    }
}
