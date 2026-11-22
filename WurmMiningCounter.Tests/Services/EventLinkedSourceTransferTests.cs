using System.Collections.Concurrent;
using System.Diagnostics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventLinkedSourceTransferTests
{
    [Fact]
    public async Task SameOwnerRecoversWhenConfirmedSnapshotOmitsMissingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-owner-recovery-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var link = Path.Combine(alice, "logs");
        try
        {
            foreach (var path in new[] { alice, logsA, logsB }) Directory.CreateDirectory(path);
            Junction(link, logsA);
            var file = Path.Combine(logsA, "_Event.2026-10.txt");
            File.WriteAllText(file, "history\n");
            var ids = new WindowsDirectoryPhysicalIdentityProvider();
            var candidate = new LogDiscoverySourceCandidate("Alice", alice, logsA,
                [new WurmLogFileLocation(WurmLogFileKind.Event, file, players, "Alice", logsA)], [], [], null, true, "", "")
            { PhysicalDirectoryKey = ids.GetIdentity(alice), PhysicalLogsDirectoryKey = ids.GetIdentity(logsA), LogicalLogsDirectoryPath = link };
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            var output = new ConcurrentQueue<EventPipelineOutput>();
            await using var pipeline = new EventPipeline(players, item => { output.Enqueue(item); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: new PlayerProfileIdentityResolver(Path.Combine(root, "registry")), sourceCandidates: [candidate]);
            pipeline.Start();
            var checkpoint = Assert.Single(pipeline.SourceCheckpoints);
            Directory.Delete(link);
            Junction(link, logsB);
            File.Delete(file);
            pipeline.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            Assert.Empty(output);
            Directory.Delete(link);
            Junction(link, logsA);
            pipeline.PublishDiscoverySnapshot(1, [candidate with { EventFiles = [] }], [], true);
            pipeline.RunCycleForTest();
            Assert.Equal(StreamGapReason.UnrecoverableReadGap, Assert.IsType<EventContinuityLoss>(Assert.Single(output)).Reason);
            File.WriteAllText(file, "replacement\n");
            pipeline.RunCycleForTest();
            Assert.Equal(new FileInfo(file).Length, Assert.Single(pipeline.SourceCheckpoints).ReadOffset);
            File.AppendAllText(file, "live\n");
            pipeline.RunCycleForTest();
            Assert.Equal(["live"], Assert.Single(output.OfType<EventReadBatch>()).Lines);
            await pipeline.StopAsync();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConfirmedDifferentOwnerDoesNotResumeOldOwnerUntilRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-owner-suspended-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var bob = Path.Combine(players, "Bob");
        var logs = Path.Combine(root, "logs");
        var aliceLink = Path.Combine(alice, "logs");
        var bobLink = Path.Combine(bob, "logs");
        try
        {
            foreach (var path in new[] { alice, bob, logs }) Directory.CreateDirectory(path);
            Junction(aliceLink, logs);
            Junction(bobLink, logs);
            var file = Path.Combine(logs, "_Event.2026-10.txt");
            File.WriteAllText(file, "history\n");
            var ids = new WindowsDirectoryPhysicalIdentityProvider();
            LogDiscoverySourceCandidate Candidate(string name, string profile, string link) => new(name, profile, logs,
                [new WurmLogFileLocation(WurmLogFileKind.Event, file, players, name, logs)], [], [], null, true, "", "")
            { PhysicalDirectoryKey = ids.GetIdentity(profile), PhysicalLogsDirectoryKey = ids.GetIdentity(logs), LogicalLogsDirectoryPath = link };
            var original = Candidate("Alice", alice, aliceLink);
            var replacement = Candidate("Bob", bob, bobLink);
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var viewModel = new MainViewModel(new MiningSessionCounter());
            App.WireIdentityActivationUiHealth(resolver, viewModel, action => action(), new RuntimeDiagnosticService());
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            var output = new ConcurrentQueue<EventPipelineOutput>();
            await using var pipeline = new EventPipeline(players, item => { output.Enqueue(item); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: [original]);
            pipeline.Start();
            var checkpoint = Assert.Single(pipeline.SourceCheckpoints);
            pipeline.PublishDiscoverySnapshot(1, [replacement], [], true);
            pipeline.AddSourceCandidates(1, [replacement]);
            pipeline.RunCycleForTest();
            Assert.Equal("Monitoring - Event owner changed; restart required", viewModel.StatusText);
            pipeline.PublishDiscoverySnapshot(2, [replacement], [], false);
            pipeline.RunCycleForTest();
            Assert.Equal("Monitoring - Event owner changed; restart required", viewModel.StatusText);
            File.AppendAllText(file, "while suspended\n");
            clock.Advance(TimeSpan.FromSeconds(10));
            pipeline.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            Assert.Empty(output);
            pipeline.PublishDiscoverySnapshot(3, [original], [], true);
            pipeline.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            Assert.Empty(output);
            await pipeline.StopAsync();
            Assert.Equal("Monitoring", viewModel.StatusText);

            var restartedOutput = new ConcurrentQueue<EventPipelineOutput>();
            await using var restarted = new EventPipeline(players, item => { restartedOutput.Enqueue(item); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: [replacement]);
            restarted.Start();
            Assert.Equal("Monitoring", viewModel.StatusText);
            var bobCheckpoint = Assert.Single(restarted.SourceCheckpoints);
            Assert.Equal("Bob", bobCheckpoint.ProfileName);
            Assert.Equal(new FileInfo(file).Length, bobCheckpoint.ReadOffset);
            Assert.Empty(restartedOutput);
            File.AppendAllText(file, "bob live\n");
            restarted.RunCycleForTest();
            Assert.Equal(["bob live"], Assert.Single(restartedOutput.OfType<EventReadBatch>()).Lines);
            await restarted.StopAsync();
        }
        finally
        {
            foreach (var path in new[] { aliceLink, bobLink }) if (Directory.Exists(path)) Directory.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExpiredSuspendedSourceKeepsResolvedConflictEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-suspended-history-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var link = Path.Combine(alice, "logs");
        try
        {
            foreach (var path in new[] { alice, logsA, logsB }) Directory.CreateDirectory(path);
            Junction(link, logsA);
            var file = Path.Combine(logsA, "_Event.2026-10-16.txt");
            File.WriteAllText(file, "history\n");
            var identities = new WindowsDirectoryPhysicalIdentityProvider();
            var candidate = new LogDiscoverySourceCandidate("Alice", alice, logsA,
                [new WurmLogFileLocation(WurmLogFileKind.Event, file, players, "Alice", logsA)], [], [], null, true, "", "")
            { PhysicalDirectoryKey = identities.GetIdentity(alice), LogicalLogsDirectoryPath = link,
                PhysicalLogsDirectoryKey = identities.GetIdentity(logsA) };
            var rival = candidate with { DisplayName = "Bob", ProfileDirectoryPath = Path.Combine(players, "Bob"), PhysicalDirectoryKey = "other" };
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            await using var pipeline = new EventPipeline(players, _ => EventPipelineHandoffResult.Complete,
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: [candidate]);
            pipeline.Start();
            pipeline.PublishDiscoverySnapshot(1, [candidate, rival], [], false);
            pipeline.RunCycleForTest();
            pipeline.PublishDiscoverySnapshot(2, [candidate], [], true);
            pipeline.RunCycleForTest();
            var resolvedFiles = (HashSet<string>)typeof(EventPipeline)
                .GetField("_resumedConflictFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(pipeline)!;
            Assert.Contains(file, resolvedFiles);
            Directory.Delete(link);
            Junction(link, logsB);
            File.AppendAllText(file, "unverified\n");
            pipeline.RunCycleForTest();
            Assert.True(Assert.Single(resolver.GetActivationStatusSnapshot(), item => item.ProfileDirectoryPath == alice).IsSuspended);
            clock.Advance(TimeSpan.FromDays(1));
            pipeline.RunCycleForTest();
            Assert.Empty(pipeline.SourceCheckpoints);
            Assert.Contains(file, resolvedFiles);
            await pipeline.StopAsync();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task LostLogsBindingIsSuspendedAndRetriedOnVerificationOrNewEvidence(bool checkStatus, bool recoverBySnapshot, bool uncertainDuringLoss)
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-lost-binding-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var carol = Path.Combine(players, "Carol");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var link = Path.Combine(alice, "logs");
        try
        {
            foreach (var path in new[] { alice, logsA, logsB, Path.Combine(carol, "logs") }) Directory.CreateDirectory(path);
            Junction(link, logsA);
            var file = Path.Combine(logsA, "_Event.2026-10.txt");
            var carolFile = Path.Combine(carol, "logs", "_Event.2026-10.txt");
            File.WriteAllText(file, "history\n");
            File.WriteAllText(carolFile, "history\n");
            var identities = new WindowsDirectoryPhysicalIdentityProvider();
            LogDiscoverySourceCandidate Candidate(string name, string profile, string logical, string physical, string path) => new(
                name, profile, physical, [new WurmLogFileLocation(WurmLogFileKind.Event, path, players, name, physical)], [], [], null, true, "", "")
            { PhysicalDirectoryKey = identities.GetIdentity(profile), LogicalLogsDirectoryPath = logical, PhysicalLogsDirectoryKey = identities.GetIdentity(physical) };
            var candidate = Candidate("Alice", alice, link, logsA, file);
            var carolCandidate = Candidate("Carol", carol, Path.Combine(carol, "logs"), Path.Combine(carol, "logs"), carolFile);
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var viewModel = new MainViewModel(new MiningSessionCounter());
            App.WireIdentityActivationUiHealth(resolver, viewModel, action => action(), new RuntimeDiagnosticService());
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            var outputs = new ConcurrentQueue<EventPipelineOutput>();
            var aliceChecks = 0;
            await using var pipeline = new EventPipeline(players, item => { outputs.Enqueue(item); return EventPipelineHandoffResult.Complete; },
                new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: [candidate, carolCandidate],
                logsBindingCheck: path => { if (path == link) aliceChecks++; });
            pipeline.PublishDiscoverySnapshot(1, [candidate, carolCandidate], [], true);
            pipeline.Start();
            var checkpoint = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == file);
            Assert.True(Assert.Single(resolver.GetActivationStatusSnapshot(), item => item.ProfileDirectoryPath == alice).IsActive);
            Directory.Delete(link);
            Junction(link, logsB);
            File.AppendAllText(file, "retained\n");
            pipeline.RunCycleForTest();
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == file));
            Assert.Empty(pipeline.TerminalSourceFaults);
            if (checkStatus)
            {
                Assert.True(Assert.Single(resolver.GetActivationStatusSnapshot(), item => item.ProfileDirectoryPath == alice).IsSuspended);
                Assert.Equal("Monitoring identity warning", viewModel.StatusText);
            }
            var checksAtLoss = aliceChecks;
            File.AppendAllText(carolFile, "carol live\n");
            for (var i = 0; i < 4; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                pipeline.RunCycleForTest();
            }
            Assert.Equal(checksAtLoss, aliceChecks);
            Assert.Equal(["carol live"], Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == carolFile).Lines);
            if (uncertainDuringLoss)
            {
                pipeline.PublishDiscoverySnapshot(2, [candidate, carolCandidate], [], false);
                pipeline.RunCycleForTest();
                Assert.Equal(checksAtLoss, aliceChecks);
            }
            Directory.Delete(link);
            Junction(link, logsA);
            if (recoverBySnapshot)
                pipeline.PublishDiscoverySnapshot(2, [candidate, carolCandidate], [], true);
            else
                clock.Advance(TimeSpan.FromSeconds(10));
            pipeline.RunCycleForTest();
            Assert.Equal(["retained"], Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == file).Lines);
            var status = Assert.Single(resolver.GetActivationStatusSnapshot(), item => item.ProfileDirectoryPath == alice);
            Assert.True(status.IsActive);
            Assert.False(status.IsSuspended);
            Assert.Equal("Monitoring", viewModel.StatusText);
            Assert.True(aliceChecks > checksAtLoss);
            await pipeline.StopAsync();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisappearanceRequiresVerifiedOwnerAndLinkedLogsBeforeClearingContext(bool checkMissingIdentityFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-disappearance-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var link = Path.Combine(alice, "logs");
        try
        {
            foreach (var path in new[] { alice, logsA, logsB }) Directory.CreateDirectory(path);
            Junction(link, logsA);
            var file = Path.Combine(logsA, "_Event.2026-10.txt");
            File.WriteAllText(file, "history\n");
            var provider = new WindowsDirectoryPhysicalIdentityProvider();
            var missingIdentity = false;
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"),
                new DelegateDirectoryPhysicalIdentityProvider(path => missingIdentity && path == alice ? null : provider.GetIdentity(path)));
            var candidate = new LogDiscoverySourceCandidate("Alice", alice, logsA,
                [new WurmLogFileLocation(WurmLogFileKind.Event, file, players, "Alice", logsA)], [], [], null, true, "", "")
            { PhysicalDirectoryKey = provider.GetIdentity(alice), LogicalLogsDirectoryPath = link,
                PhysicalLogsDirectoryKey = provider.GetIdentity(logsA) };
            var output = new ConcurrentQueue<EventPipelineOutput>();
            var vein = new VeinContextProcessor();
            long sequence = 0;
            var released = false;
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            await using var pipeline = new EventPipeline(players, item =>
            {
                output.Enqueue(item);
                if (item is EventReadBatch batch)
                    foreach (var line in batch.Lines)
                        vein.Process(new OrderedEventLine(batch.ProfileId, batch.ProfileName, ++sequence,
                            new EventLogLine(batch.ProfileId, batch.ProfileName, batch.CanonicalPath, line)));
                else if (item is EventContinuityLoss loss)
                    vein.Process(new StreamContinuityLost(loss.ProfileId, loss.ProfileName, ++sequence, loss.Reason, loss.CanonicalPath));
                return EventPipelineHandoffResult.Complete;
            }, new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: [candidate],
                releaseRetiredProfileAsync: (_, _) => { released = true; return Task.CompletedTask; });
            pipeline.Start();
            File.AppendAllText(file, "You start to gather fragments of the rock.\nYou would mine iron ore here.\nIt has a max quality of 12.5.\nYou will be able to mine here 3 more times.\nunfinished");
            pipeline.RunCycleForTest();
            var checkpoint = Assert.Single(pipeline.SourceCheckpoints);
            Assert.Equal("unfinished", checkpoint.PendingText);
            Assert.True(vein.TryGetActiveContext(checkpoint.ProfileId, out var originalContext));
            output.Clear();
            File.Move(file, file + ".away");
            if (checkMissingIdentityFirst)
            {
                missingIdentity = true;
                pipeline.RunCycleForTest();
                Assert.Empty(output);
                Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
                Assert.True(vein.TryGetActiveContext(checkpoint.ProfileId, out var firstRetained));
                Assert.Same(originalContext, firstRetained);
                missingIdentity = false;
            }
            Directory.Delete(link);
            Junction(link, logsB);
            pipeline.PublishDiscoverySnapshot(1, [candidate], [], false);
            pipeline.RunCycleForTest();
            Assert.Empty(output);
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            Assert.True(vein.TryGetActiveContext(checkpoint.ProfileId, out var retained));
            Assert.Same(originalContext, retained);
            Directory.Delete(link);
            Junction(link, logsA);
            File.Move(file + ".away", file);
            pipeline.PublishDiscoverySnapshot(2, [candidate], [], true);
            pipeline.RunCycleForTest();
            Assert.Empty(output);
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            File.Move(file, file + ".away");
            pipeline.RunCycleForTest();
            Assert.Equal(StreamGapReason.UnrecoverableReadGap, Assert.IsType<EventContinuityLoss>(Assert.Single(output)).Reason);
            Assert.False(vein.TryGetActiveContext(checkpoint.ProfileId, out _));
            File.WriteAllText(file, "replacement history\n");
            pipeline.RunCycleForTest();
            Assert.Equal(new FileInfo(file).Length, Assert.Single(pipeline.SourceCheckpoints).ReadOffset);
            File.AppendAllText(file, "live\n");
            pipeline.RunCycleForTest();
            Assert.Equal(["live"], Assert.Single(output.OfType<EventReadBatch>()).Lines);
            Assert.True(pipeline.TryRequestControlledRetirement(candidate.PhysicalDirectoryKey!, 1, 3, true));
            pipeline.RunCycleForTest();
            Assert.True(released);
            Assert.Empty(pipeline.SourceCheckpoints);
            await pipeline.StopAsync();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PendingDisappearanceRechecksLinkedLogsBeforeRetryingHandoff()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-disappearance-retry-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var link = Path.Combine(alice, "logs");
        try
        {
            foreach (var path in new[] { alice, logsA, logsB }) Directory.CreateDirectory(path);
            Junction(link, logsA);
            var file = Path.Combine(logsA, "_Event.2026-10.txt");
            File.WriteAllText(file, "history\n");
            var identities = new WindowsDirectoryPhysicalIdentityProvider();
            var candidate = new LogDiscoverySourceCandidate("Alice", alice, logsA,
                [new WurmLogFileLocation(WurmLogFileKind.Event, file, players, "Alice", logsA)], [], [], null, true, "", "")
            { PhysicalDirectoryKey = identities.GetIdentity(alice), LogicalLogsDirectoryPath = link,
                PhysicalLogsDirectoryKey = identities.GetIdentity(logsA) };
            var outputs = new ConcurrentQueue<EventPipelineOutput>();
            var attempts = 0;
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            await using var pipeline = new EventPipeline(players, item =>
            {
                if (item is EventContinuityLoss && ++attempts == 1)
                    return EventPipelineHandoffResult.NotStarted;
                outputs.Enqueue(item);
                return EventPipelineHandoffResult.Complete;
            }, new EventLogReader(), clock, profileIdentityResolver: resolver, sourceCandidates: [candidate]);
            pipeline.Start();
            var checkpoint = Assert.Single(pipeline.SourceCheckpoints);
            File.Move(file, file + ".away");
            pipeline.RunCycleForTest();
            Assert.Equal(1, attempts);
            Assert.Empty(outputs);
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            Directory.Delete(link);
            Junction(link, logsB);
            pipeline.RunCycleForTest();
            Assert.Equal(1, attempts);
            Assert.Empty(outputs);
            Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
            Directory.Delete(link);
            Junction(link, logsA);
            File.Move(file + ".away", file);
            pipeline.PublishDiscoverySnapshot(1, [candidate], [], true);
            pipeline.RunCycleForTest();
            Assert.Equal(2, attempts);
            Assert.IsType<EventContinuityLoss>(Assert.Single(outputs));
            pipeline.RunCycleForTest();
            Assert.Equal(new FileInfo(file).Length, Assert.Single(pipeline.SourceCheckpoints).ReadOffset);
            File.AppendAllText(file, "live\n");
            pipeline.RunCycleForTest();
            Assert.Equal(["live"], Assert.Single(outputs.OfType<EventReadBatch>()).Lines);
            await pipeline.StopAsync();
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ChangedOwnerStaysSuspendedAndOtherSourcesKeepTheirFifo()
    {
        var root = Path.Combine(Path.GetTempPath(), "wmc-transfer-" + Guid.NewGuid().ToString("N"));
        var players = Path.Combine(root, "players");
        var alice = Path.Combine(players, "Alice");
        var bob = Path.Combine(players, "Bob");
        var carol = Path.Combine(players, "Carol");
        var logsA = Path.Combine(root, "LogsA");
        var logsB = Path.Combine(root, "LogsB");
        var aliceOther = Path.Combine(alice, "other-logs");
        var aliceLink = Path.Combine(alice, "logs");
        var bobLink = Path.Combine(bob, "logs");
        try
        {
            foreach (var path in new[] { alice, bob, logsA, logsB, aliceOther, Path.Combine(carol, "logs") }) Directory.CreateDirectory(path);
            Junction(aliceLink, logsA);
            var shared = Path.Combine(logsA, "_Event.2026-10.txt");
            var independent = Path.Combine(carol, "logs", "_Event.2026-10.txt");
            var aliceIndependent = Path.Combine(aliceOther, "_Event.2026-10.txt");
            File.WriteAllText(shared, "history\n");
            File.WriteAllText(independent, "history\n");
            File.WriteAllText(aliceIndependent, "history\n");
            var provider = new WindowsDirectoryPhysicalIdentityProvider();
            LogDiscoverySourceCandidate Candidate(string name, string profile, string logical, string physical, string file) => new(
                name, profile, physical, [new WurmLogFileLocation(WurmLogFileKind.Event, file, players, name, physical)],
                [], [], null, true, "", "")
            { PhysicalDirectoryKey = provider.GetIdentity(profile), LogicalLogsDirectoryPath = logical,
                PhysicalLogsDirectoryKey = provider.GetIdentity(physical) };
            var original = Candidate("Alice", alice, aliceLink, logsA, shared);
            var carolCandidate = Candidate("Carol", carol, Path.Combine(carol, "logs"), Path.Combine(carol, "logs"), independent);
            var aliceCandidate = Candidate("Alice", alice, aliceOther, aliceOther, aliceIndependent);
            var resolver = new PlayerProfileIdentityResolver(Path.Combine(root, "registry"));
            var outputs = new ConcurrentQueue<EventPipelineOutput>();
            var processed = new ConcurrentQueue<string>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            using var context = new EventLineCoordinator();
            var dispatcher = new FastEventDispatcher(new MiningEventLane(line =>
            {
                if (line.Text == "alice")
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                if (line.ProfileName == "Alice") processed.Enqueue(line.Text);
            }), context);
            var clock = new ControlledEventRetirementTests.ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            var reader = new EventLogReader();
            await using var pipeline = new EventPipeline(players, output => { outputs.Enqueue(output); return dispatcher.Handoff(output); },
                reader, clock,
                profileIdentityResolver: resolver, sourceCandidates: [original, aliceCandidate, carolCandidate]);
            pipeline.Start();
            var old = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared);
            File.AppendAllText(shared, "alice\nalice second\n");
            pipeline.RunCycleForTest();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["alice", "alice second"], Assert.Single(outputs.OfType<EventReadBatch>()).Lines);
            old = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared);
            Directory.Delete(aliceLink);
            Junction(aliceLink, logsB);
            File.AppendAllText(shared, "uncertain\n");
            File.AppendAllText(independent, "carol\n");
            File.AppendAllText(aliceIndependent, "alice independent\n");
            pipeline.RunCycleForTest();
            Assert.Equal(old, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared));
            Assert.Equal(["carol"], Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == independent).Lines);
            Assert.Equal(["alice independent"], Assert.Single(outputs.OfType<EventReadBatch>(), item => item.CanonicalPath == aliceIndependent).Lines);
            Junction(bobLink, logsA);
            var replacement = Candidate("Bob", bob, bobLink, logsA, shared);
            pipeline.PublishDiscoverySnapshot(2, [replacement, aliceCandidate, carolCandidate], [], true);
            pipeline.AddSourceCandidates(2, [replacement, aliceCandidate, carolCandidate]);
            pipeline.RunCycleForTest();
            Assert.Equal(old, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared));
            pipeline.PublishDiscoverySnapshot(1, [original, carolCandidate], [], true);
            pipeline.PublishDiscoverySnapshot(3, [replacement, aliceCandidate, carolCandidate], [], false);
            pipeline.RunCycleForTest();
            pipeline.PublishDiscoverySnapshot(4, [replacement, aliceCandidate, carolCandidate], [], true);
            pipeline.PublishDiscoverySnapshot(2, [original, carolCandidate], [], true);
            clock.Advance(TimeSpan.FromSeconds(10));
            pipeline.RunCycleForTest();
            var late = Path.Combine(logsA, "_Event.2026-10-16.txt");
            File.WriteAllText(late, "unconfirmed\n");
            File.AppendAllText(shared, "bob\n");
            pipeline.DiscoverForTest();
            pipeline.RunCycleForTest();
            Assert.Equal(old, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == shared));
            Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == late);
            Assert.DoesNotContain(outputs.OfType<EventReadBatch>(), item => item.ProfileName == "Bob");
            await pipeline.StopAsync();
            release.Set();
            await dispatcher.CompleteAsync();
            Assert.Equal(["alice", "alice second", "alice independent"], processed);
        }
        finally
        {
            foreach (var link in new[] { aliceLink, bobLink }) if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Junction(string path, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", path, target },
            UseShellExecute = false, CreateNoWindow = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
