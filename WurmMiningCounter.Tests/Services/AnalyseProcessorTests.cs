using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class AnalyseProcessorTests
{
    [Theory]
    [InlineData("You start to analyse the shard.", "[01:13:10] You finish analysing the shard.", AnalyseInput.Shard)]
    [InlineData("You start to analyse the ore.", "[23:59:58] You finish analysing the ore.", AnalyseInput.Ore)]
    public void FinishTimeBelongsToCompletedResult(string start, string finish, AnalyseInput input)
    {
        var processor = new AnalyseProcessor();
        foreach (var line in new[] { start, NoTrace, finish }) Line(processor, Alice, line);
        var result = Assert.Single(processor.GetSnapshots()).Result;
        Assert.Equal(input, result.Input);
        Assert.Equal(finish.Substring(1, 8), result.FinishTime);
    }

    [Fact]
    public void FinishTimeChangesOnlyWithCompletedResultAndReset()
    {
        var processor = new AnalyseProcessor();
        foreach (var text in new[] { Start, NoTrace, "[01:13:10] You finish analysing the shard." }) Line(processor, Alice, text);
        var first = Assert.Single(processor.GetSnapshots());
        foreach (var text in new[] { Start, Trace, "[02:00:00] You finish analysing the ore." }) Line(processor, Alice, text);
        Assert.Same(first, Assert.Single(processor.GetSnapshots()));
        foreach (var text in new[] { Start, NoTrace, "[04:05:06] You finish analysing the shard." }) Line(processor, Alice, text);
        Assert.Equal("04:05:06", Assert.Single(processor.GetSnapshots()).Result.FinishTime);
        Assert.Equal("01:13:10", first.Result.FinishTime);
        foreach (var text in new[] { Start, NoTrace, Finish }) Line(processor, Alice, text);
        Assert.Null(Assert.Single(processor.GetSnapshots()).Result.FinishTime);
        processor.ResetProfile(Alice);
        Assert.Empty(processor.GetSnapshots());
    }

    [Fact]
    public void LiveFaintNoTraceSequencePreservesAllObservations()
    {
        string[] lines =
        [
            "[21:28:08] You start to analyse the shard.",
            "[21:28:08] Lots of different sized stone shards.",
            "[21:28:10] You cannot see anything unusual.",
            "[21:28:10] You take a closer look.",
            "[21:28:13] You spot a slight trace of good quality lead (west of south).",
            "[21:28:13] You study it a bit more.",
            "[21:28:16] You cannot see any unusual faint traces of anything.",
            "[21:28:16] You study it real hard.",
            "[21:28:19] You notice a minuscule trace of acceptable quality sandstone (east of north).",
            "[21:28:19] You peer at it.",
            "[21:28:22] You notice a vague trace of something, but cannot quite make it out (south of west).",
            "[21:28:22] You spot a vague trace of flint (west of north).",
            "[21:28:22] You notice a vague trace of salt (east of south).",
            "[21:28:22] You finish analysing the shard."
        ];
        var processor = new AnalyseProcessor();
        var changes = new List<AnalyseChangedEventArgs>();
        processor.AnalyseChanged += (_, args) => changes.Add(args);
        foreach (var line in lines[..^1])
        {
            Line(processor, Alice, line);
            Assert.Empty(changes);
        }
        Line(processor, Alice, lines[^1]);
        var snapshot = Assert.IsType<AnalyseSnapshot>(Assert.Single(changes).Snapshot);
        Assert.Same(snapshot, Assert.Single(processor.GetSnapshots()));
        Assert.Equal(AnalyseInput.Shard, snapshot.Result.Input);
        AnalyseEntry[] expected =
        [
            new(null, new AnalyseNoTrace(null, lines[2]), null, lines[2]),
            new(null, null, AnalyseStrength.Trace, lines[3]),
            new(new AnalyseTrace(AnalyseStrength.Slight, "lead", "good", "west of south", lines[4]), null, null, lines[4]),
            new(null, null, AnalyseStrength.Slight, lines[5]),
            new(null, new AnalyseNoTrace(AnalyseStrength.Faint, lines[6]), null, lines[6]),
            new(null, null, AnalyseStrength.Faint, lines[7]),
            new(new AnalyseTrace(AnalyseStrength.Minuscule, "sandstone", "acceptable", "east of north", lines[8]), null, null, lines[8]),
            new(null, null, AnalyseStrength.Minuscule, lines[9]),
            new(new AnalyseTrace(AnalyseStrength.Vague, null, null, "south of west", lines[10]), null, null, lines[10]),
            new(new AnalyseTrace(AnalyseStrength.Vague, "flint", null, "west of north", lines[11]), null, null, lines[11]),
            new(new AnalyseTrace(AnalyseStrength.Vague, "salt", null, "east of south", lines[12]), null, null, lines[12])
        ];
        Assert.Equal(expected, snapshot.Result.Entries);
    }

    [Fact]
    public void LiveA1FullSequencePublishesAllRecognizedObservationsInOrder()
    {
        string[] lines =
        [
            "[21:13:11] You get some stone shards.",
            "[21:13:14] You start to analyse the shard.",
            "[21:13:14] Lots of different sized stone shards.",
            "[21:13:16] You do not notice any unusual traces.",
            "[21:13:16] You take a closer look.",
            "[21:13:19] You spot a slight trace of good quality lead (west of south).",
            "[21:13:19] You study it a bit more.",
            "[21:13:22] You cannot see anything unusual.",
            "[21:13:22] You study it real hard.",
            "[21:13:25] You see a minuscule trace of acceptable quality sandstone (east of north).",
            "[21:13:25] You peer at it.",
            "[21:13:28] You spot a vague trace of flint (west of north).",
            "[21:13:28] You spot a vague trace of something, but cannot quite make it out (south of west).",
            "[21:13:28] You notice a vague trace of salt (east of south).",
            "[21:13:28] You finish analysing the shard."
        ];
        var processor = new AnalyseProcessor();
        var changes = new List<AnalyseChangedEventArgs>();
        processor.AnalyseChanged += (_, args) => changes.Add(args);
        foreach (var line in lines[..^1])
        {
            Line(processor, Alice, line);
            Assert.Empty(changes);
        }
        Line(processor, Alice, lines[^1]);

        var snapshot = Assert.IsType<AnalyseSnapshot>(Assert.Single(changes).Snapshot);
        Assert.Same(snapshot, Assert.Single(processor.GetSnapshots()));
        Assert.Equal(AnalyseInput.Shard, snapshot.Result.Input);
        AnalyseEntry[] expected =
        [
            new(null, new AnalyseNoTrace(AnalyseStrength.Trace, lines[3]), null, lines[3]),
            new(null, null, AnalyseStrength.Trace, lines[4]),
            new(new AnalyseTrace(AnalyseStrength.Slight, "lead", "good", "west of south", lines[5]), null, null, lines[5]),
            new(null, null, AnalyseStrength.Slight, lines[6]),
            new(null, new AnalyseNoTrace(null, lines[7]), null, lines[7]),
            new(null, null, AnalyseStrength.Faint, lines[8]),
            new(new AnalyseTrace(AnalyseStrength.Minuscule, "sandstone", "acceptable", "east of north", lines[9]), null, null, lines[9]),
            new(null, null, AnalyseStrength.Minuscule, lines[10]),
            new(new AnalyseTrace(AnalyseStrength.Vague, "flint", null, "west of north", lines[11]), null, null, lines[11]),
            new(new AnalyseTrace(AnalyseStrength.Vague, null, null, "south of west", lines[12]), null, null, lines[12]),
            new(new AnalyseTrace(AnalyseStrength.Vague, "salt", null, "east of south", lines[13]), null, null, lines[13])
        ];
        Assert.Equal(expected, snapshot.Result.Entries);
    }

    [Fact]
    public void LiveA1TraceIsPreservedBetweenFlintAndSaltInCompletedSequence()
    {
        const string flint = "You see a vague trace of flint (northwest).";
        const string unknown = "[21:13:28] You spot a vague trace of something, but cannot quite make it out (south of west).";
        const string salt = "You notice a faint trace of salt (north).";
        var assembler = new AnalyseSequenceAssembler();
        foreach (var line in new[] { Start, flint, unknown, salt })
            Assert.Null(assembler.Process(line));
        var result = Assert.IsType<AnalyseResult>(assembler.Process(Finish));
        Assert.Equal(new[] { flint, unknown, salt }, result.Entries.Select(entry => entry.RawText));
        Assert.Equal(new AnalyseTrace(AnalyseStrength.Vague, null, null, "south of west", unknown), result.Entries[1].Trace);
    }

    private const string Start = "You start to analyse the shard.";
    private const string Finish = "You finish analysing the shard.";
    private const string Trace = "You notice a trace of utmost quality slate (east).";
    private const string NoTrace = "You cannot see anything unusual.";
    private const string Marker = "You take a closer look.";
    private static readonly PlayerProfileId Alice = EventTransportTestProfileIds.Alice;
    private static readonly PlayerProfileId Bob = EventTransportTestProfileIds.Bob;

    [Theory]
    [InlineData(AnalyseInput.Shard, Start, Finish)]
    [InlineData(AnalyseInput.Ore, "You start to analyse the ore.", "You finish analysing the ore.")]
    public void Assembler_PreservesOrderedRepeatedObservationsAndMarkers(AnalyseInput input, string start, string finish)
    {
        var assembler = new AnalyseSequenceAssembler();
        foreach (var line in new[] { start, Trace, Trace, Marker, NoTrace, "After you finish analysing you will start mining." })
            Assert.Null(assembler.Process(line));
        var result = Assert.IsType<AnalyseResult>(assembler.Process(finish));
        Assert.Equal(input, result.Input);
        Assert.Equal(new[] { Trace, Trace, Marker, NoTrace }, result.Entries.Select(entry => entry.RawText));
        Assert.Equal(AnalyseStrength.Trace, result.Entries[2].StageMarker);
        Assert.NotNull(result.Entries[3].NoTrace);
        Assert.Null(assembler.Process(finish));
    }

    [Fact]
    public void Assembler_RequiresObservationAndMatchingFinishAndNewStartDiscardsOld()
    {
        var assembler = new AnalyseSequenceAssembler();
        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Marker));
        Assert.Null(assembler.Process(Finish));
        Assert.Null(assembler.Process(Start));
        Assert.Null(assembler.Process(Trace));
        Assert.Null(assembler.Process("You finish analysing the ore."));
        Assert.Null(assembler.Process("You start to analyse the ore."));
        Assert.Null(assembler.Process(Finish));
        Assert.Null(assembler.Process(NoTrace));
        var result = Assert.IsType<AnalyseResult>(assembler.Process("You finish analysing the ore."));
        Assert.Equal(AnalyseInput.Ore, result.Input);
        Assert.Equal(NoTrace, Assert.Single(result.Entries).RawText);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("reset")]
    [InlineData("invalidate")]
    [InlineData("reconnect")]
    public void BoundariesDiscardIncompleteStateAndClearSnapshotForOnlyOneProfile(string boundary)
    {
        var processor = new AnalyseProcessor();
        var changes = new List<AnalyseChangedEventArgs>();
        processor.AnalyseChanged += (_, e) => changes.Add(e);
        foreach (var id in new[] { Alice, Bob })
            foreach (var text in new[] { Start, NoTrace, Finish, Start, Trace }) Line(processor, id, text);
        switch (boundary)
        {
            case "gap": processor.Process(new StreamContinuityLost(Alice, "Alice", 1, StreamGapReason.Replaced, null)); break;
            case "reset": processor.Process(new ResetProfileBarrier(Alice, "Alice", 1)); break;
            case "reconnect": Line(processor, Alice, "Logging started 2026-01-01"); break;
            default: processor.Invalidate(Alice, "Alice"); break;
        }
        Line(processor, Alice, Finish);
        Line(processor, Bob, Finish);
        Assert.Equal(Bob, Assert.Single(processor.GetSnapshots()).ProfileId);
        Assert.Single(changes.Where(change => change.ProfileId == Alice && change.Snapshot is null));
        Assert.Equal(3, changes.Count(change => change.Snapshot is not null));
    }

    [Fact]
    public async Task ContextReleaseDrainsAndDiscardsPartialButRetainsCompletedSnapshot()
    {
        var processor = new AnalyseProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(processor);
        context.ProfileLaneDrained = processor.ReleaseProfile;
        context.AcceptOutput(Batch(Start, NoTrace, Finish, Start, Trace));
        await context.ReleaseProfileAsync(Alice).WaitAsync(TimeSpan.FromSeconds(5));
        var previous = Assert.Single(processor.GetSnapshots());
        context.AcceptOutput(Batch(Finish));
        await context.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(previous, Assert.Single(processor.GetSnapshots()));
        processor.ResetProfile(Alice);
        Assert.Empty(processor.GetSnapshots());
    }

    [Fact]
    public async Task ResetSessionClearsResultRetainedAfterProfileRelease()
    {
        var analyse = new AnalyseProcessor();
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var vein = new VeinContextProcessor();
        var nearby = new NearbyScanProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(analyse);
        context.ProfileLaneDrained = analyse.ReleaseProfile;
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        dispatcher.Handoff(Batch(Start, NoTrace, Finish));
        await dispatcher.ReleaseProfileAsync(Alice).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(analyse.GetSnapshots());
        var profiles = await App.ResetEventSessionAsync(dispatcher, counter, inspirations, tracker, vein, nearby, analyse);
        Assert.Contains(Alice, profiles);
        Assert.Empty(analyse.GetSnapshots());
        await dispatcher.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PublishedResultIsUnchangedByLaterSequences()
    {
        var processor = new AnalyseProcessor();
        foreach (var text in new[] { Start, Trace, Finish }) Line(processor, Alice, text);
        var first = Assert.Single(processor.GetSnapshots());
        foreach (var text in new[] { Start, NoTrace, Finish }) Line(processor, Alice, text);
        Assert.Equal(Trace, Assert.Single(first.Result.Entries).RawText);
        Assert.Equal(NoTrace, Assert.Single(Assert.Single(processor.GetSnapshots()).Result.Entries).RawText);
    }

    [Fact]
    public async Task ContextAndMiningLanesRemainIndependent()
    {
        var analyse = new AnalyseProcessor();
        var vein = new VeinContextProcessor();
        var nearby = new NearbyScanProcessor();
        var counter = new MiningSessionCounter();
        var mining = new MiningEventProcessor(new MiningEventParser(), counter);
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(analyse);
        context.RegisterConsumer(vein);
        context.RegisterConsumer(nearby);
        var dispatcher = new FastEventDispatcher(new MiningEventLane(mining), context);
        dispatcher.Handoff(Batch(Start, Trace, NoTrace, Finish));
        await dispatcher.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(analyse.GetSnapshots());
        Assert.Empty(counter.GetProfileSnapshots());
        Assert.Empty(vein.GetActiveContextSnapshots());
        Assert.Empty(nearby.GetSnapshots());
    }

    private static void Line(AnalyseProcessor processor, PlayerProfileId id, string text) =>
        processor.Process(new OrderedEventLine(id, id == Alice ? "Alice" : "Bob", 1,
            new EventLogLine(id, id == Alice ? "Alice" : "Bob", "event.txt", text)));

    private static EventReadBatch Batch(params string[] lines) => new(Alice, "Alice", "event.txt", 1, lines);
}
