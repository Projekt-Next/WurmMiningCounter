using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningSessionCounterTests
{
    [Fact]
    public void AddCreatesProfileAndMaterial()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        var snapshot = counter.GetSnapshot();
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void AddIncrementsSameMaterial()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        Assert.Equal(3, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void AddIncrementsEveryMaterialButOnlyPrimaryIncrementsTotal()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.Equal(1, profile.Materials[MiningMaterial.Ore]);
        Assert.Equal(1, profile.Materials[MiningMaterial.Flint]);
    }

    [Fact]
    public void Add_PrimaryAndMultipleByproductsKeepTotalAtPrimaryCount()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Salt));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Emerald));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.SourceCrystal));

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.Equal(1, profile.Materials[MiningMaterial.Ore]);
        Assert.Equal(1, profile.Materials[MiningMaterial.Salt]);
        Assert.Equal(1, profile.Materials[MiningMaterial.Emerald]);
        Assert.Equal(1, profile.Materials[MiningMaterial.SourceCrystal]);
    }

    [Fact]
    public void Add_StoneShardsAndFlintKeepsTotalAtOne()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.StoneShards));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.Equal(1, profile.Materials[MiningMaterial.StoneShards]);
        Assert.Equal(1, profile.Materials[MiningMaterial.Flint]);
    }

    [Theory]
    [InlineData(MiningMaterial.SourceCrystal)]
    [InlineData(MiningMaterial.Ruby)]
    public void Add_ByproductAloneHasOwnCountAndZeroTotal(MiningMaterial material)
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(material));

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(0, profile.TotalCount);
        Assert.Equal(1, profile.Materials[material]);
    }

    [Fact]
    public void Add_SourceCrystalLogAliasesAggregateIntoOneByproductCounter()
    {
        var parser = new MiningEventParser();
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Parse(parser, "You mine some pink crystals."));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Parse(parser, "You get a source crystal."));

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        var material = Assert.Single(profile.Materials);
        Assert.Equal(MiningMaterial.SourceCrystal, material.Key);
        Assert.Equal(2, material.Value);
        Assert.Equal(0, profile.TotalCount);
    }

    [Fact]
    public void ResetMaterials_SourceCrystalRemovesCounterSharedByBothLogAliases()
    {
        var parser = new MiningEventParser();
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Parse(parser, "You mine some pink crystals."));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Parse(parser, "You get a source crystal."));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));

        counter.ResetMaterials(EventTransportTestProfileIds.Alice, [MiningMaterial.SourceCrystal]);

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        var material = Assert.Single(profile.Materials);
        Assert.Equal(MiningMaterial.Flint, material.Key);
        Assert.Equal(1, material.Value);
        Assert.Equal(0, profile.TotalCount);
    }

    [Fact]
    public void DifferentMaterialsAreIndependent()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.StoneShards));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.StoneShards));

        var materials = counter.GetSnapshot()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, materials[MiningMaterial.Ore]);
        Assert.Equal(2, materials[MiningMaterial.StoneShards]);
    }

    [Fact]
    public void DifferentProfilesAreIndependent()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Ore));

        var snapshot = counter.GetSnapshot();
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
        Assert.Equal(2, snapshot[EventTransportTestProfileIds.Bob][MiningMaterial.Ore]);
    }

    [Fact]
    public void SnapshotContainsAllProfilesAndMaterials()
    {
        var counter = new MiningSessionCounter();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));

        var snapshot = counter.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, snapshot[EventTransportTestProfileIds.Alice].Count);
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Flint]);
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Bob][MiningMaterial.Diamond]);
    }

    [Fact]
    public void SnapshotIsIndependentFromLaterAdd()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var snapshot = counter.GetSnapshot();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
        Assert.Equal(2, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void SnapshotIsIndependentFromReset()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var snapshot = counter.GetSnapshot();

        counter.Reset();

        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
        Assert.Empty(counter.GetSnapshot());
    }

    [Fact]
    public void ResetClearsWholeSession()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));

        counter.Reset();

        Assert.Empty(counter.GetSnapshot());
        Assert.Empty(counter.GetProfileSnapshots());
    }

    [Fact]
    public void ResetProfileMaterialCountersClearsOnlyMaterialsAndPreservesTotal()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));

        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.Empty(profile.Materials);
    }

    [Fact]
    public void ResetProfileMaterialCountersClearsSourceCrystalAndPreservesPrimaryTotal()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.SourceCrystal));

        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.Empty(profile.Materials);
    }

    [Fact]
    public void AddAfterProfileMaterialCountersResetContinuesTotalFromPreviousValue()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(2, profile.TotalCount);
        Assert.Equal(1, profile.Materials[MiningMaterial.Ore]);
        Assert.Single(profile.Materials);
    }

    [Fact]
    public void ResetProfileMaterialCountersDoesNotAffectOtherProfiles()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));

        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        var profiles = counter.GetProfileSnapshots();
        Assert.Empty(profiles[EventTransportTestProfileIds.Alice].Materials);
        Assert.Equal(1, profiles[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(0, profiles[EventTransportTestProfileIds.Bob].TotalCount);
        Assert.Equal(1, profiles[EventTransportTestProfileIds.Bob].Materials[MiningMaterial.Diamond]);
    }

    [Fact]
    public void ResetMaterialsRemovesOnlySelectedPrimaryAndByproductAndPreservesTotal()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ruby));

        counter.ResetMaterials(EventTransportTestProfileIds.Alice, [MiningMaterial.Ore, MiningMaterial.Flint]);

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.DoesNotContain(MiningMaterial.Ore, profile.Materials.Keys);
        Assert.DoesNotContain(MiningMaterial.Flint, profile.Materials.Keys);
        Assert.Equal(1, profile.Materials[MiningMaterial.Ruby]);
    }

    [Fact]
    public void ResetMaterialsDoesNotAffectOtherProfiles()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Flint));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", Event(MiningMaterial.Diamond));

        counter.ResetMaterials(EventTransportTestProfileIds.Alice, [MiningMaterial.Flint]);

        var profiles = counter.GetProfileSnapshots();
        Assert.Equal(1, profiles[EventTransportTestProfileIds.Alice].TotalCount);
        Assert.Equal(1, profiles[EventTransportTestProfileIds.Alice].Materials[MiningMaterial.Ore]);
        Assert.DoesNotContain(MiningMaterial.Flint, profiles[EventTransportTestProfileIds.Alice].Materials.Keys);
        Assert.Equal(1, profiles[EventTransportTestProfileIds.Bob].Materials[MiningMaterial.Diamond]);
    }

    [Fact]
    public void ResetMaterialsNoOpsDoNotRaiseCountsChanged()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var changes = 0;
        counter.CountsChanged += (_, _) => changes++;

        counter.ResetMaterials(PlayerProfileId.CreateNew(), [MiningMaterial.Ore]);
        counter.ResetMaterials(EventTransportTestProfileIds.Alice, []);
        counter.ResetMaterials(EventTransportTestProfileIds.Alice, [MiningMaterial.Ruby]);

        Assert.Equal(0, changes);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials[MiningMaterial.Ore]);
    }

    [Fact]
    public void ResetMaterialsDuplicatesRaiseOneChangeOnlyWhenMaterialExists()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var changes = 0;
        counter.CountsChanged += (_, eventArgs) =>
        {
            Assert.Equal(EventTransportTestProfileIds.Alice, eventArgs.ProfileId);
            changes++;
        };

        counter.ResetMaterials(EventTransportTestProfileIds.Alice, [MiningMaterial.Ore, MiningMaterial.Ore]);

        Assert.Equal(1, changes);
        Assert.DoesNotContain(MiningMaterial.Ore, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials.Keys);
    }

    [Fact]
    public void ResetProfileMaterialCountersRaisesOneChangeOnlyWhenMaterialsExist()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        var changes = 0;
        counter.CountsChanged += (_, eventArgs) =>
        {
            Assert.Equal(EventTransportTestProfileIds.Alice, eventArgs.ProfileId);
            changes++;
        };

        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);
        counter.ResetProfileMaterialCounters(EventTransportTestProfileIds.Alice);

        Assert.Equal(1, changes);
        Assert.Empty(counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public void AddAfterResetStartsFreshSession()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        counter.Reset();

        counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));

        Assert.Equal(1, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void NullProfileNameThrows()
    {
        var counter = new MiningSessionCounter();

        Assert.Throws<ArgumentNullException>(() => counter.Add(EventTransportTestProfileIds.Alice, null!, Event(MiningMaterial.Ore)));
    }

    [Fact]
    public void EmptyProfileNameThrows()
    {
        var counter = new MiningSessionCounter();

        Assert.Throws<ArgumentException>(() => counter.Add(EventTransportTestProfileIds.Alice, string.Empty, Event(MiningMaterial.Ore)));
    }

    [Fact]
    public void WhitespaceProfileNameThrows()
    {
        var counter = new MiningSessionCounter();

        Assert.Throws<ArgumentException>(() => counter.Add(EventTransportTestProfileIds.Alice, "  \t", Event(MiningMaterial.Ore)));
    }

    [Fact]
    public void NullMiningEventThrows()
    {
        var counter = new MiningSessionCounter();

        Assert.Throws<ArgumentNullException>(() => counter.Add(EventTransportTestProfileIds.Alice, "Alice", null!));
    }

    [Fact]
    public async Task ConcurrentAddsAreThreadSafe()
    {
        const int addCount = 10_000;
        var counter = new MiningSessionCounter();
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, addCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(addCount, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public async Task ConcurrentAddsForMultipleProfilesAndMaterialsAreCorrect()
    {
        const int addsPerCombination = 2_000;
        var combinations = new[]
        {
            (ProfileId: EventTransportTestProfileIds.Alice, ProfileName: "Alice", Material: MiningMaterial.Ore),
            (ProfileId: EventTransportTestProfileIds.Alice, ProfileName: "Alice", Material: MiningMaterial.Flint),
            (ProfileId: EventTransportTestProfileIds.Bob, ProfileName: "Bob", Material: MiningMaterial.Ore),
            (ProfileId: EventTransportTestProfileIds.Bob, ProfileName: "Bob", Material: MiningMaterial.Diamond)
        };
        var counter = new MiningSessionCounter();
        using var start = new ManualResetEventSlim(false);

        var tasks = combinations.Select(combination => Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < addsPerCombination; index++)
                counter.Add(combination.ProfileId, combination.ProfileName, Event(combination.Material));
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        var snapshot = counter.GetSnapshot();
        foreach (var combination in combinations)
            Assert.Equal(addsPerCombination, snapshot[combination.ProfileId][combination.Material]);
    }

    [Fact]
    public async Task ConcurrentSnapshotAndAddDoesNotThrow()
    {
        const int addCount = 5_000;
        var counter = new MiningSessionCounter();
        using var start = new ManualResetEventSlim(false);

        var addTask = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < addCount; index++)
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        });
        var snapshotTask = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < addCount; index++)
                _ = counter.GetSnapshot();
        });

        start.Set();
        await Task.WhenAll(addTask, snapshotTask);

        Assert.Equal(addCount, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public async Task ConcurrentResetAndAddDoesNotThrow()
    {
        const int operationCount = 2_000;
        var counter = new MiningSessionCounter();
        using var start = new ManualResetEventSlim(false);

        var addTask = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < operationCount; index++)
                counter.Add(EventTransportTestProfileIds.Alice, "Alice", Event(MiningMaterial.Ore));
        });
        var resetTask = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < operationCount; index++)
                counter.Reset();
        });

        start.Set();
        await Task.WhenAll(addTask, resetTask);

        _ = counter.GetSnapshot();
    }

    private static MiningEvent Event(MiningMaterial material) => new(material);

    [Fact]
    public void SameNameProfiles_KeepMiningAndInspirationStateIndependentAcrossReset()
    {
        var first = PlayerProfileId.CreateNew(); var second = PlayerProfileId.CreateNew(); const string name = "Twin";
        var counter = new MiningSessionCounter(); var inspirations = new MiningInspirationCounter(); var tracker = new MiningInspirationTracker(inspirations);
        counter.Add(first, name, Event(MiningMaterial.Ore)); counter.Add(second, name, Event(MiningMaterial.Shards));
        tracker.Process(new EventLogLine(first, name, "event.txt", "You start to mine forwards."), null);
        tracker.Process(new EventLogLine(first, name, "event.txt", "You have a moment of inspiration..."), null);
        tracker.Process(new EventLogLine(second, name, "event.txt", "You start to mine forwards."), null);
        tracker.Process(new EventLogLine(second, name, "event.txt", "You have a moment of inspiration..."), null);
        tracker.ResetProfile(first);
        tracker.Process(new EventLogLine(second, name, "event.txt", "You mine some shards."), Event(MiningMaterial.Shards));
        Assert.Equal(1, counter.GetProfileSnapshots()[first].TotalCount); Assert.Equal(1, counter.GetProfileSnapshots()[second].TotalCount);
        Assert.False(inspirations.GetProfileSnapshots().ContainsKey(first)); Assert.Equal(1, inspirations.GetProfileSnapshots()[second].TotalCount);
    }

    private static MiningEvent Parse(MiningEventParser parser, string line) =>
        Assert.IsType<MiningEvent>(parser.Parse(line));
}
