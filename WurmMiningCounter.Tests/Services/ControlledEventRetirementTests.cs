using System.Collections.Concurrent;
using WurmMiningCounter;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class ControlledEventRetirementTests
{
    [Fact]
    public async Task FileConflictWithoutCoveringDirectoryIsRetained()
    {
        using var files = new Fixture();
        var file = files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        var alice = Path.Combine(files.Players, "Alice");
        var logs = Path.GetDirectoryName(file)!;
        var external = Path.Combine(files.Players, "other-logs", "_Event.2026-10.txt");
        LogDiscoverySourceCandidate Candidate(string name, string profile, string key) => new(
            name, profile, logs, [new WurmLogFileLocation(WurmLogFileKind.Event, external, files.Players, name, logs)],
            [], [], null, true, "", "") { PhysicalDirectoryKey = key };
        await using var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(), (_, _) => Task.CompletedTask);
        pipeline.Start();
        var checkpoint = Assert.Single(pipeline.SourceCheckpoints);
        pipeline.PublishDiscoverySnapshot(1,
            [Candidate("Alice", alice, Fixture.AliceKey), Candidate("Bob", Path.Combine(files.Players, "Bob"), Fixture.BobKey)], [], false);
        pipeline.RunCycleForTest();
        var filesSet = (HashSet<string>)typeof(EventPipeline)
            .GetField("_conflictFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(pipeline)!;
        Assert.Contains(external, filesSet);
        Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints));
    }

    [Fact]
    public async Task RepeatedResolvedConflictsDoNotRetainExpiredFileHistory()
    {
        using var files = new Fixture();
        var paths = new List<string> { files.AddEvent("Alice", "_Event.2026-10-01.txt", "history\n") };
        var alice = Path.Combine(files.Players, "Alice");
        var logs = Path.Combine(alice, "logs");
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var identities = new WindowsDirectoryPhysicalIdentityProvider();
        await using var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(), (_, _) => Task.CompletedTask, timeProvider: clock);
        int Count(string name) => ((System.Collections.IEnumerable)typeof(EventPipeline)
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(pipeline)!).Cast<object>().Count();
        LogDiscoverySourceCandidate Candidate(string profile, string key) => new(
            Path.GetFileName(profile), profile, logs,
            paths.Select(file => new WurmLogFileLocation(WurmLogFileKind.Event, file, files.Players, Path.GetFileName(profile), logs)).ToArray(),
            [], [], null, true, "", "")
        { PhysicalDirectoryKey = key, PhysicalLogsDirectoryKey = identities.GetIdentity(logs), LogicalLogsDirectoryPath = logs };
        pipeline.Start();
        for (var day = 1; day <= 8; day++)
        {
            if (day > 1)
            {
                clock.Advance(TimeSpan.FromDays(1));
                paths.Add(files.AddEvent("Alice", $"_Event.2026-10-{day:00}.txt", "history\n"));
            }
            var owner = Candidate(alice, Fixture.AliceKey);
            var rival = Candidate(Path.Combine(files.Players, "Bob"), Fixture.BobKey);
            pipeline.PublishDiscoverySnapshot(day * 2 - 1, [owner, rival], [], false);
            pipeline.RunCycleForTest();
            pipeline.PublishDiscoverySnapshot(day * 2, [owner], [], true);
            pipeline.RunCycleForTest();
            pipeline.DiscoverForTest();
            pipeline.RunCycleForTest();
            Assert.Equal(1, Count("_conflictDirectories"));
            Assert.Equal(0, Count("_conflictFiles"));
            Assert.InRange(Count("_resumedConflictFiles"), 0, 1);
        }

        pipeline.PublishDiscoverySnapshot(17, [], [], true);
        Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 17, true));
        pipeline.RunCycleForTest();
        Assert.Empty(pipeline.SourceCheckpoints);
        Assert.Equal(0, Count("_resumedConflictFiles"));
        Assert.Equal(1, Count("_conflictDirectories"));
        for (var i = 0; i < 5; i++) pipeline.RunCycleForTest();
        Assert.Equal(0, Count("_resumedConflictFiles"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedConflictDoesNotBlockLaterConfirmedRetirement(bool uncertainAfterResolution)
    {
        using var files = new Fixture();
        var aliceFile = files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        var bobFile = files.AddEvent("Bob", "_Event.2026-10.txt", "history\n");
        var alice = Path.Combine(files.Players, "Alice");
        var bob = Path.Combine(files.Players, "Bob");
        LogDiscoverySourceCandidate Candidate(string name, string profile, string logs, string file, string key) => new(
            name, profile, logs, [new WurmLogFileLocation(WurmLogFileKind.Event, file, files.Players, name, logs)],
            [], [], null, true, "", "") { PhysicalDirectoryKey = key };
        var aliceCandidate = Candidate("Alice", alice, Path.GetDirectoryName(aliceFile)!, aliceFile, Fixture.AliceKey);
        var bobCandidate = Candidate("Bob", bob, Path.GetDirectoryName(bobFile)!, bobFile, Fixture.BobKey);
        var rival = Candidate("Bob", bob, Path.GetDirectoryName(aliceFile)!, aliceFile, Fixture.BobKey);
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var releaseStarted = new TaskCompletionSource<PlayerProfileId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = files.CreatePipeline(output, (id, _) =>
        {
            releaseStarted.TrySetResult(id);
            return releaseFinished.Task;
        });
        pipeline.Start();
        var checkpoint = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == aliceFile);
        pipeline.PublishDiscoverySnapshot(1, [aliceCandidate, rival, bobCandidate], [], false);
        pipeline.RunCycleForTest();
        File.AppendAllText(aliceFile, "while conflicted\n");
        pipeline.RunCycleForTest();
        Assert.Equal(checkpoint, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == aliceFile));
        Assert.False(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, false));

        pipeline.PublishDiscoverySnapshot(2, [aliceCandidate, bobCandidate], [], true);
        pipeline.RunCycleForTest();
        Assert.Equal(["while conflicted"], Assert.Single(output.OfType<EventReadBatch>(), item => item.CanonicalPath == aliceFile).Lines);
        if (uncertainAfterResolution)
            pipeline.PublishDiscoverySnapshot(3, [aliceCandidate, bobCandidate], [], false);
        pipeline.PublishDiscoverySnapshot(1, [aliceCandidate, rival, bobCandidate], [], false);
        File.AppendAllText(aliceFile, "after resolution\n");
        pipeline.RunCycleForTest();
        Assert.Equal(["while conflicted", "after resolution"], output.OfType<EventReadBatch>()
            .Where(item => item.CanonicalPath == aliceFile).SelectMany(item => item.Lines));
        Assert.False(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 3, false));

        pipeline.PublishDiscoverySnapshot(4, [aliceCandidate, rival, bobCandidate], [], false);
        pipeline.RunCycleForTest();
        var resumedCheckpoint = Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == aliceFile);
        File.AppendAllText(aliceFile, "new conflict\n");
        pipeline.RunCycleForTest();
        Assert.Equal(resumedCheckpoint, Assert.Single(pipeline.SourceCheckpoints, item => item.CanonicalPath == aliceFile));
        pipeline.PublishDiscoverySnapshot(5, [aliceCandidate, bobCandidate], [], true);
        pipeline.RunCycleForTest();
        Assert.Equal(["new conflict"], output.OfType<EventReadBatch>().Last(item => item.CanonicalPath == aliceFile).Lines);

        pipeline.PublishDiscoverySnapshot(6, [bobCandidate], [], true);
        Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 6, true));
        pipeline.RunCycleForTest();
        Assert.True(releaseStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(checkpoint.ProfileId, await releaseStarted.Task);
        Assert.DoesNotContain(pipeline.SourceCheckpoints, item => item.CanonicalPath == aliceFile);
        File.AppendAllText(bobFile, "independent\n");
        pipeline.RunCycleForTest();
        Assert.Contains(output.OfType<EventReadBatch>(), item => item.CanonicalPath == bobFile && item.Lines.Contains("independent"));
        releaseFinished.SetResult();
        pipeline.RunCycleForTest();
    }

    [Fact]
    public async Task MonthlySegmentRemainsActiveThroughYearBoundaryUntilGraceExpires()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-12.txt", "history\n");
        var clock = new ManualClock(new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero));
        await using var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(),
            (_, _) => Task.CompletedTask, timeProvider: clock);

        pipeline.Start();
        Assert.Single(pipeline.SourceCheckpoints);
        clock.Advance(TimeSpan.FromMinutes(5));
        pipeline.DiscoverForTest();
        pipeline.RunCycleForTest();
        Assert.Single(pipeline.SourceCheckpoints);
        clock.Advance(TimeSpan.FromMinutes(1));
        pipeline.DiscoverForTest();
        pipeline.RunCycleForTest();
        Assert.Empty(pipeline.SourceCheckpoints);
    }

    [Fact]
    public async Task RetirementRemovesAllPhysicalStatesWhileOtherProfileContinuesAndReappearanceBaselines()
    {
        using var files = new Fixture();
        var aliceMonthly = files.AddEvent("Alice", "_Event.2026-10.txt", "history-a\n");
        var aliceDaily = files.AddEvent("Alice", "_Event.2026-10-16.txt", "history-b\n");
        var bob = files.AddEvent("Bob", "_Event.2026-10.txt", "history-bob\n");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<EventPipelineOutput>();
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        foreach (var path in new[] { aliceMonthly, aliceDaily, bob })
        {
            File.SetCreationTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        }
        await using var pipeline = files.CreatePipeline(output,
            (id, _) => id == files.AliceId ? release.Task : Task.CompletedTask, timeProvider: clock);

        pipeline.Start();
        Assert.Equal(3, pipeline.SourceCheckpoints.Count);
        files.AliceId = pipeline.SourceCheckpoints.First(checkpoint => checkpoint.ProfileName == "Alice").ProfileId;
        Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, snapshotIsComplete: true));
        Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, snapshotIsComplete: true));
        Assert.False(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 2, 1, snapshotIsComplete: true));
        pipeline.RunCycleForTest();

        Assert.Single(pipeline.SourceCheckpoints);
        Assert.True(pipeline.TryPermitControlledReappearance(Fixture.AliceKey, 1, 2, 2));
        File.AppendAllText(bob, "bob-live\n");
        pipeline.RunCycleForTest();
        Assert.Contains(output.OfType<EventReadBatch>(), batch => batch.ProfileName == "Bob" && batch.Lines.Contains("bob-live"));

        release.TrySetResult();
        pipeline.RunCycleForTest();
        pipeline.DiscoverForTest();
        pipeline.RunCycleForTest();

        Assert.Equal(3, pipeline.SourceCheckpoints.Count);
        Assert.Empty(output.Where(item => item is EventReadBatch batch && batch.ProfileName == "Alice"));
    }

    [Fact]
    public async Task IncompleteConflictStaleAndFaultedRequestsRemainFailClosed()
    {
        using var files = new Fixture(duplicateAliceCandidate: true);
        files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        await using var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(), (_, _) => Task.FromException(new IOException("release failed")));

        pipeline.Start();
        Assert.False(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, snapshotIsComplete: false));
        Assert.False(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, snapshotIsComplete: true));
        Assert.False(pipeline.TryRequestControlledRetirement("missing", 1, 1, snapshotIsComplete: true));
        Assert.Single(pipeline.SourceCheckpoints);
    }

    [Fact]
    public async Task FaultedReleaseDoesNotPermitReappearanceAndIsObservedOnShutdown()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(),
            (_, _) => Task.FromException(new IOException("release failed")));
        try
        {
            pipeline.Start();
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 3, 5, true));
            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();
            Assert.False(pipeline.TryPermitControlledReappearance(Fixture.AliceKey, 3, 4, 6));
            await Assert.ThrowsAsync<IOException>(() => pipeline.StopAsync());
        }
        finally
        {
            try { await pipeline.DisposeAsync(); }
            catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedControlledReleaseReportsOneTerminalFaultAndKeepsHealthInError(bool canceled)
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        var health = new MonitoringHealthState(viewModel, action => action());
        var release = canceled
            ? Task.FromCanceled(new CancellationToken(canceled: true))
            : Task.FromException(new IOException("release failed"));
        var faults = new List<EventPipelineSourceFault>();
        var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(), (_, _) => release);
        try
        {
            App.WireEventPipelineUiHealth(pipeline, dispatcher, health);
            pipeline.TerminalSourceFaulted += faults.Add;
            pipeline.Start();
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));

            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();

            var fault = Assert.Single(faults);
            Assert.Equal("Alice", fault.ProfileName);
            Assert.Equal("Monitoring error", viewModel.StatusText);
            Assert.False(pipeline.TryPermitControlledReappearance(Fixture.AliceKey, 1, 2, 2));
            health.UpdateIdentity([]);
            Assert.Equal("Monitoring error", viewModel.StatusText);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); }
            catch (IOException) { }
            catch (TaskCanceledException) { }
            await dispatcher.CompleteAsync();
        }
    }

    [Fact]
    public async Task SuccessfulControlledReleaseDoesNotReportTerminalFaultOrChangeHealth()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        var health = new MonitoringHealthState(viewModel, action => action());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = 0;
        var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(), (_, _) => release.Task);
        try
        {
            App.WireEventPipelineUiHealth(pipeline, dispatcher, health);
            pipeline.TerminalSourceFaulted += _ => faults++;
            pipeline.Start();
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));
            pipeline.RunCycleForTest();
            release.SetResult();
            pipeline.RunCycleForTest();

            Assert.Equal(0, faults);
            Assert.Equal("Monitoring", viewModel.StatusText);
        }
        finally
        {
            await pipeline.DisposeAsync();
            await dispatcher.CompleteAsync();
        }
    }

    [Fact]
    public async Task NaturalReleaseDrainIsObservedBeforeControlledReappearanceCanRebind()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10-16.txt", "history\n");
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var naturalReleases = 0;
        var asyncReleaseCalls = 0;
        await using var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(), (_, _) =>
        {
            asyncReleaseCalls++;
            return drain.Task;
        }, (_, _) => naturalReleases++, clock);

        pipeline.Start();
        clock.Advance(TimeSpan.FromDays(1));
        pipeline.DiscoverForTest(); // Natural path starts release before the controlled intent arrives.

        Assert.Equal(1, naturalReleases);
        Assert.Equal(1, asyncReleaseCalls);
        Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, snapshotIsComplete: true));
        pipeline.RunCycleForTest();
        Assert.Equal(1, asyncReleaseCalls); // Controlled retirement observes, never starts another drain.

        files.AddEvent("Alice", "_Event.2026-10-17.txt", "history-after-reappearance\n");
        Assert.True(pipeline.TryPermitControlledReappearance(Fixture.AliceKey, 1, 2, 2));
        pipeline.DiscoverForTest();
        Assert.Empty(pipeline.SourceCheckpoints);

        drain.TrySetResult();
        pipeline.RunCycleForTest();
        pipeline.DiscoverForTest();
        pipeline.RunCycleForTest();

        Assert.Equal(1, asyncReleaseCalls);
        Assert.Single(pipeline.SourceCheckpoints);
    }

    [Fact]
    public async Task FaultedNaturalReleaseDrainRemainsFailClosed()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10-16.txt", "history\n");
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(),
            (_, _) => Task.FromException(new IOException("natural drain failed")), (_, _) => { }, clock);
        try
        {
            pipeline.Start();
            clock.Advance(TimeSpan.FromDays(1));
            pipeline.DiscoverForTest();
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));
            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();

            Assert.False(pipeline.TryPermitControlledReappearance(Fixture.AliceKey, 1, 2, 2));
            Assert.Empty(pipeline.SourceCheckpoints);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task FaultedObservedNaturalReleaseReportsOneTerminalFaultAndHealth()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10-16.txt", "history\n");
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        var health = new MonitoringHealthState(viewModel, action => action());
        var faults = 0;
        var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(),
            (_, _) => Task.FromException(new IOException("natural drain failed")), (_, _) => { }, clock);
        try
        {
            App.WireEventPipelineUiHealth(pipeline, dispatcher, health);
            pipeline.TerminalSourceFaulted += _ => faults++;
            pipeline.Start();
            clock.Advance(TimeSpan.FromDays(1));
            pipeline.DiscoverForTest();
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));

            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();

            Assert.Equal(1, faults);
            Assert.Equal("Monitoring error", viewModel.StatusText);
            Assert.False(pipeline.TryPermitControlledReappearance(Fixture.AliceKey, 1, 2, 2));
        }
        finally
        {
            try { await pipeline.DisposeAsync(); }
            catch (IOException) { }
            await dispatcher.CompleteAsync();
        }
    }

    [Fact]
    public async Task DispatcherDrainFaultIsReportedOnceWithoutASourceFaultDuplicate()
    {
        using var files = new Fixture();
        var path = files.AddEvent("Alice", FormattableString.Invariant($"_Event.{DateTime.Now:yyyy-MM}.txt"), "history\n");
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => throw new IOException("mining drain failed")), context);
        var pipeline = files.CreateDispatcherPipeline(dispatcher);
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        var health = new MonitoringHealthState(viewModel, action => action());
        var dispatcherFault = new TaskCompletionSource<EventModuleTerminalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherFaults = 0;
        var sourceFaults = new List<EventPipelineSourceFault>();
        dispatcher.TerminalStatusRecorded += status =>
        {
            Interlocked.Increment(ref dispatcherFaults);
            dispatcherFault.TrySetResult(status);
        };
        pipeline.TerminalSourceFaulted += sourceFaults.Add;
        App.WireEventPipelineUiHealth(pipeline, dispatcher, health);

        try
        {
            pipeline.Start();
            File.AppendAllText(path, "live\n");
            pipeline.RunCycleForTest();
            var moduleFault = await dispatcherFault.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));
            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();

            Assert.Equal(EventModule.Mining, moduleFault.Module);
            Assert.Equal(1, dispatcherFaults);
            Assert.Empty(sourceFaults);
            Assert.Equal("Monitoring error", viewModel.StatusText);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); } catch (AggregateException) { }
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task DistinctDispatcherDrainFaultsRemainDistinctTerminalReports()
    {
        using var files = new Fixture();
        var fileName = FormattableString.Invariant($"_Event.{DateTime.Now:yyyy-MM}.txt");
        var alicePath = files.AddEvent("Alice", fileName, "history\n");
        var bobPath = files.AddEvent("Bob", fileName, "history\n");
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => throw new IOException("mining drain failed")), context);
        var pipeline = files.CreateDispatcherPipeline(dispatcher);
        var dispatcherFaults = new ConcurrentBag<EventModuleTerminalStatus>();
        var firstDispatcherFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDispatcherFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceFaults = new List<EventPipelineSourceFault>();
        dispatcher.TerminalStatusRecorded += status =>
        {
            dispatcherFaults.Add(status);
            if (dispatcherFaults.Count == 1)
                firstDispatcherFault.TrySetResult();
            else if (dispatcherFaults.Count == 2)
                secondDispatcherFault.TrySetResult();
        };
        pipeline.TerminalSourceFaulted += sourceFaults.Add;

        try
        {
            pipeline.Start();
            File.AppendAllText(alicePath, "alice-live\n");
            pipeline.RunCycleForTest();
            await firstDispatcherFault.Task.WaitAsync(TimeSpan.FromSeconds(5));

            File.AppendAllText(bobPath, "bob-live\n");
            pipeline.RunCycleForTest();
            await secondDispatcherFault.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.BobKey, 1, 1, true));
            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();

            Assert.Equal(2, dispatcherFaults.Count);
            Assert.Equal(2, dispatcherFaults.Select(status => status.ProfileId).Distinct().Count());
            Assert.Empty(sourceFaults);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); } catch (AggregateException) { }
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task DispatcherTerminalFaultCacheRemainsBoundedAcrossReleaseAndReappearanceAndClearsOnDispose()
    {
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => throw new IOException("mining drain failed")), context);
        var pipeline = new EventPipeline(Path.GetTempPath(), dispatcher);
        TaskCompletionSource? terminalFault = null;
        dispatcher.TerminalStatusRecorded += _ => terminalFault?.TrySetResult();

        try
        {
            for (var generation = 1; generation <= 3; generation++)
            {
                terminalFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(new EventReadBatch(
                    EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", generation, ["live"])));
                await terminalFault.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(1, pipeline.DispatcherTerminalFaultCacheCountForTest);
                await Assert.ThrowsAsync<AggregateException>(() => dispatcher.ReleaseProfileAsync(
                    EventTransportTestProfileIds.Alice).WaitAsync(TimeSpan.FromSeconds(5)));
            }

            await pipeline.DisposeAsync();
            Assert.Equal(0, pipeline.DispatcherTerminalFaultCacheCountForTest);

            terminalFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(new EventReadBatch(
                EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 4, ["after-dispose"])));
            await terminalFault.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pipeline.DispatcherTerminalFaultCacheCountForTest);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); } catch (AggregateException) { }
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task DispatcherTerminalFaultCacheKeepsMiningAndContextStatusesIndependently()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(_ => throw new IOException("context drain failed")));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => throw new IOException("mining drain failed")), context);
        var pipeline = new EventPipeline(Path.GetTempPath(), dispatcher);
        var statuses = new ConcurrentBag<EventModuleTerminalStatus>();
        var bothStatuses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status =>
        {
            statuses.Add(status);
            if (statuses.Count == 2)
                bothStatuses.TrySetResult();
        };

        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(new EventReadBatch(
                EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 1, ["live"])));
            await bothStatuses.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([EventModule.Mining, EventModule.Context], statuses.Select(status => status.Module).Order().ToArray());
            Assert.Equal(2, pipeline.DispatcherTerminalFaultCacheCountForTest);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); } catch (AggregateException) { }
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (EventContextLaneFaultException) { }
        }
    }

    [Fact]
    public async Task TerminalFaultCallbackRacingDisposeCannotRepopulateTheCache()
    {
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => throw new IOException("mining drain failed")), context);
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallbackToFinish = new ManualResetEventSlim();
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += _ =>
        {
            callbackEntered.Set();
            allowCallbackToFinish.Wait();
            callbackFinished.TrySetResult();
        };
        var pipeline = new EventPipeline(Path.GetTempPath(), dispatcher);

        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(new EventReadBatch(
                EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 1, ["live"])));
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));

            await pipeline.DisposeAsync();
            Assert.Equal(0, pipeline.DispatcherTerminalFaultCacheCountForTest);

            allowCallbackToFinish.Set();
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pipeline.DispatcherTerminalFaultCacheCountForTest);
        }
        finally
        {
            allowCallbackToFinish.Set();
            try { await pipeline.DisposeAsync(); } catch (AggregateException) { }
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ClosedHealthIgnoresLateControlledReleaseFault()
    {
        using var files = new Fixture();
        files.AddEvent("Alice", "_Event.2026-10.txt", "history\n");
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        var health = new MonitoringHealthState(viewModel, action => action());
        var pipeline = files.CreatePipeline(new ConcurrentQueue<EventPipelineOutput>(),
            (_, _) => Task.FromException(new IOException("release failed")));
        try
        {
            App.WireEventPipelineUiHealth(pipeline, dispatcher, health);
            pipeline.Start();
            Assert.True(pipeline.TryRequestControlledRetirement(Fixture.AliceKey, 1, 1, true));
            health.Close();

            pipeline.RunCycleForTest();
            pipeline.RunCycleForTest();

            Assert.Equal("Monitoring", viewModel.StatusText);
        }
        finally
        {
            try { await pipeline.DisposeAsync(); }
            catch (IOException) { }
            await dispatcher.CompleteAsync();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string AliceKey = "physical:alice";
        internal const string BobKey = "physical:bob";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WurmControlledEventRetirement-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> _profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _duplicateAliceCandidate;

        public Fixture(bool duplicateAliceCandidate = false)
        {
            _duplicateAliceCandidate = duplicateAliceCandidate;
            Players = Path.Combine(_root, "players");
            Directory.CreateDirectory(Players);
            Resolver = new PlayerProfileIdentityResolver(Path.Combine(_root, "registry"),
                new DelegateDirectoryPhysicalIdentityProvider(path => _profiles.TryGetValue(path, out var key) ? key : "identity:" + path));
        }

        public string Players { get; }
        public PlayerProfileIdentityResolver Resolver { get; }
        public PlayerProfileId AliceId { get; set; }

        public string AddEvent(string name, string fileName, string content)
        {
            var profile = Path.Combine(Players, name);
            _profiles[profile] = name == "Alice" ? AliceKey : BobKey;
            var logs = Path.Combine(profile, "logs");
            Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public EventPipeline CreatePipeline(ConcurrentQueue<EventPipelineOutput> output,
            Func<PlayerProfileId, string, Task> release, Action<PlayerProfileId, string>? naturalRelease = null,
            TimeProvider? timeProvider = null)
        {
            var candidates = CreateCandidates();

            return new EventPipeline(Players, item =>
            {
                if (item is EventReadBatch batch && batch.ProfileName == "Alice")
                    AliceId = batch.ProfileId;
                output.Enqueue(item);
                return EventPipelineHandoffResult.Complete;
            }, new EventLogReader(), timeProvider ?? new ManualClock(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)), releaseProfileWithIdentity: naturalRelease,
                profileIdentityResolver: Resolver, sourceCandidates: candidates, releaseRetiredProfileAsync: release);
        }

        public EventPipeline CreateDispatcherPipeline(FastEventDispatcher dispatcher) => new(
            Players, dispatcher, Resolver, CreateCandidates());

        private List<LogDiscoverySourceCandidate> CreateCandidates()
        {
            var candidates = _profiles.Select(pair =>
            {
                var name = Path.GetFileName(pair.Key);
                var logs = Path.Combine(pair.Key, "logs");
                return new LogDiscoverySourceCandidate(name, pair.Key, logs, [], [], [], null, true,
                    Path.Combine(_root, $"console.{name}.log"), "test") { PhysicalDirectoryKey = pair.Value };
            }).ToList();
            if (_duplicateAliceCandidate)
                candidates.Add(candidates.Single(candidate => candidate.PhysicalDirectoryKey == AliceKey) with
                { ProfileDirectoryPath = Path.Combine(_root, "alias", "Alice") });
            return candidates;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed class Consumer(Action<OrderedEventItem> process) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) => process(item);
        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }
}
