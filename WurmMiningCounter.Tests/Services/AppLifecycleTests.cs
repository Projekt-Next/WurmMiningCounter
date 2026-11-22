using WurmMiningCounter;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.Services;

public sealed class AppLifecycleTests
{
    private static readonly PlayerProfileId DanaId = PlayerProfileId.CreateNew();
    private static readonly PlayerProfileId EveId = PlayerProfileId.CreateNew();

    private static PlayerProfileId Id(string profileName) => profileName switch
    {
        "Alice" => EventTransportTestProfileIds.Alice,
        "Bob" => EventTransportTestProfileIds.Bob,
        "Charlie" => EventTransportTestProfileIds.Charlie,
        "Dana" => DanaId,
        "Eve" => EveId,
        _ => throw new ArgumentOutOfRangeException(nameof(profileName))
    };

    private static string Name(PlayerProfileId profileId) => profileId switch
    {
        var id when id == EventTransportTestProfileIds.Alice => "Alice",
        var id when id == EventTransportTestProfileIds.Bob => "Bob",
        var id when id == EventTransportTestProfileIds.Charlie => "Charlie",
        var id when id == DanaId => "Dana",
        var id when id == EveId => "Eve",
        _ => throw new ArgumentOutOfRangeException(nameof(profileId))
    };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void StartupPipelineFailureIsContained()
    {
        var calls = 0;

        var started = App.TryRunStartupPipeline(() =>
        {
            calls++;
            throw new InvalidOperationException("Simulated startup failure.");
        });

        Assert.False(started);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StartupFailureInOneServicePreventsLaterStartupWorkWithoutThrowing()
    {
        var startedServices = new List<string>();

        var result = App.TryRunStartupPipeline(() =>
        {
            startedServices.Add("first");
            throw new IOException("Simulated service startup failure.");
#pragma warning disable CS0162
            startedServices.Add("later");
#pragma warning restore CS0162
        });

        Assert.False(result);
        Assert.Equal(["first"], startedServices);
    }

    [Fact]
    public void NormalStartupPipelineCompletes()
    {
        var started = false;

        var result = App.TryRunStartupPipeline(() => started = true);

        Assert.True(result);
        Assert.True(started);
    }

    [Fact]
    public void TerminalSourceFaultChangesUiFromMonitoringToError()
    {
        var viewModel = CreateMonitoringViewModel();

        App.ReportTerminalFaultUiHealth(viewModel, action => action());

        Assert.Equal("Monitoring error", viewModel.StatusText);
    }

    [Fact]
    public async Task TerminalMiningFaultChangesUiFromMonitoringToErrorWithoutClearingData()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        var viewModel = new MainViewModel(counter);
        viewModel.SetStatus("Monitoring");
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("Mining fault."));
        var dispatcher = new FastEventDispatcher(mining, context);
        await using var pipeline = new EventPipeline(Path.GetTempPath(), dispatcher);
        var published = new TaskCompletionSource<EventModuleTerminalStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status => published.TrySetResult(status);
        App.WireEventPipelineUiHealth(pipeline, dispatcher, viewModel, action => action());

        dispatcher.Handoff(Batch("Alice", "Mining line"));
        var status = await published.Task.WaitAsync(Timeout);

