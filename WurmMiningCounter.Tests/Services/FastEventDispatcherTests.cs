using System.Collections.Concurrent;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class FastEventDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static PlayerProfileId Id(string profile) => profile switch
    {
        "Alice" => EventTransportTestProfileIds.Alice,
        "Bob" => EventTransportTestProfileIds.Bob,
        "Charlie" => EventTransportTestProfileIds.Charlie,
        _ => EventTransportTestProfileIds.Default
    };

    private static EventReadBatch Batch(string profile, params string[] lines) =>
        new(Id(profile), profile, profile + ".txt", 7, lines);

    [Fact]
    public async Task HealthyHandoffDeliversWholeBatchesExactlyOnceInFifoOrder()
    {
        var miningSeen = new ConcurrentQueue<string>();
        var contextSeen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine line) contextSeen.Enqueue(line.Line.Text);
            if (item is StreamContinuityLost) contextSeen.Enqueue("continuity");
        }));
        var mining = new MiningEventLane(line => miningSeen.Enqueue(line.Text),
            _ => miningSeen.Enqueue("continuity"));
        var dispatcher = new FastEventDispatcher(mining, context);

        Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "A", "B")));
        Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(
            new EventContinuityLoss(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 8, StreamGapReason.Replaced)));
        Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(
            new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 8, ["C"])));
        Assert.Equal(1, mining.ProfileQueueCount);
        Assert.Equal(1, context.ProfileQueueCount);
        Assert.Single(dispatcher.GetKnownProfiles());
        await dispatcher.CompleteAsync().WaitAsync(Timeout);
        Assert.Equal(["A", "B", "continuity", "C"], miningSeen);
        Assert.Equal(["A", "B", "continuity", "C"], contextSeen);
        Assert.Empty(dispatcher.GetTerminalStatuses());
    }

    [Fact]
    public async Task CompletedProfileFaultHistoryIsBoundedPerModuleAcrossManyReappearances()
    {
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("terminal"));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            for (var generation = 1; generation <= 1000; generation++)
            {
                Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(
                    new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", generation, ["fault"])));
                await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
                await Assert.ThrowsAsync<AggregateException>(() => dispatcher.ReleaseProfileAsync(
                    EventTransportTestProfileIds.Alice).WaitAsync(Timeout));
            }

            Assert.Equal((33, 967L, 0, 0L), dispatcher.TerminalFaultHistoryCountsForTest);
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetUsesMiningAdmissionWithoutResettingContext()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter, new MiningInspirationTracker(inspirations));
        var mining = new MiningEventLane(processor);
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice",
                "You start to mine forwards.", "You have a moment of inspiration...", "You mine some ore.")));
            await dispatcher.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            Assert.False(inspirations.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Alice));
            Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
            Assert.Equal(1, context.ProfileQueueCount);
            await dispatcher.CompleteAsync().WaitAsync(Timeout);
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetIsRejectedAfterMiningFault()
    {
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("fault"));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "fault")));
            await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            await Assert.ThrowsAsync<MiningLaneFaultException>(
                () => dispatcher.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetIsRejectedWhileProfileReleaseIsInProgress()
    {
        using var releaseStarted = new ManualResetEventSlim();
        using var allowRelease = new ManualResetEventSlim();
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(
            new MiningEventLane(_ => { }),
            context,
            afterMiningAdmission: null,
            beforeProfileReleaseInitiation: () =>
            {
                releaseStarted.Set();
                allowRelease.Wait();
            });
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "before-release")));
            var release = Task.Run(() => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice));
            Assert.True(releaseStarted.Wait(Timeout));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dispatcher.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));

            allowRelease.Set();
            await release.WaitAsync(Timeout);
            await dispatcher.CompleteAsync().WaitAsync(Timeout);
        }
        finally
        {
            allowRelease.Set();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task MiningInspirationResetIsRejectedAfterDispatcherCompletion()
    {
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        await dispatcher.CompleteAsync().WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(EventModule.Mining)]
    [InlineData(EventModule.Context)]
    public async Task WorkerFaultLeavesOtherModuleAndOtherProfileRunning(EventModule faulting)
    {
        var miningSeen = new ConcurrentQueue<string>();
        var contextSeen = new ConcurrentQueue<string>();
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is not OrderedEventLine line) return;
            if (faulting == EventModule.Context && line.ProfileName == "Alice") throw new InvalidOperationException("Context fault");
            contextSeen.Enqueue(line.Line.Text);
        }));
        var mining = new MiningEventLane(line =>
        {
            if (faulting == EventModule.Mining && line.ProfileName == "Alice") throw new InvalidOperationException("Mining fault");
            miningSeen.Enqueue(line.Text);
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "fault")));
            if (faulting == EventModule.Mining) await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            else await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(
                new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 8, ["later"])));
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Bob", "healthy")));
            var status = Assert.Single(dispatcher.GetTerminalStatuses());
            Assert.Equal(faulting, status.Module);
            Assert.Equal("Alice", status.ProfileName);
            Assert.Equal("Alice.txt", status.CanonicalPath);
            Assert.Equal(7, status.SourceGeneration);
            await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
            Assert.Contains("later", faulting == EventModule.Mining ? contextSeen : miningSeen);
            Assert.Contains("healthy", miningSeen);
            Assert.Contains("healthy", contextSeen);
        }
        finally { try { context.Dispose(); } catch (EventContextLaneFaultException) { } }
    }

    [Theory]
    [InlineData(EventModule.Mining)]
    [InlineData(EventModule.Context)]
    public async Task FullModuleQueueIsTerminalWhileHealthyModuleKeepsReceiving(EventModule blocked)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyLines = new ConcurrentQueue<string>();
        using var healthyProcessed = new SemaphoreSlim(0);
        var context = new EventLineCoordinator();
        void Process(EventModule module, string text)
        {
            if (module == blocked && text == "active")
            {
                entered.TrySetResult();
                release.Wait();
            }
            if (module != blocked)
            {
                healthyLines.Enqueue(text);
                healthyProcessed.Release();
            }
        }
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine line) Process(EventModule.Context, line.Line.Text);
        }));
        var mining = new MiningEventLane(line => Process(EventModule.Mining, line.Text));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "active"));
            await entered.Task.WaitAsync(Timeout);
            await healthyProcessed.WaitAsync().WaitAsync(Timeout);
            for (var index = 0; index < 64; index++)
            {
                Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", index.ToString())));
                await healthyProcessed.WaitAsync().WaitAsync(Timeout);
            }
            Assert.Equal(64, blocked == EventModule.Mining
                ? mining.GetMetrics(EventTransportTestProfileIds.Alice).QueueDepth
                : context.GetQueueMetrics(EventTransportTestProfileIds.Alice).QueueDepth);
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "overflow")));
            await healthyProcessed.WaitAsync().WaitAsync(Timeout);
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "later")));
            await healthyProcessed.WaitAsync().WaitAsync(Timeout);
            Assert.Equal(blocked, Assert.Single(dispatcher.GetTerminalStatuses()).Module);
            Assert.Contains("overflow", healthyLines);
            Assert.Contains("later", healthyLines);
            release.Set();
            await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
        }
        finally
        {
            release.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
            try { context.Dispose(); } catch (EventContextLaneFaultException) { }
        }
    }

    [Fact]
    public async Task ClosedDispatcherReturnsNotStartedWithoutCreatingLanes()
    {
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(_ => { });
        var dispatcher = new FastEventDispatcher(mining, context);
        await dispatcher.CompleteAsync();
        Assert.Equal(EventPipelineHandoffResult.NotStarted, dispatcher.Handoff(Batch("Alice", "A")));
        Assert.Equal(0, mining.ProfileQueueCount);
        Assert.Equal(0, context.ProfileQueueCount);
    }

    [Fact]
    public async Task UnexpectedFailureAfterFirstAdmissionReturnsIndeterminateAndCompletionFault()
    {
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(line => seen.Enqueue(line.Text));
        var dispatcher = new FastEventDispatcher(mining, context,
            () => throw new InvalidOperationException("Unexpected interrupted handoff"));
        Assert.Equal(EventPipelineHandoffResult.Indeterminate, dispatcher.Handoff(Batch("Alice", "A")));
        Assert.Equal(EventPipelineHandoffResult.NotStarted, dispatcher.Handoff(Batch("Alice", "A")));
        await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
        Assert.Equal(["A"], seen);
        Assert.Equal(0, context.ProfileQueueCount);
    }

    [Fact]
    public async Task LocalResetPreservesBothFifosWithoutCrossWorkerRendezvous()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var miningEvents = new ConcurrentQueue<string>();
        var contextEvents = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine line) contextEvents.Enqueue(line.Line.Text);
            if (item is ResetProfileBarrier)
            {
                contextEvents.Enqueue("reset");
                contextReset.TrySetResult();
            }
        }));
        var mining = new MiningEventLane(line =>
        {
            if (line.Text == "before") { entered.TrySetResult(); release.Wait(); }
            miningEvents.Enqueue(line.Text);
        }, resetProfile: _ => miningEvents.Enqueue("reset"));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "before"));
            await entered.Task.WaitAsync(Timeout);
            var reset = dispatcher.ResetProfileAsync(EventTransportTestProfileIds.Alice);
            Assert.False(reset.IsCompleted);
            await contextReset.Task.WaitAsync(Timeout); // Context resets while Mining is blocked
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "after")));
            release.Set();
            await reset.WaitAsync(Timeout);
            await dispatcher.CompleteAsync().WaitAsync(Timeout);
            Assert.Equal(["before", "reset", "after"], miningEvents);
            Assert.Equal(["before", "reset", "after"], contextEvents);
        }
        finally { release.Set(); await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task MarkerOnlyContextProfileIsOwnedReleasedAndCanCreateAFreshLane()
    {
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventMarker marker)
                seen.Enqueue(marker.Kind);
        }));
        var mining = new MiningEventLane(_ => { });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("first")).WaitAsync(Timeout);
            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Bob, "Bob", Marker("bob")).WaitAsync(Timeout);
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));

            await dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            Assert.False(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Bob));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Bob));

            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("fresh")).WaitAsync(Timeout);
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));
            await dispatcher.CompleteAsync().WaitAsync(Timeout);
            Assert.Equal(["first", "bob", "fresh"], seen);
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task MarkerOnlyCreationRegistersOneLifecycleProfileAndProcessesTheMarkerOnce()
    {
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventMarker marker)
                seen.Enqueue(marker.Kind);
        }));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        try
        {
            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("first")).WaitAsync(Timeout);
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));
            Assert.Equal(["Alice"], dispatcher.GetKnownProfiles().Select(profile => profile.ProfileName));
            Assert.Equal(["first"], seen);

            await dispatcher.CompleteAsync().WaitAsync(Timeout);
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerCreationAndHandoffCannotInvertContextAndDispatcherLocks(bool sameProfile)
    {
        using var allowMarkerRegistration = new ManualResetEventSlim();
        using var allowHandoffContextAdmission = new ManualResetEventSlim();
        var markerRegistrationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoffHoldingDispatcher = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var markerProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoffProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventMarker marker)
            {
                seen.Enqueue($"marker:{marker.ProfileName}:{marker.Kind}");
                if (marker.ProfileName == "Alice" && marker.Kind == "marker")
                    markerProcessed.TrySetResult();
            }
            else if (item is OrderedEventLine line)
            {
                seen.Enqueue($"line:{line.ProfileName}:{line.Line.Text}");
                if (line.Line.Text == "output")
                    handoffProcessed.TrySetResult();
            }
        }));
        context.BeforeProfileLaneRegistration = profileName =>
        {
            if (profileName != "Alice")
                return;

            markerRegistrationReached.TrySetResult();
            allowMarkerRegistration.Wait();
        };
        var mining = new MiningEventLane(_ => { });
        var dispatcher = new FastEventDispatcher(mining, context, () =>
        {
            handoffHoldingDispatcher.TrySetResult();
            allowHandoffContextAdmission.Wait();
        });
        var handoffProfile = sameProfile ? "Alice" : "Bob";

        try
        {
            var handoff = Task.Run(() => dispatcher.Handoff(Batch(handoffProfile, "output")));
            await handoffHoldingDispatcher.Task.WaitAsync(Timeout);

            var marker = Task.Run(() => context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("marker")));
            await markerRegistrationReached.Task.WaitAsync(Timeout);

            // On the old lock order this handoff held dispatcher _sync while
            // waiting for Context admission, while marker registration held
            // Context admission and waited for dispatcher _sync.
            allowHandoffContextAdmission.Set();
            Assert.Equal(EventPipelineHandoffResult.Complete, await handoff.WaitAsync(Timeout));

            allowMarkerRegistration.Set();
            await marker.WaitAsync(Timeout);
            await markerProcessed.Task.WaitAsync(Timeout);
            await handoffProcessed.Task.WaitAsync(Timeout);

            Assert.Equal(sameProfile ? 1 : 2, context.ProfileQueueCount);
            Assert.Equal(sameProfile ? 1 : 2, dispatcher.GetKnownProfiles().Count);
            Assert.Equal(1, seen.Count(entry => entry == "marker:Alice:marker"));
            Assert.Equal(1, seen.Count(entry => entry == $"line:{handoffProfile}:output"));

            await dispatcher.CompleteAsync().WaitAsync(Timeout);
        }
        finally
        {
            allowHandoffContextAdmission.Set();
            allowMarkerRegistration.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task MarkerCreationRacingReleaseCannotLeaveTheOldContextLaneAlive()
    {
        using var release = new ManualResetEventSlim();
        var markerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventMarker { Kind: "active" })
            {
                markerEntered.TrySetResult();
                release.Wait();
            }
        }));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        try
        {
            var marker = context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("active"));
            await markerEntered.Task.WaitAsync(Timeout);
            var drain = dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice);
            Assert.False(drain.IsCompleted);
            Assert.Equal(EventPipelineHandoffResult.NotStarted, dispatcher.Handoff(Batch("Alice", "stale")));

            release.Set();
            await marker.WaitAsync(Timeout);
            await drain.WaitAsync(Timeout);
            Assert.False(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));

            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("fresh")).WaitAsync(Timeout);
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));
        }
        finally
        {
            release.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ActiveReleaseRejectsMarkerAdmissionUntilTheProfileCanBeFreshlyCreated()
    {
        using var allowMiningDrain = new ManualResetEventSlim();
        var miningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextLaneReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.ProfileLaneReleased = profileName =>
        {
            if (profileName == "Alice")
                contextLaneReleased.TrySetResult();
        };
        context.RegisterConsumer(new Consumer(item =>
        {
            switch (item)
            {
                case OrderedEventMarker marker:
                    seen.Enqueue($"marker:{marker.ProfileName}:{marker.Kind}");
                    break;
                case OrderedEventLine line:
                    seen.Enqueue($"line:{line.ProfileName}:{line.Line.Text}");
                    break;
            }
        }));
        var mining = new MiningEventLane(line =>
        {
            if (line.ProfileName == "Alice" && line.Text == "active")
            {
                miningEntered.TrySetResult();
                allowMiningDrain.Wait();
            }
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "active")));
            await miningEntered.Task.WaitAsync(Timeout);

            var release = dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice);
            await contextLaneReleased.Task.WaitAsync(Timeout);
            Assert.False(release.IsCompleted);
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));
            Assert.False(context.HasProfileQueue(EventTransportTestProfileIds.Alice));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("rejected")));
            Assert.False(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.Equal(EventPipelineHandoffResult.NotStarted, dispatcher.Handoff(Batch("Alice", "stale")));

            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Bob, "Bob", Marker("bob")).WaitAsync(Timeout);
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Bob", "output")));
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Bob));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Bob));

            allowMiningDrain.Set();
            await release.WaitAsync(Timeout);
            Assert.False(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));

            await context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", Marker("fresh")).WaitAsync(Timeout);
            Assert.True(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.True(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));
            Assert.Equal(1, seen.Count(entry => entry == "marker:Alice:fresh"));

            await dispatcher.CompleteAsync().WaitAsync(Timeout);
        }
        finally
        {
            allowMiningDrain.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ReleaseRegisteredBeforeContextCompletionStillSettlesWhenContextAdmissionIsClosed()
    {
        var releaseRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueRelease = new ManualResetEventSlim();
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context, null, () =>
        {
            releaseRegistered.TrySetResult();
            continueRelease.Wait();
        });
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "active")));

            var release = Task.Run(() => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice));
            await releaseRegistered.Task.WaitAsync(Timeout);

            var completion = dispatcher.CompleteAsync();
            continueRelease.Set();

            await Assert.ThrowsAnyAsync<Exception>(() => release.WaitAsync(Timeout));
            await Assert.ThrowsAnyAsync<Exception>(() => completion.WaitAsync(Timeout));
            Assert.True(release.IsCompleted);
            Assert.True(completion.IsCompleted);
        }
        finally
        {
            continueRelease.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ContextReleaseInitiationFailureStillDrainsMiningBeforeReleaseFaults()
    {
        using var allowMiningDrain = new ManualResetEventSlim();
        var miningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueRelease = new ManualResetEventSlim();
        var releaseInitiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(line =>
        {
            if (line.ProfileName == "Alice")
            {
                miningEntered.TrySetResult();
                allowMiningDrain.Wait();
            }
        });
        var dispatcher = new FastEventDispatcher(mining, context, null, () =>
        {
            releaseRegistered.TrySetResult();
            continueRelease.Wait();
        });
        dispatcher.ProfileReleaseStarted += profileName =>
        {
            if (profileName == "Alice")
                releaseInitiated.TrySetResult();
        };
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "active")));
            await miningEntered.Task.WaitAsync(Timeout);

            var release = Task.Run(() => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice));
            await releaseRegistered.Task.WaitAsync(Timeout);
            var completion = dispatcher.CompleteAsync();
            continueRelease.Set();
            await releaseInitiated.Task.WaitAsync(Timeout);

            Assert.False(release.IsCompleted);
            allowMiningDrain.Set();

            await Assert.ThrowsAnyAsync<Exception>(() => release.WaitAsync(Timeout));
            await Assert.ThrowsAnyAsync<Exception>(() => completion.WaitAsync(Timeout));
            Assert.False(mining.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.True(release.IsCompleted);
            Assert.True(completion.IsCompleted);
        }
        finally
        {
            continueRelease.Set();
            allowMiningDrain.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
        }
    }

    [Theory]
    [InlineData(EventModule.Mining)]
    [InlineData(EventModule.Context)]
    public async Task ReleaseWaitsForBothOldLanesAndDoesNotBlockBob(EventModule blocked)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bob = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new EventLineCoordinator();
        var seen = new ConcurrentQueue<string>();
        void Process(EventModule module, string profile, string text)
        {
            if (module == blocked && text == "active") { entered.TrySetResult(); release.Wait(); }
            if (module == EventModule.Context) seen.Enqueue(text);
            if (profile == "Bob") bob.TrySetResult();
        }
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine line) Process(EventModule.Context, line.ProfileName, line.Line.Text);
        }));
        var mining = new MiningEventLane(line => Process(EventModule.Mining, line.ProfileName, line.Text));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "active"));
            await entered.Task.WaitAsync(Timeout);
            var drain = dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice);
            Assert.Same(drain, dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice));
            Assert.Equal(EventPipelineHandoffResult.NotStarted, dispatcher.Handoff(Batch("Alice", "stale")));
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Bob", "B")));
            await bob.Task.WaitAsync(Timeout);
            Assert.False(drain.IsCompleted);
            release.Set();
            await drain.WaitAsync(Timeout);
            Assert.False(mining.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.False(context.HasProfileQueue(EventTransportTestProfileIds.Alice));
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "fresh")));
            await dispatcher.CompleteAsync().WaitAsync(Timeout);
            Assert.DoesNotContain("stale", seen);
            Assert.Contains("fresh", seen);
        }
        finally { release.Set(); await dispatcher.CompleteAsync().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task CompletionRetainsMiningFaultsFromMultipleReleasedProfiles()
    {
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(line => throw new InvalidOperationException(line.ProfileName));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            foreach (var profile in new[] { "Alice", "Bob" })
            {
                Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch(profile, "fault")));
                await mining.GetProfileFaulted(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout);
                await Assert.ThrowsAsync<AggregateException>(
                    () => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout));
                Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.For(profile)));
            }

            var failure = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
            Assert.Equal(["Alice", "Bob"], failure.Flatten().InnerExceptions
                .OfType<MiningLaneFaultException>().Select(fault => fault.ProfileName).Order());
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task CompletionRetainsContextFaultsFromMultipleReleasedProfiles()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine line)
                throw new InvalidOperationException(line.ProfileName);
        }));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        try
        {
            foreach (var profile in new[] { "Alice", "Bob" })
            {
                Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch(profile, "fault")));
                await context.GetProfileFaulted(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout);
                await Assert.ThrowsAsync<AggregateException>(
                    () => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout));
                Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.For(profile)));
            }

            var failure = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
            Assert.Equal(["Alice", "Bob"], failure.Flatten().InnerExceptions
                .OfType<EventContextLaneFaultException>().Select(fault => fault.ProfileName).Order());
        }
        finally
        {
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task CompletionRetainsMixedReleasedModuleFaultsWithoutDuplicates()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine { ProfileName: "Bob" })
                throw new InvalidOperationException("Bob Context");
        }));
        var mining = new MiningEventLane(line =>
        {
            if (line.ProfileName is "Alice" or "Charlie")
                throw new InvalidOperationException($"{line.ProfileName} Mining");
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            foreach (var profile in new[] { "Alice", "Bob", "Charlie" })
            {
                Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch(profile, "fault")));
                if (profile == "Bob")
                    await context.GetProfileFaulted(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout);
                else
                    await mining.GetProfileFaulted(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout);
                await Assert.ThrowsAsync<AggregateException>(
                    () => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout));
            }

            var failure = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
            var faults = failure.Flatten().InnerExceptions;
            Assert.Equal(["Alice", "Charlie"], faults.OfType<MiningLaneFaultException>()
                .Select(fault => fault.ProfileName).Order());
            Assert.Equal(["Bob"], faults.OfType<EventContextLaneFaultException>()
                .Select(fault => fault.ProfileName));
            Assert.Equal(3, faults.Count(fault => fault is MiningLaneFaultException or EventContextLaneFaultException));
        }
        finally
        {
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task CompletionRetainsBothModuleFaultsFromOneReleasedProfileAfterReleaseIsObserved()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is OrderedEventLine)
                throw new InvalidOperationException("Context");
        }));
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("Mining"));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "fault")));
            await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            await Assert.ThrowsAsync<AggregateException>(
                () => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));
            Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.Alice));

            var failure = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
            var faults = failure.Flatten().InnerExceptions;
            Assert.Single(faults.OfType<MiningLaneFaultException>());
            Assert.Single(faults.OfType<EventContextLaneFaultException>());
        }
        finally
        {
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task SuccessfulReleasedProfilesDoNotAddCompletionFailures()
    {
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        foreach (var profile in new[] { "Alice", "Bob", "Charlie" })
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch(profile, "healthy")));
            await dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.For(profile)).WaitAsync(Timeout);
            Assert.False(dispatcher.HasProfileLifecycle(EventTransportTestProfileIds.For(profile)));
        }

        await dispatcher.CompleteAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task CompletionPreservesFaultsFromBothModules()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(_ => throw new ArgumentException("Context")));
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("Mining"));
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(Batch("Alice", "A"));
            var failure = await Assert.ThrowsAsync<AggregateException>(() => dispatcher.CompleteAsync().WaitAsync(Timeout));
            Assert.Contains(failure.InnerExceptions, error => error is MiningLaneFaultException);
            Assert.Contains(failure.InnerExceptions, error => error is EventContextLaneFaultException);
        }
        finally { try { context.Dispose(); } catch (EventContextLaneFaultException) { } }
    }

    [Fact]
    public async Task TerminalModuleStatusAndProfileReleaseArePublishedWithoutChangingHandoff()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(_ => throw new InvalidOperationException("Context")));
        var mining = new MiningEventLane(_ => { });
        var dispatcher = new FastEventDispatcher(mining, context);
        var statuses = new ConcurrentQueue<EventModuleTerminalStatus>();
        var releases = new ConcurrentQueue<string>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status =>
        {
            statuses.Enqueue(status);
            published.TrySetResult();
        };
        dispatcher.ProfileReleaseStarted += releases.Enqueue;
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "A")));
            await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            await published.Task.WaitAsync(Timeout);
            var first = Assert.Single(statuses);
            Assert.Equal(EventModule.Context, first.Module);
            Assert.Same(first, Assert.Single(dispatcher.GetTerminalStatuses()));

            _ = dispatcher.GetTerminalStatuses(); // RefreshFaults must not re-notify.
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "B")));
            Assert.Same(first, Assert.Single(statuses));
            Assert.Same(first, Assert.Single(dispatcher.GetTerminalStatuses()));

            await Assert.ThrowsAsync<AggregateException>(
                () => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));
            Assert.Equal(["Alice"], releases);
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task MiningTerminalStatusIsPublishedOnceAcrossRefreshAndLaterHandoff()
    {
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(_ => throw new InvalidOperationException("Mining"));
        var dispatcher = new FastEventDispatcher(mining, context);
        var statuses = new ConcurrentQueue<EventModuleTerminalStatus>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status =>
        {
            statuses.Enqueue(status);
            published.TrySetResult();
        };
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "A")));
            await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            await published.Task.WaitAsync(Timeout);
            var first = Assert.Single(statuses);
            Assert.Equal(EventModule.Mining, first.Module);

            _ = dispatcher.GetTerminalStatuses();
            Assert.Equal(EventPipelineHandoffResult.Complete, dispatcher.Handoff(Batch("Alice", "B")));
            Assert.Same(first, Assert.Single(statuses));
            Assert.Same(first, Assert.Single(dispatcher.GetTerminalStatuses()));
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    [Fact]
    public async Task FreshGenerationPublishesItsOwnFirstTerminalStatusAfterRelease()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(_ => throw new InvalidOperationException("Context")));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        var statuses = new ConcurrentQueue<EventModuleTerminalStatus>();
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedCount = 0;
        dispatcher.TerminalStatusRecorded += status =>
        {
            statuses.Enqueue(status);
            if (Interlocked.Increment(ref publishedCount) == 1)
                firstPublished.TrySetResult();
            else
                secondPublished.TrySetResult();
        };
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 7, ["first"])));
            await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            await firstPublished.Task.WaitAsync(Timeout);
            var first = Assert.Single(statuses);

            await Assert.ThrowsAsync<AggregateException>(
                () => dispatcher.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 8, ["second"])));
            await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            await secondPublished.Task.WaitAsync(Timeout);

            var published = statuses.ToArray();
            Assert.Equal(2, published.Length);
            Assert.Same(first, published[0]);
            Assert.Equal(7, published[0].SourceGeneration);
            Assert.Equal(8, published[1].SourceGeneration);
            Assert.Same(published[1], Assert.Single(dispatcher.GetTerminalStatuses()));
        }
        finally
        {
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task MiningObserverUsesUnresolvedOutputIdentityAfterLaterAdmission()
    {
        using var allowFault = new ManualResetEventSlim();
        var processingX = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new EventLineCoordinator();
        var mining = new MiningEventLane(line =>
        {
            if (line.Text != "X") return;
            processingX.TrySetResult();
            allowFault.Wait();
            throw new InvalidOperationException("X Mining");
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        var published = new TaskCompletionSource<EventModuleTerminalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status =>
        {
            if (status.Module == EventModule.Mining)
                published.TrySetResult(status);
        };
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "X.txt", 7, ["X"])));
            await processingX.Task.WaitAsync(Timeout);
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Y.txt", 8, ["Y"])));

            allowFault.Set();
            await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            var status = await published.Task.WaitAsync(Timeout);

            Assert.Equal("X.txt", status.CanonicalPath);
            Assert.Equal(7, status.SourceGeneration);
        }
        finally
        {
            allowFault.Set();
            try { await dispatcher.CompleteAsync().WaitAsync(Timeout); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ContextObserverUsesSourceOutputIdentityAfterLaterAdmission()
    {
        using var allowFault = new ManualResetEventSlim();
        var processingX = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is not OrderedEventLine { Line.Text: "X" }) return;
            processingX.TrySetResult();
            allowFault.Wait();
            throw new InvalidOperationException("X Context");
        }));
        var dispatcher = new FastEventDispatcher(new MiningEventLane(_ => { }), context);
        var published = new TaskCompletionSource<EventModuleTerminalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TerminalStatusRecorded += status =>
        {
            if (status.Module == EventModule.Context)
                published.TrySetResult(status);
        };
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "X.txt", 7, ["X"])));
            await processingX.Task.WaitAsync(Timeout);
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Y.txt", 8, ["Y"])));

            allowFault.Set();
            await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            var status = await published.Task.WaitAsync(Timeout);

            Assert.Equal("X.txt", status.CanonicalPath);
            Assert.Equal(7, status.SourceGeneration);
        }
        finally
        {
            allowFault.Set();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (Exception) { }
        }
    }

    [Theory]
    [InlineData(EventModule.Mining)]
    [InlineData(EventModule.Context)]
    public async Task RefreshFaultsAndObserverAgreeOnFaultSourceIdentity(EventModule faulting)
    {
        using var allowFault = new ManualResetEventSlim();
        var processingX = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (faulting != EventModule.Context || item is not OrderedEventLine { Line.Text: "X" }) return;
            processingX.TrySetResult();
            allowFault.Wait();
            throw new InvalidOperationException("X Context");
        }));
        var mining = new MiningEventLane(line =>
        {
            if (faulting != EventModule.Mining || line.Text != "X") return;
            processingX.TrySetResult();
            allowFault.Wait();
            throw new InvalidOperationException("X Mining");
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "X.txt", 7, ["X"]));
            await processingX.Task.WaitAsync(Timeout);
            dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Y.txt", 8, ["Y"]));
            allowFault.Set();
            if (faulting == EventModule.Mining)
                await mining.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            else
                await context.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

            var status = Assert.Single(dispatcher.GetTerminalStatuses());
            Assert.Equal(faulting, status.Module);
            Assert.Equal("X.txt", status.CanonicalPath);
            Assert.Equal(7, status.SourceGeneration);
        }
        finally
        {
            allowFault.Set();
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
            try { context.Dispose(); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task ResetFaultWithoutSourceOutputKeepsControlIdentity()
    {
        using var context = new EventLineCoordinator();
        var dispatcher = new FastEventDispatcher(
            new MiningEventLane(_ => { }, resetProfile: _ => throw new InvalidOperationException("reset")),
            context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Alice.txt", 7, ["before"])));

            await Assert.ThrowsAsync<MiningLaneFaultException>(() => dispatcher.ResetProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout));

            var status = Assert.Single(dispatcher.GetTerminalStatuses());
            Assert.Equal(EventModule.Mining, status.Module);
            Assert.Equal("Alice.txt", status.CanonicalPath);
            Assert.Equal(7, status.SourceGeneration);
        }
        finally { try { await dispatcher.CompleteAsync(); } catch (AggregateException) { } }
    }

    private sealed class Consumer(Action<OrderedEventItem> process) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) => process(item);
        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }

    private static Func<PlayerProfileId, string, long, OrderedEventItem> Marker(string kind) =>
        (profileId, profileName, sequence) => new OrderedEventMarker(profileId, profileName, sequence, Guid.NewGuid(), kind);
}
