using System.Collections.Concurrent;
using System.Text;
using WurmMiningCounter;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventPipelineTests
{
    [Fact]
    public async Task SyntheticAnalyseAndMiningInterleavingKeepsActiveVeinAndCounts()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var analyse = new AnalyseProcessor();
        var vein = new VeinContextProcessor();
        var veinSnapshots = new ConcurrentQueue<VeinContextSnapshot>();
        vein.ActiveContextChanged += (_, args) => { if (args.Snapshot is { } snapshot) veinSnapshots.Enqueue(snapshot); };
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        context.RegisterConsumer(analyse);
        var counter = new MiningSessionCounter();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(new MiningEventProcessor(new MiningEventParser(), counter)), context);
        await using var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);
        pipeline.Start();
        // Synthetic consumer-isolation scenario, not evidence of actual in-game action ordering.
        const string trace = "You notice a trace of utmost quality slate (east).";
        const string noTrace = "You cannot see anything unusual.";
        File.AppendAllText(path, "You start to gather fragments of the rock.\nYou would mine iron ore here.\nIt has a max quality of 12.5.\nYou will be able to mine here 3 more times.\n" +
            "You start to analyse the shard.\n" + trace + "\nYou start to mine forwards.\nYou mine some ore.\n" +
            noTrace + "\nYou finish analysing the shard.\nYou start to mine forwards.\nYou mine some ore.\n");
        await Step(clock, cycles, 1);
        // Completion drains both independent FIFO lanes; it imposes no order between them.
        await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));

        var result = Assert.Single(analyse.GetSnapshots()).Result;
        Assert.Equal(AnalyseInput.Shard, result.Input);
        Assert.Equal(new[] { trace, noTrace }, result.Entries.Select(entry => entry.RawText));
        var initial = veinSnapshots.First(snapshot => snapshot.Remaining is RemainingMeasurement.Exact { Value: 3 });
        var active = Assert.Single(vein.GetActiveContextSnapshots());
        Assert.Equal(initial.ProfileId, active.ProfileId);
        Assert.Equal(VeinMaterial.Iron, active.Material);
        Assert.Equal(initial.Quality, active.Quality);
        Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(active.Remaining).Value);
        Assert.Equal(2, Assert.Single(counter.GetProfileSnapshots()).Value.TotalCount);
    }

    [Fact]
    public async Task AnalyseFromEventFilePreservesMiningAndProspectingState()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var analyse = new AnalyseProcessor();
        var changes = new ConcurrentQueue<AnalyseSnapshot>();
        analyse.AnalyseChanged += (_, args) => { if (args.Snapshot is { } snapshot) changes.Enqueue(snapshot); };
        var vein = new VeinContextProcessor();
        var nearby = new NearbyScanProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        context.RegisterConsumer(nearby);
        context.RegisterConsumer(analyse);
        context.ProfileLaneDrained = analyse.ReleaseProfile;
        var counter = new MiningSessionCounter();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(new MiningEventProcessor(new MiningEventParser(), counter)), context);
        await using var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);
        pipeline.Start();
        File.AppendAllText(path, "You start to gather fragments of the rock.\nYou would mine iron ore here.\nIt has a max quality of 12.5.\nYou will be able to mine here 3 more times.\n" +
            "You start to analyse the shard.\nYou notice a trace of utmost quality slate (east).\nYou notice a trace of utmost quality slate (east).\nYou take a closer look.\nYou cannot see anything unusual.\nYou finish analysing the shard.\n" +
            "You start to analyse the ore.\nYou cannot see anything unusual.\nYou finish analysing the ore.\n" +
            "You start to analyse the shard.\nYou notice a trace of utmost quality slate (east).\n" +
            "You start to mine forwards.\nYou mine some ore.\n");
        await Step(clock, cycles, 1);
        await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));

        var results = changes.ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(AnalyseInput.Shard, results[0].Result.Input);
        Assert.Equal(4, results[0].Result.Entries.Length);
        Assert.Equal(results[0].Result.Entries[0], results[0].Result.Entries[1]);
        Assert.Equal(AnalyseInput.Ore, results[1].Result.Input);
        Assert.NotNull(Assert.Single(results[1].Result.Entries).NoTrace);
        Assert.Equal(results[1], Assert.Single(analyse.GetSnapshots()));
        var active = Assert.Single(vein.GetActiveContextSnapshots());
        Assert.Equal(VeinMaterial.Iron, active.Material);
        Assert.Equal(2, Assert.IsType<RemainingMeasurement.Exact>(active.Remaining).Value);
        Assert.Equal(1, Assert.Single(counter.GetProfileSnapshots()).Value.TotalCount);
        Assert.Empty(nearby.GetSnapshots());
    }

    [Theory]
    [InlineData("", "You would mine silver ore here.\nIt is of poor quality.\nOnly a few weeks mining remain here.\n")]
    [InlineData("You would mine silver ore here.\n", "It is of poor quality.\nOnly a few weeks mining remain here.\n")]
    [InlineData("You would mine silver ore here.\nIt is of poor quality.\n", "Only a few weeks mining remain here.\n")]
    public async Task DarkDungeonsPreservesInFlightProspectingThroughEventPipeline(string beforeFailure, string afterFailure)
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var changes = new ConcurrentQueue<VeinContextSnapshot?>();
        var uiActions = new ConcurrentQueue<Action>();
        var vein = new VeinContextProcessor();
        vein.ActiveContextChanged += (_, args) => changes.Enqueue(args.Snapshot);
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(vein);
        var counter = new MiningSessionCounter();
        var mining = new MiningEventProcessor(new MiningEventParser(), counter,
            new MiningInspirationTracker(new MiningInspirationCounter()));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(mining), coordinator);
        var viewModel = new MainViewModel(counter, veinContextProcessor: vein, postToUi: uiActions.Enqueue);
        var viewModelEvents = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.VeinContexts).CollectionChanged +=
            (_, args) => viewModelEvents.Add(args.Action);
        await using var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);
        pipeline.Start();
        File.AppendAllText(path, "You start to gather fragments of the rock.\nYou would mine iron ore here.\nIt has a max quality of 12.5.\nYou will be able to mine here 3 more times.\nYou start to gather fragments of the rock.\n" + beforeFailure + "You see dark dungeons.\n");
        await Step(clock, cycles, 1);
        File.AppendAllText(path, afterFailure);
        await Step(clock, cycles, 2);
        await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));
        while (uiActions.TryDequeue(out var action)) action();

        var observed = changes.ToArray();
        var clearIndex = Array.FindIndex(observed, snapshot => snapshot is null);
        Assert.True(clearIndex > 0);
        Assert.Equal(VeinMaterial.Silver, observed.Last()!.Material);
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, viewModelEvents);
        var context = Assert.Single(vein.GetActiveContextSnapshots());
        Assert.Equal(VeinMaterial.Silver, context.Material);
        Assert.Equal(ProspectingQualityGrade.Poor, Assert.IsType<QualityMeasurement.Grade>(context.Quality).Value);
        Assert.Equal(ProspectingRemainingBand.FewWeeks, Assert.IsType<RemainingMeasurement.EstimateBand>(context.Remaining).Value);
        Assert.Equal(VeinConfidence.Synchronized, context.Confidence);
        Assert.Equal(VeinMaterial.Silver, Assert.Single(viewModel.ProfilePanels).VeinContext!.Material);
        Assert.Empty(counter.GetProfileSnapshots());
    }

    [Theory]
    [InlineData("[10:38:11] You start to mine forwards.\n[10:38:28] You mine some ore.\n[10:38:30] A vein of pure silver emerges here.\n[10:38:35] You see dark dungeons.\n", true, 1)]
    [InlineData("[10:25:37] You start to gather fragments of the rock.\n[10:25:43] You would mine silver ore here.\n[10:25:43] It is of poor quality.\n[10:25:43] Only a few weeks mining remain here.\n[10:25:46] You see dark dungeons.\n", true, 0)]
    [InlineData("You start to gather fragments of the rock.\nYou would mine silver ore here.\nIt is of poor quality.\nYou see dark dungeons.\n", true, 0)]
    [InlineData("You start to gather fragments of the rock.\nYou would mine iron ore here.\nIt has a max quality of 12.5.\nYou will be able to mine here 2 more times.\nYou start to mine forwards.\nYou see dark dungeons.\nYou mine some ore.\n", false, 1)]
    [InlineData("A vein of pure silver emerges here.\nYou start to mine forwards.\nThe wall breaks!\nYou see dark dungeons.\n", false, 0)]
    public async Task DarkDungeonsIsAppliedThroughLiveEventPipeline(string input, bool clears, long oreCount)
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var snapshots = new ConcurrentQueue<VeinContextSnapshot?>();
        var uiActions = new ConcurrentQueue<Action>();
        var vein = new VeinContextProcessor();
        vein.ActiveContextChanged += (_, args) => snapshots.Enqueue(args.Snapshot);
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        var counter = new MiningSessionCounter();
        var mining = new MiningEventProcessor(new MiningEventParser(), counter,
            new MiningInspirationTracker(new MiningInspirationCounter()));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(mining), context);
        var viewModel = new MainViewModel(counter, veinContextProcessor: vein, postToUi: uiActions.Enqueue);
        var removedFromViewModel = false;
        ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.VeinContexts).CollectionChanged += (_, args) =>
            removedFromViewModel |= args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove;
        await using var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);
        pipeline.Start();
        File.AppendAllText(path, input);
        await Step(clock, cycles, 1);
        if (clears)
        {
            File.AppendAllText(path, "You start to gather fragments of the rock.\nYou would mine copper ore here.\nIt has a max quality of 12.5.\nYou will be able to mine here 3 more times.\n");
            await Step(clock, cycles, 2);
        }
        await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));
        while (uiActions.TryDequeue(out var action)) action();

        Assert.Equal(clears, snapshots.Any(snapshot => snapshot is null));
        Assert.Equal(clears, removedFromViewModel);
        var final = Assert.Single(vein.GetActiveContextSnapshots());
        Assert.Equal(clears ? VeinMaterial.Copper : oreCount == 1 ? VeinMaterial.Iron : VeinMaterial.Silver, final.Material);
        if (clears)
            Assert.Equal(3, Assert.IsType<RemainingMeasurement.Exact>(final.Remaining).Value);
        else if (oreCount == 1)
            Assert.Equal(1, Assert.IsType<RemainingMeasurement.Exact>(final.Remaining).Value);
        Assert.Equal(final.Material, Assert.Single(viewModel.ProfilePanels).VeinContext!.Material);
        Assert.Equal(oreCount, counter.GetProfileSnapshots().Values.Sum(snapshot => snapshot.TotalCount));
    }

    private static PlayerProfileId RuntimeId(FastEventDispatcher dispatcher) =>
        Assert.Single(dispatcher.GetKnownProfiles()).Id;

    [Fact]
    public async Task ReturningOwnerBeforeContinuityAdmissionResumesAtEofUnderItsOriginalId()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var profilePath = Path.Combine(files.PlayersPath, "Alice");
        var root = Path.GetDirectoryName(files.PlayersPath)!;
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var accept = false;
        var published = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = CreateWithHandoff(files, clock, output =>
        {
            if (!accept)
                return EventPipelineHandoffResult.NotStarted;
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, cycles);
        pipeline.Start();
        var before = Assert.Single(pipeline.SourceCheckpoints);
        Directory.Move(profilePath, Path.Combine(root, "retired-A"));
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "B history\n");
        await Step(clock, cycles, 1);
        Assert.Equal(before, Assert.Single(pipeline.SourceCheckpoints));
        Directory.Move(profilePath, Path.Combine(root, "retired-B"));
        Directory.Move(Path.Combine(root, "retired-A"), profilePath);
        File.AppendAllText(path, "detached A update\n");
        accept = true;
        await Step(clock, cycles, 2);
        await Step(clock, cycles, 3);
        await Step(clock, cycles, 4);
        var after = Assert.Single(pipeline.SourceCheckpoints);
        Assert.Equal(before.ProfileId, after.ProfileId);
        Assert.Equal(new FileInfo(path).Length, after.ReadOffset);
        Assert.IsType<EventContinuityLoss>(Assert.Single(published));
        File.AppendAllText(path, "live A update\n");
        await Step(clock, cycles, 5);
        var batch = Assert.Single(published.OfType<EventReadBatch>());
        Assert.Equal(before.ProfileId, batch.ProfileId);
        Assert.Equal(["live A update"], batch.Lines);
    }

    [Fact]
    public async Task ActualEventAndConsoleReadsHoldOwnershipGuardUntilTheReadCompletes()
    {
        using var files = new EventFiles();
        var eventPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var root = Path.GetDirectoryName(files.PlayersPath)!;
        var profilePath = Path.Combine(files.PlayersPath, "Alice");
        var movedPath = Path.Combine(root, "moved-profile");
        File.WriteAllText(Path.Combine(root, "console.Alice.log"), $"[10:00:00] Writing to {skillsPath}\n");
        var attemptReplacement = false;
        var guardedReads = 0;
        void TryReplaceDuringRead()
        {
            if (!attemptReplacement)
                return;
            Assert.Throws<IOException>(() => Directory.Move(profilePath, movedPath));
            guardedReads++;
        }
        using var monitor = new LiveSkillMonitor(files.PlayersPath, TimeSpan.FromDays(1),
            consoleStreamFactory: path =>
            {
                TryReplaceDuringRead();
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }, profileIdentityResolver: files.Resolver);
        monitor.Start();
        var id = Assert.Single(monitor.GetSnapshots()).ProfileId;
        var reader = new EventLogReader();
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = new EventPipeline(files.PlayersPath, output =>
        {
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, reader, clock, scan: files.Scan,
            read: (path, offset, length, fingerprint, pending) =>
            {
                TryReplaceDuringRead();
                return reader.Read(path, offset, length, fingerprint, pending);
            }, cycleCompleted: cycles.Signal, profileIdentityResolver: files.Resolver);
        pipeline.Start();
        attemptReplacement = true;
        File.AppendAllText(eventPath, "live\n");
        File.AppendAllText(skillsPath, MiningSkillLine(80.001m));
        monitor.Poll();
        await Step(clock, cycles, 1);
        Assert.Equal(2, guardedReads);
        Assert.Equal(id, Assert.Single(monitor.GetSnapshots()).ProfileId);
        Assert.Equal(0.001m, Assert.Single(Assert.Single(monitor.GetSnapshots()).Skills).SessionGain);
        Assert.Equal(id, Assert.Single(published.OfType<EventReadBatch>()).ProfileId);
        await pipeline.StopAsync();
        monitor.Dispose();
        Directory.Move(profilePath, movedPath);
    }

    [Fact]
    public void IdleSkillProfileDoesNotProbeOwnershipOrAccessRegistryOnEveryPoll()
    {
        var probes = 0;
        using var files = new EventFiles(_ => { probes++; return "volume:alice"; });
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        using var monitor = new LiveSkillMonitor(files.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: files.Resolver);
        monitor.Start();
        var initial = Assert.Single(monitor.GetSnapshots());
        var root = Path.GetDirectoryName(files.PlayersPath)!;
        using (new FileStream(Path.Combine(root, "identity-registry", "player-profile-identities.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var poll = 0; poll < 5; poll++)
                monitor.Poll();
            Assert.Equal(1, probes);
            Assert.Equal(initial.ProfileId, Assert.Single(monitor.GetSnapshots()).ProfileId);
            File.WriteAllText(Path.Combine(root, "console.Alice.log"), $"[10:00:00] Writing to {skillsPath}\n");
            monitor.Poll();
            Assert.Equal(2, probes); // verification belongs to the actual source open.
            Assert.True(Assert.Single(monitor.GetSnapshots()).IsHealthy);
        }
    }

    [Fact]
    public async Task SameNamedPhysicalProfilesKeepSeparateEventAndSkillOwners()
    {
        using var first = new EventFiles();
        using var second = new EventFiles();
        var firstEvent = first.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var secondEvent = second.Add(EventTransportTestProfileIds.Bob, "Alice", "_Event.2026-08.txt", "history\n");
        var firstSkills = first.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var secondSkills = second.Add(EventTransportTestProfileIds.Bob, "Alice", "_Skills.2026-08.txt", MiningSkillLine(90m));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(first.PlayersPath)!, "console.Alice.log"), $"[10:00:00] Writing to {firstSkills}\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(second.PlayersPath)!, "console.Alice.log"), $"[10:00:00] Writing to {secondSkills}\n");
        using var firstMonitor = new LiveSkillMonitor(first.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: first.Resolver);
        using var secondMonitor = new LiveSkillMonitor(second.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: first.Resolver);
        var firstClock = new ManualClock();
        var secondClock = new ManualClock();
        var firstCycles = new CycleSignal();
        var secondCycles = new CycleSignal();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        EventPipelineHandoffResult Handoff(EventPipelineOutput output)
        {
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }
        await using var firstPipeline = CreateWithHandoff(first, firstClock, Handoff, firstCycles);
        await using var secondPipeline = new EventPipeline(second.PlayersPath, Handoff, new EventLogReader(), secondClock,
            scan: second.Scan, cycleCompleted: secondCycles.Signal, profileIdentityResolver: first.Resolver);
        firstMonitor.Start();
        secondPipeline.Start();
        secondMonitor.Start();
        firstPipeline.Start();
        var firstId = Assert.Single(firstMonitor.GetSnapshots()).ProfileId;
        var secondId = Assert.Single(secondMonitor.GetSnapshots()).ProfileId;
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, Assert.Single(firstPipeline.SourceCheckpoints).ProfileId);
        Assert.Equal(secondId, Assert.Single(secondPipeline.SourceCheckpoints).ProfileId);

        File.AppendAllText(firstEvent, "first\n");
        File.AppendAllText(secondEvent, "second\n");
        File.AppendAllText(firstSkills, MiningSkillLine(80.001m));
        File.AppendAllText(secondSkills, MiningSkillLine(90.002m, 0.002m));
        firstMonitor.Poll();
        secondMonitor.Poll();
        await Step(firstClock, firstCycles, 1);
        await Step(secondClock, secondCycles, 1);
        Assert.Equal(["first"], Assert.Single(published.OfType<EventReadBatch>().Where(batch => batch.ProfileId == firstId)).Lines);
        Assert.Equal(["second"], Assert.Single(published.OfType<EventReadBatch>().Where(batch => batch.ProfileId == secondId)).Lines);
        Assert.Equal(0.001m, Assert.Single(Assert.Single(firstMonitor.GetSnapshots()).Skills).SessionGain);
        Assert.Equal(0.002m, Assert.Single(Assert.Single(secondMonitor.GetSnapshots()).Skills).SessionGain);
    }

    [Fact]
    public async Task MissingConfirmedEvidenceSuspendsReadsWithoutRebaseliningOrRegistryIo()
    {
        var available = true;
        using var files = new EventFiles(_ => available ? "volume:alice" : null);
        var eventPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var root = Path.GetDirectoryName(files.PlayersPath)!;
        File.WriteAllText(Path.Combine(root, "console.Alice.log"), $"[10:00:00] Writing to {skillsPath}\n");
        using var monitor = new LiveSkillMonitor(files.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: files.Resolver);
        monitor.Start();
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = CreateWithHandoff(files, clock, output =>
        {
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, cycles);
        pipeline.Start();
        var checkpoint = Assert.Single(pipeline.SourceCheckpoints);
        available = false;
        File.AppendAllText(eventPath, "live\n");
        File.AppendAllText(skillsPath, MiningSkillLine(80.001m));
        // Registry access during normal polling would fail this exclusive lock.
        using (new FileStream(Path.Combine(root, "identity-registry", "player-profile-identities.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            monitor.Poll();
            await Step(clock, cycles, 1);
            Assert.Empty(published);
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            var suspended = Assert.Single(monitor.GetSnapshots());
            Assert.False(suspended.IsHealthy);
            Assert.Equal(80m, Assert.Single(suspended.Skills).CurrentValue);
            available = true;
            monitor.Poll();
            await Step(clock, cycles, 2);
            var recovered = Assert.Single(monitor.GetSnapshots());
            Assert.True(recovered.IsHealthy);
            Assert.Equal(checkpoint.ProfileId, recovered.ProfileId);
            Assert.Equal(0.001m, Assert.Single(recovered.Skills).SessionGain);
            var batch = Assert.IsType<EventReadBatch>(Assert.Single(published));
            Assert.Equal(checkpoint.ProfileId, batch.ProfileId);
            Assert.Equal(["live"], batch.Lines);
        }
    }

    [Fact]
    public void SkillOwnerReplacementRequiresAFreshWriterAndReturningOwnerKeepsItsSession()
    {
        using var files = new EventFiles();
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var root = Path.GetDirectoryName(files.PlayersPath)!;
        var consolePath = Path.Combine(root, "console.Alice.log");
        File.WriteAllText(consolePath, $"[10:00:00] Writing to {skillsPath}\n");
        using var monitor = new LiveSkillMonitor(files.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: files.Resolver);
        monitor.Start();
        var oldId = Assert.Single(monitor.GetSnapshots()).ProfileId;
        File.AppendAllText(skillsPath, MiningSkillLine(80.001m));
        monitor.Poll();
        var original = Assert.Single(monitor.GetSnapshots());
        var profilePath = Path.Combine(files.PlayersPath, "Alice");
        var retiredPath = Path.Combine(root, "retired-A");
        Directory.Move(profilePath, retiredPath);
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(90m));
        monitor.Poll();
        var replacement = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId != oldId);
        Assert.Null(replacement.ActiveSourcePath);
        File.AppendAllText(skillsPath, MiningSkillLine(90.001m));
        monitor.Poll();
        Assert.Equal(0m, Assert.Single(monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId != oldId).Skills).SessionGain);
        Assert.Equal(original.Skills, monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId).Skills);
        File.AppendAllText(consolePath, $"[10:00:01] Writing to {skillsPath}\n");
        monitor.Poll();
        Assert.Equal(0.001m, Assert.Single(monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId != oldId).Skills).SessionGain);

        Directory.Move(profilePath, Path.Combine(root, "retired-B"));
        Directory.Move(retiredPath, profilePath);
        File.AppendAllText(skillsPath, MiningSkillLine(80.002m));
        File.AppendAllText(consolePath, $"[10:00:02] Writing to {skillsPath}\n");
        monitor.Poll();
        var returned = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId);
        Assert.True(returned.IsHealthy);
        Assert.Equal(original.ObservationStartedAtUtc, returned.ObservationStartedAtUtc);
        Assert.Equal(original.Skills, returned.Skills); // EOF excludes updates while detached.
        File.AppendAllText(skillsPath, MiningSkillLine(80.003m));
        monitor.Poll();
        Assert.Equal(0.002m, Assert.Single(monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId).Skills).SessionGain);
        Assert.Equal(0.001m, Assert.Single(monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == replacement.ProfileId).Skills).SessionGain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerReplacementKeepsOldCheckpointUntilContinuityHandoffCompletes(bool indeterminate)
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var attempts = 0;
        var accept = false;
        var published = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = CreateWithHandoff(files, clock, output =>
        {
            if (output is EventContinuityLoss)
            {
                attempts++;
                if (!accept)
                    return indeterminate ? EventPipelineHandoffResult.Indeterminate : EventPipelineHandoffResult.NotStarted;
            }
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, cycles);
        pipeline.Start();
        var before = Assert.Single(pipeline.SourceCheckpoints);
        Directory.Move(Path.Combine(files.PlayersPath, "Alice"), Path.Combine(Path.GetDirectoryName(files.PlayersPath)!, "retired"));
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "replacement history\n");
        await Step(clock, cycles, 1);
        Assert.Equal(before, Assert.Single(pipeline.SourceCheckpoints));
        Assert.Equal(1, attempts);
        File.AppendAllText(path, "before admission\n");
        accept = true;
        await Step(clock, cycles, 2);
        await Step(clock, cycles, 3);
        Assert.Equal(1, attempts); // no resolution retry on the 250 ms poll.
        await Step(clock, cycles, 4);
        Assert.Empty(published.OfType<EventReadBatch>());
        if (indeterminate)
        {
            Assert.Equal(before, Assert.Single(pipeline.SourceCheckpoints));
            Assert.Equal(before.ProfileId, Assert.Single(pipeline.TerminalSourceFaults).ProfileId);
            Assert.Equal(1, attempts);
        }
        else
        {
            var after = Assert.Single(pipeline.SourceCheckpoints);
            Assert.NotEqual(before.ProfileId, after.ProfileId);
            Assert.Equal(new FileInfo(path).Length, after.ReadOffset);
            Assert.Empty(pipeline.TerminalSourceFaults);
            Assert.Equal(2, attempts);
            File.AppendAllText(path, "after admission\n");
            await Step(clock, cycles, 5);
            var batch = Assert.Single(published.OfType<EventReadBatch>());
            Assert.Equal(after.ProfileId, batch.ProfileId);
            Assert.Equal(["after admission"], batch.Lines);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedSourceRegistrationDoesNotSplitIdentityWhenFileIdAvailabilityChanges(
        bool skillsFirst, bool initiallyAvailable)
    {
        var identityAvailable = initiallyAvailable;
        using var files = new EventFiles(_ => identityAvailable ? "volume:profile" : null);
        var eventPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var consolePath = Path.Combine(Path.GetDirectoryName(files.PlayersPath)!, "console.Alice.log");
        File.WriteAllText(consolePath, $"[10:00:00] Writing to {skillsPath}\n");
        var published = new ConcurrentQueue<EventPipelineOutput>();
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        EventPipeline CreatePipeline() => CreateWithHandoff(files, clock, output =>
        {
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, cycles);
        LiveSkillMonitor CreateMonitor() => new(files.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: files.Resolver);
        var pipeline = CreatePipeline();
        var monitor = CreateMonitor();
        try
        {
            PlayerProfileId firstId;
            if (skillsFirst)
            {
                monitor.Start();
                firstId = Assert.Single(monitor.GetSnapshots()).ProfileId;
                identityAvailable = !initiallyAvailable;
                pipeline.Start();
            }
            else
            {
                pipeline.Start();
                firstId = Assert.Single(pipeline.SourceCheckpoints).ProfileId;
                identityAvailable = !initiallyAvailable;
                monitor.Start();
            }

            if (initiallyAvailable)
            {
                // Missing evidence cannot create a path-scoped ID for a known
                // confirmed owner. Explicit re-registration succeeds on recovery.
                if (skillsFirst)
                {
                    Assert.Empty(pipeline.SourceCheckpoints);
                    await pipeline.DisposeAsync();
                    identityAvailable = true;
                    pipeline = CreatePipeline();
                    pipeline.Start();
                }
                else
                {
                    Assert.Empty(monitor.GetSnapshots());
                    monitor.Poll();
                    Assert.Empty(monitor.GetSnapshots());
                    monitor.Dispose();
                    identityAvailable = true;
                    monitor = CreateMonitor();
                    monitor.Start();
                }
            }

            var registryPath = Path.Combine(Path.GetDirectoryName(files.PlayersPath)!, "identity-registry", "player-profile-identities.json");
            var registryBytes = File.ReadAllBytes(registryPath);
            File.AppendAllText(eventPath, "live\n");
            File.AppendAllText(skillsPath, MiningSkillLine(80.001m));
            monitor.Poll();
            await Step(clock, cycles, 1);
            var batch = Assert.IsType<EventReadBatch>(Assert.Single(published));
            var snapshot = Assert.Single(monitor.GetSnapshots());
            Assert.Equal(["live"], batch.Lines);
            Assert.NotEqual(default, firstId);
            Assert.Equal(firstId, batch.ProfileId);
            Assert.Equal(firstId, snapshot.ProfileId);
            Assert.Equal(0.001m, Assert.Single(snapshot.Skills).SessionGain);
            Assert.Equal(registryBytes, File.ReadAllBytes(registryPath));
        }
        finally
        {
            await pipeline.DisposeAsync();
            monitor.Dispose();
        }
    }

    [Fact]
    public async Task PhysicalProfileReplacementRebindsEventAndSkillsAtEofWithoutPublishingUnderOldId()
    {
        using var files = new EventFiles();
        var eventPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "You mine some stone shards.\n");
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var root = Path.GetDirectoryName(files.PlayersPath)!;
        var consolePath = Path.Combine(root, "console.Alice.log");
        File.WriteAllText(consolePath, $"[10:00:00] Writing to {skillsPath}\n");
        using var monitor = new LiveSkillMonitor(files.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: files.Resolver);
        monitor.Start();
        var oldId = Assert.Single(monitor.GetSnapshots()).ProfileId;
        var counter = new MiningSessionCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter);
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(processor), context);
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        var released = new ConcurrentQueue<PlayerProfileId>();
        var notifications = new List<LiveSkillProfileChangedEventArgs>();
        monitor.ProfileChanged += (_, args) => notifications.Add(args);
        await using var pipeline = new EventPipeline(files.PlayersPath, output =>
        {
            published.Enqueue(output);
            return dispatcher.Handoff(output);
        }, new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (id, _) => { released.Enqueue(id); dispatcher.ReleaseProfile(id); },
            profileIdentityResolver: files.Resolver);
        try
        {
            pipeline.Start();
            File.AppendAllText(eventPath, "You mine some ore.\n");
            File.AppendAllText(skillsPath, MiningSkillLine(80.001m));
            monitor.Poll();
            await Step(clock, cycles, 1);
            Assert.Equal(oldId, Assert.Single(pipeline.SourceCheckpoints).ProfileId);

            Directory.Move(Path.Combine(files.PlayersPath, "Alice"), Path.Combine(root, "retired-profile"));
            files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "You mine some ore.\nYou mine some ore.\nYou mine some ore.\n");
            files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(90m) + MiningSkillLine(90.001m));
            File.AppendAllText(consolePath, $"[10:00:01] Writing to {skillsPath}\n");
            await Step(clock, cycles, 2);
            monitor.Poll();

            var newId = Assert.Single(pipeline.SourceCheckpoints).ProfileId;
            Assert.NotEqual(oldId, newId);
            Assert.Equal(new FileInfo(eventPath).Length, Assert.Single(pipeline.SourceCheckpoints).ReadOffset);
            Assert.Equal([oldId], released);
            Assert.Single(published.OfType<EventReadBatch>());
            var newSkills = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == newId);
            Assert.Equal(90.001m, Assert.Single(newSkills.Skills).CurrentValue);
            Assert.Equal(0m, Assert.Single(newSkills.Skills).SessionGain);
            var retiredSkills = monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == oldId);
            Assert.False(retiredSkills.IsHealthy);
            Assert.Null(retiredSkills.ActiveSourcePath);
            Assert.Equal(80.001m, Assert.Single(retiredSkills.Skills).CurrentValue);

            File.AppendAllText(eventPath, "You mine some stone shards.\n");
            File.AppendAllText(skillsPath, MiningSkillLine(90.002m));
            await Step(clock, cycles, 3);
            monitor.Poll();
            var additionalPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt",
                "You mine some ore.\n", created: clock.GetUtcNow().UtcDateTime);
            await Step(clock, cycles, 4);
            Assert.Equal(2, pipeline.SourceCheckpoints.Count);
            Assert.All(pipeline.SourceCheckpoints, checkpoint => Assert.Equal(newId, checkpoint.ProfileId));
            Assert.Equal(new FileInfo(additionalPath).Length,
                pipeline.SourceCheckpoints.Single(checkpoint => checkpoint.CanonicalPath == additionalPath).ReadOffset);
            await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));

            var batches = published.OfType<EventReadBatch>().ToArray();
            Assert.Equal(2, batches.Length);
            Assert.Equal(oldId, batches[0].ProfileId);
            Assert.Equal(["You mine some ore."], batches[0].Lines);
            Assert.Equal(newId, batches[1].ProfileId);
            Assert.Equal(["You mine some stone shards."], batches[1].Lines);
            Assert.Equal(1, counter.GetProfileSnapshots()[oldId].TotalCount);
            Assert.Equal(1, counter.GetProfileSnapshots()[newId].TotalCount);
            Assert.DoesNotContain(MiningMaterial.Ore, counter.GetProfileSnapshots()[newId].Materials.Keys);
            Assert.All(notifications.Where(args => args.ProfileId == oldId), args =>
                Assert.Equal(80.001m, Assert.Single(args.Snapshot.Skills).CurrentValue));
            Assert.Equal(0.001m, Assert.Single(monitor.GetSnapshots().Single(snapshot => snapshot.ProfileId == newId).Skills).SessionGain);
        }
        finally
        {
            await pipeline.DisposeAsync();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task OrdinaryEventAndSkillFileRotationKeepsIdentityAndSessionAggregates()
    {
        using var files = new EventFiles();
        var eventPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08.txt", MiningSkillLine(80m));
        var consolePath = Path.Combine(Path.GetDirectoryName(files.PlayersPath)!, "console.Alice.log");
        File.WriteAllText(consolePath, $"[10:00:00] Writing to {skillsPath}\n");
        using var monitor = new LiveSkillMonitor(files.PlayersPath, TimeSpan.FromDays(1), profileIdentityResolver: files.Resolver);
        monitor.Start();
        var id = Assert.Single(monitor.GetSnapshots()).ProfileId;
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = CreateWithHandoff(files, clock, output =>
        {
            published.Enqueue(output);
            return EventPipelineHandoffResult.Complete;
        }, cycles);
        pipeline.Start();
        File.AppendAllText(eventPath, "first\n");
        File.AppendAllText(skillsPath, MiningSkillLine(80.001m));
        monitor.Poll();
        await Step(clock, cycles, 1);

        File.Move(eventPath, eventPath + ".rotated");
        File.WriteAllText(eventPath, "replacement history\n");
        var nextSkills = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Skills.2026-08-15.txt", MiningSkillLine(80.002m));
        File.AppendAllText(consolePath, $"[10:00:01] Writing to {nextSkills}\n");
        monitor.Poll();
        await Step(clock, cycles, 2);
        File.AppendAllText(eventPath, "second\n");
        await Step(clock, cycles, 3);

        Assert.Equal(["first", "second"], published.OfType<EventReadBatch>().SelectMany(batch => batch.Lines));
        Assert.All(published.OfType<EventReadBatch>(), batch => Assert.Equal(id, batch.ProfileId));
        var snapshot = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(id, snapshot.ProfileId);
        Assert.Equal(0.002m, Assert.Single(snapshot.Skills).SessionGain);
    }

    private static string MiningSkillLine(decimal current, decimal delta = 0.001m) =>
        $"[10:00:00] Mining increased by {delta.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')} to {current.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')}\n";

    [Fact]
    public async Task EventPipelineAndLiveSkillMonitor_RegisterSamePhysicalProfileWithSameIdentity()
    {
        using var files = new EventFiles();
        const string profileName = "Alice";
        var eventPath = files.Add(EventTransportTestProfileIds.Alice, profileName, "_Event.2026-08.txt", "");
        var skillsPath = files.Add(EventTransportTestProfileIds.Alice, profileName, "_Skills.2026-08.txt", "");
        var consolePath = Path.Combine(Path.GetDirectoryName(files.PlayersPath)!, $"console.{profileName}.log");
        File.WriteAllText(consolePath, $"[10:00:00] Writing to {skillsPath}\n");

        using var monitor = new LiveSkillMonitor(
            files.PlayersPath,
            TimeSpan.FromDays(1),
            profileIdentityResolver: files.Resolver);
        monitor.Start();
        monitor.Poll();
        var skillSnapshot = Assert.Single(monitor.GetSnapshots());
        Assert.Equal(skillsPath, skillSnapshot.ActiveSourcePath);
        Assert.Equal(LiveSkillSourceState.Observing, skillSnapshot.SourceState);

        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var published = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = CreateWithHandoff(
            files,
            clock,
            output =>
            {
                published.Enqueue(output);
                return EventPipelineHandoffResult.Complete;
            },
            cycles);

        pipeline.Start();
        File.AppendAllText(eventPath, "event\n");
        await Step(clock, cycles, 1);

        var eventBatch = Assert.IsType<EventReadBatch>(Assert.Single(published));
        Assert.NotEqual(default, eventBatch.ProfileId);
        Assert.NotEqual(default, skillSnapshot.ProfileId);
        Assert.Equal(eventBatch.ProfileId, skillSnapshot.ProfileId);
    }

    [Fact]
    public async Task DispatcherHandoffCommitsBeforeBlockedDomainProcessingCompletes()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new LineConsumer(seen));
        var mining = new MiningEventLane(_ => { entered.TrySetResult(); release.Wait(); });
        var dispatcher = new FastEventDispatcher(mining, context);
        var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            profileIdentityResolver: files.Resolver);
        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
            File.AppendAllText(path, "B\n");
            await Step(clock, cycles, 2);
            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
            release.Set();
            await pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["A", "B"], seen);
        }
        finally { release.Set(); await pipeline.DisposeAsync(); await dispatcher.CompleteAsync(); }
    }

    [Fact]
    public async Task DispatcherAccountsForFaultedMiningWhileContextAndCheckpointContinue()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new LineConsumer(seen));
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("Mining fault"));
        var dispatcher = new FastEventDispatcher(mining, context);
        var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            profileIdentityResolver: files.Resolver);
        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);
            await mining.GetProfileFaulted(RuntimeId(dispatcher)).WaitAsync(TimeSpan.FromSeconds(5));
            File.AppendAllText(path, "B\n");
            await Step(clock, cycles, 2);
            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
            Assert.Equal(EventModule.Mining, Assert.Single(dispatcher.GetTerminalStatuses()).Module);
            await Assert.ThrowsAsync<AggregateException>(() => pipeline.StopAndCompleteAsync(dispatcher));
            Assert.Equal(["A", "B"], seen);
        }
        finally
        {
            await pipeline.DisposeAsync();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task DispatcherIndeterminateFaultsOnlyItsSourceWithoutCheckpointCommitOrReplay()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(line => seen.Enqueue(line.Text));
        var dispatcher = new FastEventDispatcher(mining, context,
            () => throw new InvalidOperationException("Interrupted handoff"));
        var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (profileId, _) => dispatcher.ReleaseProfile(profileId),
            profileIdentityResolver: files.Resolver);
        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);
            Assert.Equal(0, pipeline.SourceCheckpoints.Single().ReadOffset);
            Assert.IsType<EventPipelineHandoffIndeterminateException>(
                Assert.Single(pipeline.TerminalSourceFaults).Exception);
            await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync());
            Assert.Equal(["A"], seen);
        }
        finally
        {
            await pipeline.DisposeAsync();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task CompleteHandoffCommitsCheckpointWithoutWaitingForDomainWorkers()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var accepted = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = CreateWithHandoff(
            files,
            clock,
            output =>
            {
                accepted.Enqueue(output);
                return EventPipelineHandoffResult.Complete;
            },
            cycles);

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await Step(clock, cycles, 1);

        Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
        Assert.Equal(["A"], Assert.IsType<EventReadBatch>(Assert.Single(accepted)).Lines);
    }

    [Fact]
    public async Task NotStartedHandoffDoesNotCommitCheckpoint()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var attempts = 0;
        await using var pipeline = CreateWithHandoff(
            files,
            clock,
            _ =>
            {
                attempts++;
                return EventPipelineHandoffResult.NotStarted;
            },
            cycles);

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await Step(clock, cycles, 1);

        Assert.Equal(1, attempts);
        Assert.Equal(0, pipeline.SourceCheckpoints.Single().ReadOffset);
    }

    [Fact]
    public async Task IndeterminateHandoffTerminallyFaultsSourceWithoutCommitOrAutomaticReplay()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var attempts = 0;
        var pipeline = CreateWithHandoff(
            files,
            clock,
            _ =>
            {
                attempts++;
                return EventPipelineHandoffResult.Indeterminate;
            },
            cycles);
        using var coordinator = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), coordinator);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);

            var failure = Assert.IsType<EventPipelineHandoffIndeterminateException>(
                Assert.Single(pipeline.TerminalSourceFaults).Exception);
            Assert.IsType<EventReadBatch>(failure.Output);
            Assert.Equal(1, attempts);
            Assert.Equal(0, pipeline.SourceCheckpoints.Single().ReadOffset);

            File.AppendAllText(path, "B\n");
            await Step(clock, cycles, 2);
            Assert.Equal(1, attempts);

            var shutdownFault = await Assert.ThrowsAsync<EventPipelineHandoffIndeterminateException>(
                () => pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, shutdownFault);

            var repeatedStopFault = await Assert.ThrowsAsync<EventPipelineHandoffIndeterminateException>(
                () => pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, repeatedStopFault);

            var disposeFault = await Assert.ThrowsAsync<EventPipelineHandoffIndeterminateException>(
                () => pipeline.DisposeAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, disposeFault);

            var repeatedDisposeFault = await Assert.ThrowsAsync<EventPipelineHandoffIndeterminateException>(
                () => pipeline.DisposeAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(failure, repeatedDisposeFault);
        }
        finally
        {
            await pipeline.DisposeAsync();
            await dispatcher.CompleteAsync();
        }
    }

    [Fact]
    public async Task EventPipelineDispatcherCommitsOneBatchToBothIndependentLanes()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var miningLines = new ConcurrentQueue<string>();
        var prospectingLines = new ConcurrentQueue<string>();
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(new LineConsumer(prospectingLines));
        var mining = new MiningEventLane(line => miningLines.Enqueue(line.Text));
        var dispatcher = new FastEventDispatcher(mining, coordinator);
        var pipeline = new EventPipeline(files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(), clock, scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\nB\n");
            await Step(clock, cycles, 1);
        }
        finally
        {
            await pipeline.DisposeAsync();
        }

        await dispatcher.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["A", "B"], miningLines);
        Assert.Equal(["A", "B"], prospectingLines);
    }

    [Fact]
    public async Task MiningFaultAfterCheckpointTransferIsRecordedWhileLaterHandoffCommits()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        using var releaseFault = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new EventLineCoordinator();
        var mining = new MiningEventLane(line =>
        {
            callbackEntered.TrySetResult();
            releaseFault.Wait();
            throw new InvalidOperationException("Mining failed");
        });
        var dispatcher = new FastEventDispatcher(mining, coordinator);
        var pipeline = new EventPipeline(
            files.PlayersPath, dispatcher.Handoff, new EventLogReader(), clock,
            scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);

            File.AppendAllText(path, "B\n");
            releaseFault.Set();
            var failure = await mining.GetProfileFaulted(RuntimeId(dispatcher)).WaitAsync(TimeSpan.FromSeconds(5));
            await Step(clock, cycles, 2);

            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
            Assert.Equal(0, pipeline.Metrics.OutputFaults);
            Assert.Equal(["A"], Assert.IsType<EventReadBatch>(Assert.Single(failure.UnresolvedOutputs)).Lines);
            await Assert.ThrowsAsync<AggregateException>(
                () => pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseFault.Set();
            await pipeline.DisposeAsync();
            try { await dispatcher.CompleteAsync(); }
            catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task FullMiningQueueRecordsTerminalMiningStatusWhileContextAndCheckpointContinue()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var miningLines = new ConcurrentQueue<string>();
        var prospectingLines = new ConcurrentQueue<string>();
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(new LineConsumer(prospectingLines));
        var mining = new MiningEventLane(line =>
        {
            if (line.Text == "active")
            {
                started.TrySetResult();
                release.Wait();
            }
            miningLines.Enqueue(line.Text);
            if (line.Text == "63")
                drained.TrySetResult();
        });
        var dispatcher = new FastEventDispatcher(mining, coordinator);
        var pipeline = new EventPipeline(
            files.PlayersPath, dispatcher.Handoff, new EventLogReader(), clock,
            scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "active\n");
            await Step(clock, cycles, 1);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < MiningEventLane.QueueCapacityPerProfile; index++)
            {
                File.AppendAllText(path, index + "\n");
                await Step(clock, cycles, index + 2);
            }

            File.AppendAllText(path, "rejected\n");
            await Step(clock, cycles, MiningEventLane.QueueCapacityPerProfile + 2);
            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
            Assert.Equal(0, pipeline.Metrics.OutputFaults);
            Assert.Equal(MiningEventLane.QueueCapacityPerProfile, mining.GetMetrics(RuntimeId(dispatcher)).QueueDepth);
            Assert.Equal(EventModule.Mining, Assert.Single(dispatcher.GetTerminalStatuses()).Module);

            release.Set();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Step(clock, cycles, MiningEventLane.QueueCapacityPerProfile + 3);
            Assert.Equal(new FileInfo(path).Length, pipeline.SourceCheckpoints.Single().ReadOffset);
            await Assert.ThrowsAsync<AggregateException>(
                () => pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));

            var acceptedByMining = new[] { "active" }.Concat(
                Enumerable.Range(0, MiningEventLane.QueueCapacityPerProfile)
                    .Select(index => index.ToString())).ToArray();
            Assert.Equal(acceptedByMining, miningLines);
            Assert.Equal(acceptedByMining.Append("rejected"), prospectingLines);
        }
        finally
        {
            release.Set();
            await pipeline.DisposeAsync();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task OversizedPendingLineTerminallyFaultsOnlyItsSourceWithoutCommittingOrPublishingFollowingLine()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var outputs = new List<EventPipelineOutput>();
        var reader = new EventLogReader(maximumReadBytes: 8, maximumLineBytes: 8);
        var pipeline = new EventPipeline(
            files.PlayersPath, output =>
            {
                outputs.Add(output);
                return EventPipelineHandoffResult.Complete;
            }, reader, clock,
            scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "12345678");
            await Step(clock, cycles, 1);
            Assert.Equal(8, pipeline.SourceCheckpoints.Single().ReadOffset);
            Assert.Equal("12345678", pipeline.SourceCheckpoints.Single().PendingText);
            Assert.Empty(outputs);

            File.AppendAllText(path, "9\nOK\n");
            await Step(clock, cycles, 2);

            Assert.Equal(8, pipeline.SourceCheckpoints.Single().ReadOffset);
            Assert.Equal("12345678", pipeline.SourceCheckpoints.Single().PendingText);
            Assert.Empty(outputs);
            var fault = Assert.IsType<EventLogOversizedLineException>(
                Assert.Single(pipeline.TerminalSourceFaults).Exception);
            Assert.Equal(8, fault.MaximumLineBytes);

            await Step(clock, cycles, 3);
            Assert.Empty(outputs);
            Assert.Single(pipeline.TerminalSourceFaults);
        }
        finally
        {
            await pipeline.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminalAliceFaultDoesNotStopBobPollingOrCheckpointCommits()
    {
        using var files = new EventFiles();
        var alicePath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var bobPath = files.Add(EventTransportTestProfileIds.Bob, "Bob", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var reader = new EventLogReader(maximumReadBytes: 8, maximumLineBytes: 8);
        await using var pipeline = new EventPipeline(
            files.PlayersPath,
            item =>
            {
                output.Enqueue(item);
                return EventPipelineHandoffResult.Complete;
            },
            reader,
            clock,
            scan: files.Scan,
            cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        pipeline.Start();
        File.AppendAllText(alicePath, "12345678");
        await Step(clock, cycles, 1);

        File.AppendAllText(alicePath, "9\n");
        File.AppendAllText(bobPath, "B1\n");
        await Step(clock, cycles, 2);

        var aliceFault = Assert.Single(pipeline.TerminalSourceFaults);
        Assert.Equal("Alice", aliceFault.ProfileName);
        Assert.IsType<EventLogOversizedLineException>(aliceFault.Exception);
        Assert.Equal(["B1"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
        Assert.Equal(new FileInfo(bobPath).Length,
            pipeline.SourceCheckpoints.Single(checkpoint => checkpoint.ProfileName == "Bob").ReadOffset);
        Assert.Equal(8,
            pipeline.SourceCheckpoints.Single(checkpoint => checkpoint.ProfileName == "Alice").ReadOffset);

        File.AppendAllText(alicePath, "ignored\n");
        File.AppendAllText(bobPath, "B2\n");
        await Step(clock, cycles, 3);

        var batches = output.OfType<EventReadBatch>().ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(["B1"], batches[0].Lines);
        Assert.Equal(["B2"], batches[1].Lines);
        Assert.Equal(new FileInfo(bobPath).Length,
            pipeline.SourceCheckpoints.Single(checkpoint => checkpoint.ProfileName == "Bob").ReadOffset);
        Assert.Equal(8,
            pipeline.SourceCheckpoints.Single(checkpoint => checkpoint.ProfileName == "Alice").ReadOffset);
        Assert.Single(pipeline.TerminalSourceFaults);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposeAndCompletePreservesSourceAndEveryModuleFault(
        bool faultMining,
        bool faultContext)
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new FaultingConsumer(() =>
        {
            if (faultContext)
                throw new InvalidOperationException("Context fault");
        }));
        var mining = new MiningEventLane(_ =>
        {
            if (faultMining)
                throw new InvalidOperationException("Mining fault");
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        var lengthCalls = 0;
        var pipeline = new EventPipeline(
            files.PlayersPath,
            dispatcher.Handoff,
            new EventLogReader(),
            clock,
            scan: files.Scan,
            getLength: _ => Interlocked.Increment(ref lengthCalls) == 1
                ? new FileInfo(path).Length
                : throw new InvalidOperationException("Source fault"),
            cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);
            if (faultMining)
                await mining.GetProfileFaulted(RuntimeId(dispatcher)).WaitAsync(TimeSpan.FromSeconds(5));
            if (faultContext)
                await context.GetProfileFaulted(RuntimeId(dispatcher)).WaitAsync(TimeSpan.FromSeconds(5));

            await clock.WaitForTimer();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

            var shutdown = await Assert.ThrowsAsync<AggregateException>(
                () => pipeline.DisposeAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            var failures = shutdown.Flatten().InnerExceptions;
            Assert.Contains(failures, failure => failure.Message == "Source fault");
            if (faultMining)
                Assert.Contains(failures, failure => failure is MiningLaneFaultException);
            if (faultContext)
                Assert.Contains(failures, failure => failure is EventContextLaneFaultException);

            var repeated = await Assert.ThrowsAsync<AggregateException>(
                () => pipeline.DisposeAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(failures.Count, repeated.Flatten().InnerExceptions.Count);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); } catch (InvalidOperationException) { }
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (EventContextLaneFaultException) { }
        }
    }

    [Fact]
    public async Task OversizedSourceFaultStillDrainsPreviouslyAcceptedDispatcherWorkWithoutStoppingTheSourceLoop()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        using var releaseMining = new ManualResetEventSlim();
        var miningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var miningLines = new ConcurrentQueue<string>();
        using var coordinator = new EventLineCoordinator();
        var mining = new MiningEventLane(line =>
        {
            miningEntered.TrySetResult();
            releaseMining.Wait();
            miningLines.Enqueue(line.Text);
        });
        var dispatcher = new FastEventDispatcher(mining, coordinator);
        var pipeline = new EventPipeline(
            files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(maximumReadBytes: 16, maximumLineBytes: 8), clock,
            scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "A\n");
            await Step(clock, cycles, 1);
            await miningEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            File.AppendAllText(path, "123456789");
            await Step(clock, cycles, 2);
            var sourceFault = Assert.IsType<EventLogOversizedLineException>(
                Assert.Single(pipeline.TerminalSourceFaults).Exception);

            var shutdown = pipeline.StopAndCompleteAsync(dispatcher);
            Assert.False(shutdown.IsCompleted);
            releaseMining.Set();
            var shutdownFault = await Assert.ThrowsAsync<EventLogOversizedLineException>(
                () => shutdown.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(sourceFault, shutdownFault);

            var repeatedFault = await Assert.ThrowsAsync<EventLogOversizedLineException>(
                () => pipeline.StopAndCompleteAsync(dispatcher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(sourceFault, repeatedFault);
            Assert.Equal(["A"], miningLines);
        }
        finally
        {
            releaseMining.Set();
            await pipeline.DisposeAsync();
            await dispatcher.CompleteAsync();
        }
    }

    [Fact]
    public async Task RealTerminalSourceFaultChangesM5UiHealth()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        using var coordinator = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), coordinator);
        var pipeline = new EventPipeline(
            files.PlayersPath, dispatcher.Handoff,
            new EventLogReader(maximumReadBytes: 16, maximumLineBytes: 8), clock,
            scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            App.WireEventPipelineUiHealth(pipeline, dispatcher, viewModel, action => action());
            pipeline.Start();
            File.AppendAllText(path, "123456789");
            await Step(clock, cycles, 1);

            Assert.IsType<EventLogOversizedLineException>(
                Assert.Single(pipeline.TerminalSourceFaults).Exception);
            Assert.Equal("Monitoring error", viewModel.StatusText);
        }
        finally
        {
            await pipeline.DisposeAsync();
            await dispatcher.CompleteAsync();
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public async Task FastCyclesStartOnNominalQuarterSecondDeadlines(int cycleMilliseconds)
    {
        using var files = new EventFiles();
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var starts = new List<TimeSpan>();
        await using var pipeline = Create(files, clock, _ => true, cycles,
            getLength: _ =>
            {
                starts.Add(TimeSpan.FromTicks(clock.GetTimestamp()));
                clock.Advance(TimeSpan.FromMilliseconds(cycleMilliseconds));
                return 0;
            });

        pipeline.Start();
        for (var count = 1; count <= 4; count++)
        {
            await clock.WaitForTimer();
            clock.AdvanceTo(TimeSpan.FromMilliseconds(count * 250));
            await cycles.WaitFor(count);
        }

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(750)],
            starts.Select(start => start - TimeSpan.FromMilliseconds(250)));
    }

    [Theory]
    [InlineData(320, 500)]
    [InlineData(810, 1000)]
    public async Task SlowCycleSkipsAllMissedDeadlines(int cycleMilliseconds, int nextStartRelativeMilliseconds)
    {
        using var files = new EventFiles();
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var starts = new List<TimeSpan>();
        await using var pipeline = Create(files, clock, _ => true, cycles,
            getLength: _ =>
            {
                starts.Add(TimeSpan.FromTicks(clock.GetTimestamp()));
                if (starts.Count == 1)
                    clock.Advance(TimeSpan.FromMilliseconds(cycleMilliseconds));
                return 0;
            });

        pipeline.Start();
        await clock.WaitForTimer();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(250));
        await cycles.WaitFor(1);

        var nextAbsoluteDeadline = TimeSpan.FromMilliseconds(250 + nextStartRelativeMilliseconds);
        await clock.WaitForTimer();
        clock.AdvanceTo(nextAbsoluteDeadline - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, pipeline.Metrics.CompletedCycles);
        Assert.Single(starts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await cycles.WaitFor(2);
        Assert.Equal([TimeSpan.FromMilliseconds(250), nextAbsoluteDeadline], starts);
    }

    [Fact]
    public async Task DiscoveryAndVerificationRunOnceAfterSeveralMissedPollDeadlines()
    {
        using var files = new EventFiles();
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var scans = 0;
        var verifications = 0;
        var lengths = 0;
        var reader = new EventLogReader();
        await using var pipeline = Create(files, clock, _ => true, cycles,
            scan: path => { scans++; return files.Scan(path); },
            getLength: _ =>
            {
                if (++lengths == 1)
                    clock.Advance(TimeSpan.FromMilliseconds(10560));
                return 0;
            },
            verify: (file, offset, fingerprint) =>
            {
                verifications++;
                return reader.VerifyCheckpoint(file, offset, fingerprint);
            });

        pipeline.Start();
        Assert.Equal(1, scans);
        await clock.WaitForTimer();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(250));
        await cycles.WaitFor(1);
        Assert.Equal(1, scans);
        Assert.Equal(0, verifications);
        Assert.Equal(1, pipeline.Metrics.CompletedCycles);

        await clock.WaitForTimer();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(11000));
        await cycles.WaitFor(2);
        Assert.Equal(2, scans);
        Assert.Equal(1, verifications);

        await clock.WaitForTimer();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(11250));
        await cycles.WaitFor(3);
        Assert.Equal(2, scans);
        Assert.Equal(1, verifications);
    }

    [Fact]
    public async Task StartupBaselinesExistingFileAndEmitsOnlyLaterAppend()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var cycles = new CycleSignal();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        await Step(clock, cycles, 1);
        Assert.Empty(output);

        File.AppendAllText(path, "live\n");
        await Step(clock, cycles, 2);

        Assert.Equal(["live"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
    }

    [Fact]
    public async Task CapturedBoundaryLeavesLaterAppendForNextCycleAndZeroGrowthDoesNotRead()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var cycles = new CycleSignal();
        var reader = new EventLogReader();
        var reads = new List<(long Start, long End)>();
        var appendDuringFirstRead = true;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            read: (file, start, end, fingerprint, pending) =>
            {
                reads.Add((start, end));
                if (appendDuringFirstRead)
                {
                    appendDuringFirstRead = false;
                    File.AppendAllText(path, "B\n");
                }
                return reader.Read(file, start, end, fingerprint, pending);
            });

        pipeline.Start();
        await Step(clock, cycles, 1);
        Assert.Empty(reads);

        File.AppendAllText(path, "A\n");
        await Step(clock, cycles, 2);
        Assert.Equal([(0L, 2L)], reads);
        Assert.Equal(["A"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);

        await Step(clock, cycles, 3);
        Assert.Equal([(0L, 2L), (2L, 4L)], reads);
        Assert.Equal(["A", "B"], output.OfType<EventReadBatch>().SelectMany(batch => batch.Lines));
        await Step(clock, cycles, 4);
        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public async Task PartialReadCommitsPendingAndOneReadCreatesOneOrderedBatch()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var cycles = new CycleSignal();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        File.AppendAllText(path, "A\npar");
        await Step(clock, cycles, 1);
        Assert.Equal(["A"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);

        File.AppendAllText(path, "tial\nB\nC\n");
        await Step(clock, cycles, 2);
        var batches = output.OfType<EventReadBatch>().ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(["partial", "B", "C"], batches[1].Lines);
        Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<string>>(batches[1].Lines);
        await Step(clock, cycles, 3);
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public async Task PartialOnlyReadCommitsOffsetWithoutEmptyBatch()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var cycles = new CycleSignal();
        var starts = new List<long>();
        var reader = new EventLogReader();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            read: (file, start, end, fingerprint, pending) =>
            {
                starts.Add(start);
                return reader.Read(file, start, end, fingerprint, pending);
            });

        pipeline.Start();
        File.AppendAllText(path, "par");
        await Step(clock, cycles, 1);
        Assert.Empty(output);
        Assert.Equal(0, pipeline.Metrics.LastBatchLineCount);
        File.AppendAllText(path, "tial\n");
        await Step(clock, cycles, 2);
        Assert.Equal([0L, 3L], starts);
        Assert.Equal(["partial"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
    }

    [Fact]
    public async Task NotStartedHandoffKeepsCheckpointForRetry()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var accepted = new ConcurrentQueue<EventPipelineOutput>();
        var attempted = new List<string>();
        var attempts = 0;
        await using var pipeline = Create(files, clock, item =>
        {
            attempted.Add(string.Join(',', ((EventReadBatch)item).Lines));
            attempts++;
            if (attempts == 1)
                return false;
            if (attempts == 2)
                return false;
            accepted.Enqueue(item);
            return true;
        }, cycles);

        pipeline.Start();
        File.AppendAllText(path, "A\nB\n");
        await Step(clock, cycles, 1);
        await Step(clock, cycles, 2);
        Assert.Empty(accepted);
        await Step(clock, cycles, 3);
        Assert.Equal(["A,B", "A,B", "A,B"], attempted);
        Assert.Equal(["A", "B"], Assert.IsType<EventReadBatch>(Assert.Single(accepted)).Lines);
        await Step(clock, cycles, 4);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ReaderRetryThenDisappearanceEmitsBoundaryAndReappearanceBaselinesAtEof()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var reader = new EventLogReader();
        var starts = new List<long>();
        var failOnce = true;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            read: (file, start, end, fingerprint, pending) =>
            {
                starts.Add(start);
                if (failOnce)
                {
                    failOnce = false;
                    return new EventLogReadResult([], start, fingerprint, true, pending);
                }
                return reader.Read(file, start, end, fingerprint, pending);
            });

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await Step(clock, cycles, 1);
        Assert.Empty(output);
        File.Move(path, path + ".away");
        await Step(clock, cycles, 2);
        var loss = Assert.IsType<EventContinuityLoss>(Assert.Single(output));
        Assert.Equal(StreamGapReason.UnrecoverableReadGap, loss.Reason);
        Assert.Equal(1, loss.SourceGeneration);
        await Step(clock, cycles, 3);
        Assert.Single(output);
        File.Move(path + ".away", path);
        await Step(clock, cycles, 4);
        Assert.Single(output);
        File.AppendAllText(path, "B\n");
        await Step(clock, cycles, 5);
        Assert.Equal([0L, 2L], starts);
        var batch = Assert.IsType<EventReadBatch>(output.Last());
        Assert.Equal(1, batch.SourceGeneration);
        Assert.Equal(["B"], batch.Lines);
    }

    [Fact]
    public async Task SlowReadNeverOverlapsAndDoesNotCauseCatchUpBurst()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var reader = new EventLogReader();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            read: (file, start, end, fingerprint, pending) =>
            {
                Interlocked.Increment(ref reads);
                entered.TrySetResult();
                release.Wait();
                return reader.Read(file, start, end, fingerprint, pending);
            });

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(0, pipeline.Metrics.CompletedCycles);
        release.Set();
        await cycles.WaitFor(1);
        Assert.Equal(1, pipeline.Metrics.CompletedCycles);

        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.Equal(1, pipeline.Metrics.CompletedCycles);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await cycles.WaitFor(2);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task DisposeWaitsForCurrentReadAndStopsFurtherCycles()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reader = new EventLogReader();
        var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            read: (file, start, end, fingerprint, pending) =>
            {
                entered.TrySetResult();
                release.Wait();
                return reader.Read(file, start, end, fingerprint, pending);
            });

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = pipeline.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["A"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(output);
        Assert.Equal(1, pipeline.Metrics.CompletedCycles);
    }

    #pragma warning disable xUnit1031 // The callback is deliberately held by this deterministic admission barrier.
    [Fact]
    public async Task StopWaitsForAnAlreadyAdmittedSinkCallbackAndRejectsLaterSourceAdmission()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new ConcurrentQueue<EventPipelineOutput>();
        var pipeline = Create(files, clock, output =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            accepted.Enqueue(output);
            return true;
        }, cycles);

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = pipeline.StopAsync();
        Assert.False(stop.IsCompleted);
        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["A"], Assert.IsType<EventReadBatch>(Assert.Single(accepted)).Lines);
        Assert.True(pipeline.StopAsync().IsCompletedSuccessfully);
        await pipeline.DisposeAsync();
    }
    #pragma warning restore xUnit1031

    [Fact]
    public async Task DiscoveryRunsEverySecondAndClassifiesNewAndLateSources()
    {
        using var files = new EventFiles();
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var scans = 0;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            scan: path => { scans++; return files.Scan(path); });

        pipeline.Start();
        Assert.Equal(1, scans);
        var newPath = files.Add(EventTransportTestProfileIds.Bob, "Bob", "_Event.2026-08.txt", "new\n", clock.GetUtcNow().UtcDateTime);
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt", "old\n", clock.GetUtcNow().UtcDateTime.AddSeconds(-1));
        for (var count = 1; count <= 3; count++)
            await Step(clock, cycles, count);
        Assert.Equal(1, scans);
        await Step(clock, cycles, 4);
        Assert.Equal(2, scans);
        var batch = Assert.IsType<EventReadBatch>(Assert.Single(output));
        Assert.Equal("Bob", batch.ProfileName);
        Assert.Equal(newPath, batch.CanonicalPath);
        Assert.Equal(["new"], batch.Lines);
        await Step(clock, cycles, 5);
        Assert.Single(output);
    }

    [Theory]
    [InlineData("_Event.2026-08.TXT")]
    [InlineData("_Event.2026-08.Txt")]
    public async Task DiscoveryAcceptsCaseInsensitiveEventLogExtension(string fileName)
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", fileName, "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        File.AppendAllText(path, "entry\n");
        await Step(clock, cycles, 1);

        var batch = Assert.IsType<EventReadBatch>(Assert.Single(output));
        Assert.Equal(path, batch.CanonicalPath);
        Assert.Equal(["entry"], batch.Lines);
    }

    [Fact]
    public async Task MultipleSourcesUseStablePathOrderWithoutTimestampSorting()
    {
        using var files = new EventFiles();
        var aliceDaily = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt", "");
        var aliceMonthly = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var bob = files.Add(EventTransportTestProfileIds.Bob, "Bob", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        File.AppendAllText(aliceDaily, "[12:00:03] daily\n");
        File.AppendAllText(aliceMonthly, "[12:00:02] monthly\n");
        File.AppendAllText(bob, "[12:00:01] bob\n");
        await Step(clock, cycles, 1);

        Assert.Equal(
            [aliceDaily, aliceMonthly, bob],
            output.OfType<EventReadBatch>().Select(batch => batch.CanonicalPath));
    }

    [Fact]
    public async Task DiscoveryFailureKeepsKnownSourceAndActiveWindowFiltersSegments()
    {
        using var files = new EventFiles();
        var active = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt", "");
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-07.txt", "old\n");
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-09.txt", "future\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var scans = 0;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            scan: path => ++scans == 2 ? throw new IOException("Enumeration failed") : files.Scan(path));

        pipeline.Start();
        File.AppendAllText(active, "live\n");
        for (var count = 1; count <= 4; count++)
            await Step(clock, cycles, count);
        Assert.Equal(2, scans);
        Assert.Equal(["live"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
    }

    [Fact]
    public async Task DailySegmentLeavesActiveWindowAfterFiveMinuteGrace()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt", "history\n");
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 16, 0, 4, 0, TimeSpan.Zero));
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        File.AppendAllText(path, "within-grace\n");
        await Step(clock, cycles, 1);
        Assert.Equal(["within-grace"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);

        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMinutes(1));
        await cycles.WaitFor(2);
        File.AppendAllText(path, "after-grace\n");
        await Step(clock, cycles, 3);
        Assert.Single(output);
    }

    [Fact]
    public async Task FinalExpiredProfileSourceStartsDeterministicLaneRelease()
    {
        using var files = new EventFiles();
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt", "");
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 16, 0, 4, 0, TimeSpan.Zero));
        var cycles = new CycleSignal();
        var releasedProfiles = new ConcurrentQueue<PlayerProfileId>();
        await using var pipeline = new EventPipeline(
            files.PlayersPath,
            _ => EventPipelineHandoffResult.Complete,
            new EventLogReader(),
            clock,
            scan: files.Scan,
            cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (profileId, _) => releasedProfiles.Enqueue(profileId),
            profileIdentityResolver: files.Resolver);

        pipeline.Start();
        await Step(clock, cycles, 1);
        var registeredProfileId = Assert.Single(pipeline.SourceCheckpoints).ProfileId;

        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMinutes(1));
        await cycles.WaitFor(2);

        Assert.Equal(registeredProfileId, Assert.Single(releasedProfiles));
    }

    [Fact]
    public async Task ExpiringOneOfSeveralProfileSourcesDoesNotReleaseItsLane()
    {
        using var files = new EventFiles();
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08-15.txt", "");
        files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 16, 0, 4, 0, TimeSpan.Zero));
        var cycles = new CycleSignal();
        var releasedProfiles = new ConcurrentQueue<PlayerProfileId>();
        await using var pipeline = new EventPipeline(
            files.PlayersPath,
            _ => EventPipelineHandoffResult.Complete,
            new EventLogReader(),
            clock,
            scan: files.Scan,
            cycleCompleted: cycles.Signal,
            releaseProfileWithIdentity: (profileId, _) => releasedProfiles.Enqueue(profileId),
            profileIdentityResolver: files.Resolver);

        pipeline.Start();
        await Step(clock, cycles, 1);

        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMinutes(1));
        await cycles.WaitFor(2);

        Assert.Empty(releasedProfiles);
    }

    [Fact]
    public async Task StartupBaselineFailureNeverReplaysFromZero()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var reader = new EventLogReader();
        var first = true;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            capture: file =>
            {
                if (first)
                {
                    first = false;
                    throw new IOException("Locked");
                }
                return reader.CaptureSnapshot(file);
            });

        pipeline.Start();
        File.AppendAllText(path, "before-baseline\n");
        await Step(clock, cycles, 1);
        Assert.Empty(output);
        File.AppendAllText(path, "after\n");
        await Step(clock, cycles, 2);
        Assert.Equal(["after"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
    }

    [Fact]
    public async Task PendingStartupBaselineDoesNotBlockOtherLiveOutputAndNeverPublishesItsHistory()
    {
        using var files = new EventFiles();
        var pendingPath = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "history\n");
        var readyPath = files.Add(EventTransportTestProfileIds.Bob, "Bob", "_Event.2026-08.txt", "history\n");
        var clock = new ManualClock();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var cycles = new CycleSignal();
        var reader = new EventLogReader();
        var permitBaseline = false;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            capture: path => path == pendingPath && !permitBaseline
                ? throw new IOException("Baseline unavailable")
                : reader.CaptureSnapshot(path));

        pipeline.Start();
        File.AppendAllText(readyPath, "live\n");
        File.AppendAllText(pendingPath, "before-baseline\n");
        await Step(clock, cycles, 1);
        Assert.Equal(["live"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
        permitBaseline = true;
        await Step(clock, cycles, 2);
        File.AppendAllText(pendingPath, "after-baseline\n");
        await Step(clock, cycles, 3);
        var batches = output.OfType<EventReadBatch>().ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal("Bob", batches[0].ProfileName);
        Assert.Equal(["live"], batches[0].Lines);
        Assert.Equal("Alice", batches[1].ProfileName);
        Assert.Equal(["after-baseline"], batches[1].Lines);
    }

    [Fact]
    public async Task ValidVerificationPreservesPendingAndDoesNotReadIncrementally()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var cycles = new CycleSignal();
        var reader = new EventLogReader();
        var reads = 0;
        var verifications = 0;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            read: (file, start, end, fingerprint, pending) =>
            {
                reads++;
                return reader.Read(file, start, end, fingerprint, pending);
            },
            verify: (file, offset, fingerprint) =>
            {
                verifications++;
                return reader.VerifyCheckpoint(file, offset, fingerprint);
            });

        pipeline.Start();
        File.AppendAllText(path, "pending");
        await Step(clock, cycles, 1);
        for (var count = 2; count <= 40; count++)
            await Step(clock, cycles, count);
        Assert.Equal(1, verifications);
        Assert.Equal(1, reads);
        Assert.Empty(output);
        File.AppendAllText(path, "\n");
        await Step(clock, cycles, 41);
        Assert.Equal(["pending"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
    }

    [Fact]
    public async Task VerificationAtTenSecondsDetectsSameLengthReplacementBeforeNextBatch()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "old\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var reader = new EventLogReader();
        var verifications = 0;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            verify: (file, offset, fingerprint) =>
            {
                verifications++;
                return reader.VerifyCheckpoint(file, offset, fingerprint);
            });

        pipeline.Start();
        for (var count = 1; count <= 39; count++)
            await Step(clock, cycles, count);
        Assert.Equal(0, verifications);
        File.WriteAllText(path, "new\n");
        await Step(clock, cycles, 40);
        Assert.Equal(1, verifications);
        var loss = Assert.IsType<EventContinuityLoss>(Assert.Single(output));
        Assert.Equal(StreamGapReason.Replaced, loss.Reason);
        Assert.Equal(1, loss.SourceGeneration);
        File.AppendAllText(path, "after\n");
        await Step(clock, cycles, 41);
        var items = output.ToArray();
        Assert.IsType<EventContinuityLoss>(items[0]);
        var batch = Assert.IsType<EventReadBatch>(items[1]);
        Assert.Equal(1, batch.SourceGeneration);
        Assert.Equal(["after"], batch.Lines);
    }

    [Fact]
    public async Task SameLengthReplacementWithIdenticalSuffixEmitsControlBeforeFurtherOutput()
    {
        using var files = new EventFiles();
        var suffix = new string('s', 4096);
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", new string('a', 8192) + suffix);
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        File.WriteAllText(path, new string('b', 8192) + suffix);
        for (var count = 1; count <= 40; count++)
            await Step(clock, cycles, count);

        var loss = Assert.IsType<EventContinuityLoss>(Assert.Single(output));
        Assert.Equal(StreamGapReason.Replaced, loss.Reason);
        File.AppendAllText(path, "live\n");
        await Step(clock, cycles, 41);
        var batch = Assert.IsType<EventReadBatch>(output.Last());
        Assert.Equal(1, batch.SourceGeneration);
        Assert.Equal(["live"], batch.Lines);
    }

    [Fact]
    public async Task ReplacementDuringGrowthDoesNotPublishReplacementHistoryAsAppend()
    {
        using var files = new EventFiles();
        var suffix = new string('s', 4096);
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", new string('a', 8192) + suffix);
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        File.WriteAllText(path, new string('b', 8192) + suffix + "new\n");
        await Step(clock, cycles, 1);

        Assert.Equal(StreamGapReason.Replaced,
            Assert.IsType<EventContinuityLoss>(Assert.Single(output)).Reason);
        File.AppendAllText(path, "live\n");
        await Step(clock, cycles, 2);
        Assert.Equal(["live"], Assert.IsType<EventReadBatch>(output.Last()).Lines);
    }

    [Fact]
    public async Task DisappearanceClearsPendingOnlyAfterControlAcceptance()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var rejectControl = true;
        var pipeline = new EventPipeline(
            files.PlayersPath, item =>
            {
                if (item is EventContinuityLoss && rejectControl)
                {
                    rejectControl = false;
                    return EventPipelineHandoffResult.NotStarted;
                }
                return output.EnqueueAccepted(item)
                    ? EventPipelineHandoffResult.Complete
                    : EventPipelineHandoffResult.NotStarted;
            }, new EventLogReader(), clock,
            scan: files.Scan, cycleCompleted: cycles.Signal,
            profileIdentityResolver: files.Resolver);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "partial");
            await Step(clock, cycles, 1);
            var before = Assert.Single(pipeline.SourceCheckpoints);
            Assert.Equal("partial", before.PendingText);

            File.Move(path, path + ".away");
            await Step(clock, cycles, 2);
            Assert.Empty(output);
            Assert.Equal(before, Assert.Single(pipeline.SourceCheckpoints));

            File.WriteAllText(path, "new history\n");
            await Step(clock, cycles, 3);
            Assert.Equal(StreamGapReason.UnrecoverableReadGap,
                Assert.IsType<EventContinuityLoss>(Assert.Single(output)).Reason);
            Assert.Equal(before.ReadOffset, Assert.Single(pipeline.SourceCheckpoints).ReadOffset);
            Assert.Empty(Assert.Single(pipeline.SourceCheckpoints).PendingText);

            await Step(clock, cycles, 4);
            Assert.Equal(new FileInfo(path).Length, Assert.Single(pipeline.SourceCheckpoints).ReadOffset);
            File.AppendAllText(path, "live\n");
            await Step(clock, cycles, 5);
            Assert.Equal(["live"], Assert.IsType<EventReadBatch>(output.Last()).Lines);
        }
        finally
        {
            await pipeline.DisposeAsync();
        }
    }

    [Fact]
    public async Task TransientFileNotFoundErrorWhileFileExistsDoesNotBreakContinuity()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var first = true;
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles,
            getLength: file =>
            {
                if (first)
                {
                    first = false;
                    throw new FileNotFoundException("Transient open race");
                }
                return new FileInfo(file).Length;
            });

        pipeline.Start();
        File.AppendAllText(path, "live\n");
        await Step(clock, cycles, 1);
        Assert.Empty(output);
        Assert.Equal(1, pipeline.Metrics.SourceReadFaults);
        await Step(clock, cycles, 2);
        Assert.Equal(["live"], Assert.IsType<EventReadBatch>(Assert.Single(output)).Lines);
        Assert.Equal(0, pipeline.Metrics.ContinuityEvents);
    }

    [Fact]
    public async Task TruncateControlUsesSafeEofBaselineWithoutReplay()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "long history\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        await using var pipeline = Create(files, clock, output.EnqueueAccepted, cycles);

        pipeline.Start();
        var utf8 = Encoding.UTF8.GetBytes("ą");
        File.WriteAllBytes(path, [utf8[0]]);
        await Step(clock, cycles, 1);
        var loss = Assert.IsType<EventContinuityLoss>(Assert.Single(output));
        Assert.Equal(StreamGapReason.Truncated, loss.Reason);
        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            append.WriteByte(utf8[1]);
            append.Write(Encoding.UTF8.GetBytes("\nnext\n"));
        }
        await Step(clock, cycles, 2);
        Assert.Equal(["ą", "next"], Assert.IsType<EventReadBatch>(output.Last()).Lines);
    }

    [Fact]
    public async Task VerificationRetryAndRejectedContinuityKeepOldGeneration()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "old\n");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var reader = new EventLogReader();
        var verificationFault = true;
        var rejectControl = true;
        await using var pipeline = Create(files, clock, item =>
        {
            if (item is EventContinuityLoss && rejectControl)
            {
                rejectControl = false;
                return false;
            }
            output.Enqueue(item);
            return true;
        }, cycles, verify: (file, offset, fingerprint) =>
        {
            if (verificationFault)
            {
                verificationFault = false;
                return EventCheckpointVerificationResult.RequiresRetry;
            }
            return reader.VerifyCheckpoint(file, offset, fingerprint);
        });

        pipeline.Start();
        File.WriteAllText(path, "new\n");
        for (var count = 1; count <= 40; count++)
            await Step(clock, cycles, count);
        Assert.Empty(output);
        await Step(clock, cycles, 41);
        Assert.Empty(output);
        await Step(clock, cycles, 42);
        Assert.Equal(1, Assert.IsType<EventContinuityLoss>(Assert.Single(output)).SourceGeneration);
        Assert.Equal(1, pipeline.Metrics.ContinuityEvents);
    }

    private static EventPipeline Create(
        EventFiles files,
        ManualClock clock,
        Func<EventPipelineOutput, bool> sink,
        CycleSignal cycles,
        Func<string, IReadOnlyList<EventLogFileCandidate>>? scan = null,
        Func<string, long>? getLength = null,
        Func<string, (long Position, EventFileFingerprint Fingerprint)>? capture = null,
        Func<string, long, long, EventFileFingerprint?, string, EventLogReadResult>? read = null,
        Func<string, long, EventFileFingerprint, EventCheckpointVerificationResult>? verify = null) =>
        new(files.PlayersPath,
            output => sink(output)
                ? EventPipelineHandoffResult.Complete
                : EventPipelineHandoffResult.NotStarted,
            new EventLogReader(), clock,
            scan ?? files.Scan, getLength: getLength, captureSnapshot: capture, read: read, verify: verify,
            cycleCompleted: cycles.Signal, profileIdentityResolver: files.Resolver);

    [Fact]
    public async Task SourceLifecycleEventsExposeDiscoveryBaselineAndTerminalFaultWithoutRawLines()
    {
        using var files = new EventFiles();
        var path = files.Add(EventTransportTestProfileIds.Alice, "Alice", "_Event.2026-08.txt", "");
        var clock = new ManualClock();
        var cycles = new CycleSignal();
        var discovered = new ConcurrentQueue<EventPipelineSourceStatus>();
        var baselined = new ConcurrentQueue<EventPipelineSourceStatus>();
        var terminal = new ConcurrentQueue<EventPipelineSourceFault>();
        await using var pipeline = CreateWithHandoff(
            files, clock, _ => EventPipelineHandoffResult.Indeterminate, cycles);
        pipeline.SourceDiscovered += discovered.Enqueue;
        pipeline.SourceBaselined += baselined.Enqueue;
        pipeline.TerminalSourceFaulted += terminal.Enqueue;

        pipeline.Start();
        File.AppendAllText(path, "A\n");
        await Step(clock, cycles, 1);

        Assert.Equal("Alice", Assert.Single(discovered).ProfileName);
        Assert.Equal("Alice", Assert.Single(baselined).ProfileName);
        var fault = Assert.Single(terminal);
        Assert.Equal("Alice", fault.ProfileName);
        Assert.IsType<EventPipelineHandoffIndeterminateException>(fault.Exception);
    }

    private static EventPipeline CreateWithHandoff(
        EventFiles files,
        ManualClock clock,
        Func<EventPipelineOutput, EventPipelineHandoffResult> handoff,
        CycleSignal cycles) =>
        new(files.PlayersPath, handoff, new EventLogReader(), clock,
            files.Scan, cycleCompleted: cycles.Signal, profileIdentityResolver: files.Resolver);

    private static async Task Step(ManualClock clock, CycleSignal cycles, long count)
    {
        await clock.WaitForTimer();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await cycles.WaitFor(count);
    }

    private sealed class EventFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmEventPipeline-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, DateTime> _created = new(StringComparer.OrdinalIgnoreCase);

        public EventFiles(Func<string, string?>? physicalIdentity = null)
        {
            PlayersPath = Path.Combine(_root, "players");
            Directory.CreateDirectory(PlayersPath);
            Resolver = physicalIdentity is null
                ? new PlayerProfileIdentityResolver(Path.Combine(_root, "identity-registry"))
                : new PlayerProfileIdentityResolver(Path.Combine(_root, "identity-registry"),
                    new DelegateDirectoryPhysicalIdentityProvider(physicalIdentity));
        }

        public string PlayersPath { get; }

        public PlayerProfileIdentityResolver Resolver { get; }

        public string Add(PlayerProfileId profileId, string profileName, string name, string text, DateTime? created = null)
        {
            var directory = Path.Combine(PlayersPath, profileName, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, text);
            _created[path] = created ?? new DateTime(2026, 8, 15, 11, 0, 0, DateTimeKind.Utc);
            return path;
        }

        public IReadOnlyList<EventLogFileCandidate> Scan(string path) =>
            new EventLogReconciliationScanner().Scan(path)
                .Select(candidate => new EventLogFileCandidate
                {
                    ProfileName = candidate.ProfileName,
                    ProfileDirectoryPath = Path.GetDirectoryName(
                        Path.GetDirectoryName(candidate.EventFilePath)!)!,
                    EventFilePath = candidate.EventFilePath,
                    CreationTimeUtc = _created[candidate.EventFilePath]
                }).ToArray();

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class LineConsumer(ConcurrentQueue<string> lines) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item)
        {
            if (item is OrderedEventLine line)
                lines.Enqueue(line.Line.Text);
        }

        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }

    private sealed class FaultingConsumer(Action process) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) => process();

        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }

    private sealed class CycleSignal
    {
        private readonly object _sync = new();
        private TaskCompletionSource _changed = NewSignal();
        private long _count;

        public void Signal(long count)
        {
            lock (_sync)
            {
                _count = count;
                _changed.TrySetResult();
                _changed = NewSignal();
            }
        }

        public async Task WaitFor(long count)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_count >= count)
                        return;
                    wait = _changed.Task;
                }
                await wait.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly SemaphoreSlim _registrations = new(0);
        private DateTimeOffset _now;
        private long _timestamp;

        public ManualClock(DateTimeOffset? now = null)
        {
            _now = now ?? new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override long GetTimestamp() { lock (_sync) return _timestamp; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state, _timestamp + dueTime.Ticks);
                _timers.Add(timer);
                _registrations.Release();
                return timer;
            }
        }

        public async Task WaitForTimer() => await _registrations.WaitAsync(TimeSpan.FromSeconds(5));

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _timestamp += elapsed.Ticks;
                _now += elapsed;
                due = _timers.Where(timer => timer.DueTimestamp <= _timestamp).ToList();
                foreach (var timer in due)
                    timer.DueTimestamp = long.MaxValue;
            }
            foreach (var timer in due)
                timer.Fire();
        }

        public void AdvanceTo(TimeSpan timestamp)
        {
            var remaining = timestamp - TimeSpan.FromTicks(GetTimestamp());
            Assert.True(remaining >= TimeSpan.Zero);
            Advance(remaining);
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, long dueTimestamp) : ITimer
        {
            public long DueTimestamp { get; set; } = dueTimestamp;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    DueTimestamp = owner._timestamp + dueTime.Ticks;
                    owner._registrations.Release();
                    return true;
                }
            }
            public void Dispose()
            {
                lock (owner._sync)
                    owner._timers.Remove(this);
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

internal static class EventPipelineTestSink
{
    public static bool EnqueueAccepted(this ConcurrentQueue<EventPipelineOutput> output, EventPipelineOutput item)
    {
        output.Enqueue(item);
        return true;
    }
}