        Assert.Equal(EventModule.Mining, status.Module);
        Assert.Equal("Monitoring error", viewModel.StatusText);
        Assert.Equal(1, Assert.Single(viewModel.Profiles).TotalCount);
        try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
    }

    [Fact]
    public async Task TerminalFaultWinsOverLaterIdentityRecoveryFromRealSourceLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "WurmMiningCounter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profilePath = Path.Combine(root, "Alice");
            var resolver = new PlayerProfileIdentityResolver(
                Path.Combine(root, "identity"), new DelegateDirectoryPhysicalIdentityProvider(_ => "volume:alice"));
            var viewModel = CreateMonitoringViewModel();
            var health = new MonitoringHealthState(viewModel, action => action());
            var diagnostics = new RuntimeDiagnosticService();
            App.WireIdentityActivationUiHealth(resolver, health, diagnostics);
            using var context = new EventLineCoordinator();
            var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => throw new InvalidOperationException("Mining fault.")), context);
            await using var pipeline = new EventPipeline(root, dispatcher);
            App.WireEventPipelineUiHealth(pipeline, dispatcher, health);

            Assert.False(resolver.TryRegisterSource(profilePath, out _, out _));
            Assert.Equal("Monitoring identity error", viewModel.StatusText);

            dispatcher.Handoff(Batch("Alice", "fault"));
            await Assert.ThrowsAsync<AggregateException>(() =>
                dispatcher.CompleteAsync().WaitAsync(Timeout));
            Assert.Equal("Monitoring error", viewModel.StatusText);

            Directory.CreateDirectory(profilePath);
            Assert.True(resolver.TryRegisterSource(profilePath, out var owner, out var readGuard));
            using (owner)
            using (readGuard)
                owner!.MarkActivated();

            Assert.Equal("Monitoring error", viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void QueuedOlderWpfHealthCallbackCannotOverwriteTerminalFault()
    {
        var queued = new Queue<Action>();
        var viewModel = CreateMonitoringViewModel();
        var health = new MonitoringHealthState(viewModel, queued.Enqueue);
        health.UpdateIdentity([]);
        health.RecordTerminalFault();

        queued.Dequeue()();
        queued.Dequeue()();

        Assert.Equal("Monitoring error", viewModel.StatusText);
    }

    [Fact]
    public async Task TerminalContextFaultChangesUiFromMonitoringToError()
    {
        var viewModel = CreateMonitoringViewModel();
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new ThrowingConsumer());
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        await using var pipeline = new EventPipeline(Path.GetTempPath(), dispatcher);
        var published = new TaskCompletionSource<EventModuleTerminalStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status => published.TrySetResult(status);
        App.WireEventPipelineUiHealth(pipeline, dispatcher, viewModel, action => action());

        dispatcher.Handoff(Batch("Alice", "Context line"));
        var status = await published.Task.WaitAsync(Timeout);

        Assert.Equal(EventModule.Context, status.Module);
        Assert.Equal("Monitoring error", viewModel.StatusText);
        try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        try { context.Dispose(); } catch (EventContextLaneFaultException) { }
    }

    [Fact]
    public void TerminalFaultUiHealthDoesNotDependOnEnabledDiagnostics()
    {
        var diagnostics = new RuntimeDiagnosticService(enabled: false);
        var viewModel = CreateMonitoringViewModel();

        App.ReportTerminalFaultUiHealth(viewModel, action => action());

        Assert.False(diagnostics.Enabled);
        Assert.Equal("Monitoring error", viewModel.StatusText);
    }

    [Fact]
    public void HealthyMonitoringStatusRemainsNormalWithoutTerminalFault()
    {
        var viewModel = CreateMonitoringViewModel();

        Assert.Equal("Monitoring", viewModel.StatusText);
    }

    [Fact]
    public void ShutdownFailureDoesNotPreventLaterCleanup()
    {
        var cleanupOrder = new List<string>();

        App.RunShutdownActions(
            ("failing", () =>
            {
                cleanupOrder.Add("failing");
                throw new InvalidOperationException("Simulated cleanup failure.");
            }),
            ("later", () => cleanupOrder.Add("later")));

        Assert.Equal(["failing", "later"], cleanupOrder);
    }

    [Fact]
    public void ShutdownFailureDoesNotPreventAnyRemainingCleanup()
    {
        var cleanupOrder = new List<string>();

        App.RunShutdownActions(
            ("first", () => cleanupOrder.Add("first")),
            ("failing", () =>
            {
                cleanupOrder.Add("failing");
                throw new IOException("Simulated cleanup failure.");
            }),
            ("third", () => cleanupOrder.Add("third")),
            ("fourth", () => cleanupOrder.Add("fourth")));

        Assert.Equal(["first", "failing", "third", "fourth"], cleanupOrder);
    }

    [Fact]
    public void ShutdownRequestPreparesLifecycleBeforeInvokingApplicationShutdown()
    {
        var order = new List<string>();

        App.RequestShutdown(
            () => order.Add("prepare"),
            () => order.Add("shutdown"));

        Assert.Equal(["prepare", "shutdown"], order);
    }

    [Fact]
    public async Task ResetSessionProfilesIncludeNearbyOnlyProfileAfterRelease()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var nearby = new NearbyScanProcessor();
        var vein = new VeinContextProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        context.RegisterConsumer(nearby);
        var nearbyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nearby.NearbyScanChanged += (_, args) =>
        {
            if (args.ProfileId == EventTransportTestProfileIds.Alice && args.Snapshot is not null)
                nearbyObserved.TrySetResult();
        };
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "There is iron nearby."));
            await nearbyObserved.Task.WaitAsync(Timeout);
            await dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            Assert.Empty(dispatcher.GetKnownProfiles());
            Assert.Empty(counter.GetProfileSnapshots());
            Assert.Empty(inspirations.GetProfileSnapshots());
            Assert.Empty(vein.GetActiveContextSnapshots());
            Assert.Single(nearby.GetSnapshots());

            var profiles = await App.ResetEventSessionAsync(dispatcher, counter, inspirations, tracker, vein, nearby);
            Assert.Equal([EventTransportTestProfileIds.Alice], profiles);

            Assert.Empty(nearby.GetSnapshots());
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ResetSessionProfilesIncludePlainRockOnlyProfileAfterRelease()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var nearby = new NearbyScanProcessor();
        var vein = new VeinContextProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        context.RegisterConsumer(nearby);
        var plainRockObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plainRockCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vein.PlainRockProspectingResultChanged += (_, args) =>
        {
            if (args.ProfileName != "Alice") return;
            if (args.Result is null) plainRockCleared.TrySetResult();
            else plainRockObserved.TrySetResult();
        };
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        try
        {
            dispatcher.Handoff(Batch("Alice",
                "You start to gather fragments of the rock.",
                "You would mine stone shards here.",
                "It has a max quality of 12.5."));
            await plainRockObserved.Task.WaitAsync(Timeout);
            await dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            Assert.Empty(dispatcher.GetKnownProfiles());
            Assert.Empty(vein.GetActiveContextSnapshots());
            Assert.Equal([EventTransportTestProfileIds.Alice], vein.GetPlainRockProspectingResultProfiles());

            var profiles = await App.ResetEventSessionAsync(dispatcher, counter, inspirations, tracker, vein, nearby);
            Assert.Equal([EventTransportTestProfileIds.Alice], profiles);
            await plainRockCleared.Task.WaitAsync(Timeout);

            Assert.Empty(vein.GetPlainRockProspectingResultProfiles());
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ResetSessionProfilesUnionIncludesEveryDomainOnce()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        var inspirations = new MiningInspirationCounter();
        inspirations.Add(EveId, "Eve", MiningMaterial.Ore);
        var tracker = new MiningInspirationTracker(inspirations);
        var nearby = new NearbyScanProcessor();
        nearby.Process(Line("Bob", "There is iron nearby."));
        var vein = new VeinContextProcessor();
        vein.Process(Line("Charlie", "You start to gather fragments of the rock."));
        vein.Process(Line("Charlie", "You would mine stone shards here."));
        vein.Process(Line("Charlie", "It has a max quality of 12.5."));
        vein.Process(Line("Dana", "You start to gather fragments of the rock."));
        vein.Process(Line("Dana", "You would mine iron ore here."));
        vein.Process(Line("Dana", "It has a max quality of 12.5."));
        vein.Process(Line("Dana", "You will be able to mine here 1103 more times."));
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        context.RegisterConsumer(nearby);
        var resetCounts = new Dictionary<PlayerProfileId, int>();
        var mining = new MiningEventLane(_ => { }, resetProfile: profileId =>
        {
            resetCounts.TryGetValue(profileId, out var count);
            resetCounts[profileId] = count + 1;
            counter.ResetProfile(profileId);
            inspirations.ResetProfile(profileId);
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            var profiles = App.GetEventSessionProfiles(dispatcher, counter, inspirations, vein, nearby);
            Assert.Equal([EventTransportTestProfileIds.Alice, EventTransportTestProfileIds.Bob,
                EventTransportTestProfileIds.Charlie, DanaId, EveId], profiles.OrderBy(Name));

            var resetProfiles = await App.ResetEventSessionAsync(
                dispatcher, counter, inspirations, tracker, vein, nearby);

            Assert.Empty(resetCounts);
            Assert.Equal(profiles.OrderBy(Name), resetProfiles.OrderBy(Name));
            Assert.Empty(counter.GetProfileSnapshots());
            Assert.Empty(inspirations.GetProfileSnapshots());
            Assert.Empty(nearby.GetSnapshots());
            Assert.Empty(vein.GetActiveContextSnapshots());
            Assert.Empty(vein.GetPlainRockProspectingResultProfiles());
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ResetSessionProfilesAreEmptyWithoutStateAndDoNotCreateLanes()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var nearby = new NearbyScanProcessor();
        var vein = new VeinContextProcessor();
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(_ => { });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            var profiles = App.GetEventSessionProfiles(dispatcher, counter, inspirations, vein, nearby);
            Assert.Empty(profiles);
            await Task.WhenAll(profiles.Select(dispatcher.ResetProfileAsync));

            Assert.Equal(0, mining.ProfileQueueCount);
            Assert.Equal(0, context.ProfileQueueCount);
            Assert.Empty(dispatcher.GetKnownProfiles());
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ResetSessionProfilesResetActiveProfileOnlyOnceAcrossMultipleOwners()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var nearby = new NearbyScanProcessor();
        var vein = new VeinContextProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(vein);
        context.RegisterConsumer(nearby);
        var nearbyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nearby.NearbyScanChanged += (_, args) =>
        {
            if (args.ProfileId == EventTransportTestProfileIds.Alice && args.Snapshot is not null)
                nearbyObserved.TrySetResult();
        };
        var resetCount = 0;
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }, resetProfile: _ => resetCount++), context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "There is iron nearby."));
            await nearbyObserved.Task.WaitAsync(Timeout);
            vein.Process(Line("Alice", "You start to gather fragments of the rock."));
            vein.Process(Line("Alice", "You would mine stone shards here."));
            vein.Process(Line("Alice", "It has a max quality of 12.5."));

            var profiles = await App.ResetEventSessionAsync(dispatcher, counter, inspirations, tracker, vein, nearby);
            Assert.Equal([EventTransportTestProfileIds.Alice], profiles);

            Assert.Equal(1, resetCount);
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public void SkillOnlyResetBoundary_CapturesOnlyProfilesOutsideEventReset()
    {
        var baselines = new MiningStatisticsSessionBaselineStore();
        var profiles = new[]
        {
            Snapshot("Alice", new SkillStateSnapshot("Mining", 80m, null, 0m, 5)),
            Snapshot("Bob", new SkillStateSnapshot("Mining", 70m, null, 0m, 7))
        };

        var startedAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        App.CaptureSkillOnlyMiningTickResetBoundaries(baselines, profiles, [EventTransportTestProfileIds.Alice], startedAt, (_, _) => { });

        Assert.Equal(0, baselines.Get(EventTransportTestProfileIds.Alice).MiningTickBaseline);
        Assert.Equal(7, baselines.Get(EventTransportTestProfileIds.Bob).MiningTickBaseline);
        Assert.Equal(startedAt, baselines.Get(EventTransportTestProfileIds.Bob).SessionStartedAtUtc);
    }

    [Fact]
    public void SkillOnlyResetBoundaryFailure_IsReportedAndDoesNotBlockOtherProfiles()
    {
        var baselines = new MiningStatisticsSessionBaselineStore();
        var profiles = new[]
        {
            Snapshot("Alice", new SkillStateSnapshot("Mining", 80m, null, 0m, 5)),
            Snapshot("Bob", new SkillStateSnapshot("Mining", 70m, null, 0m, 7))
        };
        var warnings = new List<(PlayerProfileId ProfileId, Exception Exception)>();
        baselines.BaselineChanged += (_, args) =>
        {
            if (args.ProfileId == EventTransportTestProfileIds.Alice)
                throw new InvalidOperationException("Expected baseline notification failure.");
        };

        App.CaptureSkillOnlyMiningTickResetBoundaries(
            baselines,
            profiles,
            [],
            new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero),
            (profileId, exception) => warnings.Add((profileId, exception)));

        Assert.Equal(5, baselines.Get(EventTransportTestProfileIds.Alice).MiningTickBaseline);
        Assert.Equal(7, baselines.Get(EventTransportTestProfileIds.Bob).MiningTickBaseline);
        var warning = Assert.Single(warnings);
        Assert.Equal(EventTransportTestProfileIds.Alice, warning.ProfileId);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public void MiningTickResetBoundary_UsesZeroWhenMiningIsMissing()
    {
        var baselines = new MiningStatisticsSessionBaselineStore();
        baselines.CaptureMiningSessionBaseline(EventTransportTestProfileIds.Alice, 5, 0.005m, DateTimeOffset.UnixEpoch);
        var profiles = new[] { Snapshot("Alice", new SkillStateSnapshot("Prospecting", 10m, null, 0m, 3)) };

        var startedAt = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        App.CaptureMiningSessionBaseline(baselines, profiles, EventTransportTestProfileIds.Alice, startedAt);

        Assert.Equal(0, baselines.Get(EventTransportTestProfileIds.Alice).MiningTickBaseline);
        Assert.Equal(0m, baselines.Get(EventTransportTestProfileIds.Alice).MiningGainBaseline);
        Assert.Equal(startedAt, baselines.Get(EventTransportTestProfileIds.Alice).SessionStartedAtUtc);
    }

    [Fact]
    public void EventOwnedResetBoundary_CapturesCompleteMiningSessionBaseline()
    {
        var baselines = new MiningStatisticsSessionBaselineStore();
        var profiles = new[]
        {
            Snapshot("Alice", new SkillStateSnapshot("Mining", 80.012345m, 0.001m, 0.012345m, 5))
        };
        var startedAt = new DateTimeOffset(2026, 10, 8, 12, 34, 56, TimeSpan.Zero);

        App.CaptureMiningSessionBaseline(baselines, profiles, EventTransportTestProfileIds.Alice, startedAt);

        Assert.Equal(new MiningStatisticsSessionBaselines(5, 0.012345m, startedAt), baselines.Get(EventTransportTestProfileIds.Alice));
    }

    [Fact]
    public async Task ResetSessionAfterReleaseClearsMiningAndPendingInspirationThroughApplicationContract()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var vein = new VeinContextProcessor();
        var nearby = new NearbyScanProcessor();
        using var context = new EventLineCoordinator();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter, tracker);
        var mining = new MiningEventLane(processor);
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(Batch("Alice",
                "You mine some ore.",
                "You start to mine forwards.",
                "You have a moment of inspiration..."));
            await dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            Assert.True(counter.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Alice));

            await App.ResetEventSessionAsync(dispatcher, counter, inspirations, tracker, vein, nearby);

            Assert.False(counter.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Alice));
            Assert.False(inspirations.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Alice));
            processor.Process(new EventLogLine(
                EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", "You mine some ore."));
            Assert.Empty(inspirations.GetProfileSnapshots());
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ContextResetAfterReleaseDoesNotCreateEmptyNameLifecycleBeforeReregistration()
    {
        using var context = new EventLineCoordinator();
        var resetCount = 0;
        var mining = new MiningEventLane(_ => { }, resetProfile: _ => resetCount++);
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "first"));
            await dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            await context.ResetProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            Assert.Empty(dispatcher.GetKnownProfiles());

            dispatcher.Handoff(Batch("Alice", "second"));
            await dispatcher.ResetProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            Assert.Equal(1, resetCount);
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ResetSelectedSameNamedProfileDoesNotReachOtherProfile()
    {
        var first = PlayerProfileId.CreateNew();
        var second = PlayerProfileId.CreateNew();
        var resets = new List<PlayerProfileId>();
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(
            new MiningEventLane(_ => { }, resetProfile: profileId => resets.Add(profileId)), context);
        try
        {
            dispatcher.Handoff(new EventReadBatch(first, "Twin", "first.txt", 1, ["first"]));
            dispatcher.Handoff(new EventReadBatch(second, "Twin", "second.txt", 1, ["second"]));

            await dispatcher.ResetProfileAsync(first).WaitAsync(Timeout);

            Assert.Equal([first], resets);
            Assert.Contains(dispatcher.GetKnownProfiles(), profile => profile.Id == second && profile.ProfileName == "Twin");
        }
        finally { await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    private static MainViewModel CreateMonitoringViewModel()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.SetStatus("Monitoring");
        return viewModel;
    }

    private static EventReadBatch Batch(string profileName, params string[] lines) =>
        new(Id(profileName), profileName, profileName + ".txt", 1, lines);

    private static OrderedEventLine Line(string profileName, string text) =>
        new(Id(profileName), profileName, 1,
            new EventLogLine(Id(profileName), profileName, profileName + ".txt", text));

    private static LiveSkillProfileSnapshot Snapshot(string profileName, params SkillStateSnapshot[] skills) =>
        new(Id(profileName), profileName, null, LiveSkillSourceState.Unknown, skills);

    private sealed class ThrowingConsumer : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) =>
            throw new InvalidOperationException("Context fault.");

        public void Invalidate(PlayerProfileId profileId, string profileName)
        {
        }
    }
}
