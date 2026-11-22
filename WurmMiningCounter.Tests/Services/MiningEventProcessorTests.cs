using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningEventProcessorTests
{
    [Fact]
    public void RecognizedMiningLineReturnsTrueAndIncrementsCounter()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        var processed = processor.Process(Line("Alice", "You mine some ore."));

        Assert.True(processed);
        Assert.Equal(1, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void TimestampedMiningLineIsProcessed()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        var processed = processor.Process(Line("Alice", "[06:22:08] You mine some stone shards."));

        Assert.True(processed);
        Assert.Equal(1, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.StoneShards]);
    }

    [Fact]
    public void UnrecognizedLineReturnsFalseAndDoesNotChangeCounter()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        var processed = processor.Process(Line("Alice", "You start to mine the ceiling."));

        Assert.False(processed);
        Assert.Empty(counter.GetSnapshot());
    }

    [Fact]
    public void DifferentMaterialsAreCountedCorrectly()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        processor.Process(Line("Alice", "You mine some ore."));
        processor.Process(Line("Alice", "You mine some rock salt."));
        processor.Process(Line("Alice", "You find a ruby!"));

        var materials = counter.GetSnapshot()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, materials[MiningMaterial.Ore]);
        Assert.Equal(1, materials[MiningMaterial.RockSalt]);
        Assert.Equal(1, materials[MiningMaterial.Ruby]);
    }

    [Fact]
    public void DifferentProfilesAreIndependent()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        processor.Process(Line("Alice", "You mine some ore."));
        processor.Process(Line("Bob", "You mine some ore."));

        var snapshot = counter.GetSnapshot();
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
        Assert.Equal(1, snapshot[EventTransportTestProfileIds.Bob][MiningMaterial.Ore]);
    }

    [Fact]
    public void MultipleLinesIncrementSameProfileAndMaterial()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        processor.Process(Line("Alice", "You mine some ore."));
        processor.Process(Line("Alice", "You mine some ore."));
        processor.Process(Line("Alice", "You mine some ore."));

        Assert.Equal(3, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    [Fact]
    public void OtherPlayerMiningIsIgnored()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        var processed = processor.Process(Line("Alice", "ProfileB mines some stone shards."));

        Assert.False(processed);
        Assert.Empty(counter.GetSnapshot());
    }

    [Fact]
    public void BonusFindIsCounted()
    {
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);

        var processed = processor.Process(Line("Alice", "You find a ruby!"));

        Assert.True(processed);
        Assert.Equal(1, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ruby]);
    }

    [Fact]
    public void NullLineThrowsArgumentNullException()
    {
        var processor = CreateProcessor(new MiningSessionCounter());

        Assert.Throws<ArgumentNullException>(() => processor.Process(null!));
    }

    [Fact]
    public void NullParserThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MiningEventProcessor(null!, new MiningSessionCounter()));
    }

    [Fact]
    public void NullCounterThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MiningEventProcessor(new MiningEventParser(), null!));
    }

    [Fact]
    public void InvalidProfileNameFromRecognizedEventIsNotHidden()
    {
        var processor = CreateProcessor(new MiningSessionCounter());

        Assert.Throws<ArgumentException>(() => processor.Process(Line(string.Empty, "You mine some ore.")));
        Assert.Throws<ArgumentException>(() => processor.Process(Line(" \t", "You mine some ore.")));
    }

    [Fact]
    public async Task ConcurrentProcessingIsSafe()
    {
        const int lineCount = 10_000;
        var counter = new MiningSessionCounter();
        var processor = CreateProcessor(counter);
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, lineCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                Assert.True(processor.Process(Line("Alice", "You mine some ore.")));
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(lineCount, counter.GetSnapshot()[EventTransportTestProfileIds.Alice][MiningMaterial.Ore]);
    }

    private static MiningEventProcessor CreateProcessor(MiningSessionCounter counter) =>
        new(new MiningEventParser(), counter);

    private static EventLogLine Line(string profileName, string text) =>
        new(EventTransportTestProfileIds.For(profileName), profileName, "C:\\logs\\Event.txt", text);
}
