using System.Collections.Concurrent;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

/// <summary>Direct tests of the active Mining module ingress, independent of the retired fanout.</summary>
public sealed class MiningEventLaneTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static PlayerProfileId Id(string profile) => profile switch
    {
        "Alice" => EventTransportTestProfileIds.Alice,
        "Bob" => EventTransportTestProfileIds.Bob,
        _ => EventTransportTestProfileIds.Default
    };

    [Fact]
    public async Task AcceptedBatchesAreProcessedFifoPerProfile()
    {
        var seen = new ConcurrentQueue<string>();
        var lane = new MiningEventLane(line => seen.Enqueue(line.Text));
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "A1", "A2")));
            Assert.True(lane.TryEnqueue(Batch("Alice", "A3")));

            await lane.CompleteAsync().WaitAsync(Timeout);
            Assert.Equal(["A1", "A2", "A3"], seen);
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task FullQueueFaultsTheProfileAndDrainsPreviouslyAcceptedWork()
    {
        using var release = new ManualResetEventSlim();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<string>();
        var lane = new MiningEventLane(line =>
        {
            if (line.Text == "active") { active.TrySetResult(); release.Wait(); }
            seen.Enqueue(line.Text);
        });
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "active")));
            await active.Task.WaitAsync(Timeout);
            for (var index = 0; index < MiningEventLane.QueueCapacityPerProfile; index++)
                Assert.True(lane.TryEnqueue(Batch("Alice", index.ToString())));

            Assert.False(lane.TryEnqueue(Batch("Alice", "overflow")));
            release.Set();
            await lane.CompleteAsync().WaitAsync(Timeout);
            Assert.Equal(MiningEventLane.QueueCapacityPerProfile + 1, seen.Count);
        }
        finally { release.Set(); await lane.CompleteAsync(); }
    }

    [Fact]
    public async Task WorkerFaultReportsUnresolvedOutputsAndRejectsLaterAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var lane = new MiningEventLane(line =>
        {
            if (line.Text != "A2") return;
            entered.TrySetResult();
            release.Wait();
            throw new InvalidOperationException("fault");
        });
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "A1", "A2", "A3")));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(lane.TryEnqueue(Batch("Alice", "B1")));
            release.Set();

            var fault = await lane.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            Assert.False(lane.TryEnqueue(Batch("Alice", "late")));
            Assert.Equal(1, fault.FailedLineIndex);
            Assert.Equal(["A1", "A2", "A3"], Assert.IsType<EventReadBatch>(fault.UnresolvedOutputs[0]).Lines);
            Assert.Equal(["B1"], Assert.IsType<EventReadBatch>(fault.UnresolvedOutputs[1]).Lines);
            await Assert.ThrowsAsync<MiningLaneFaultException>(() => lane.CompleteAsync().WaitAsync(Timeout));
        }
        finally { release.Set(); try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task ProfileResetAndContinuityStayInTheLocalFifo()
    {
        var seen = new ConcurrentQueue<string>();
        var lane = new MiningEventLane(
            line => seen.Enqueue("line:" + line.Text),
            loss => seen.Enqueue("continuity:" + loss.Reason),
            resetProfile: profile => seen.Enqueue("reset:" + profile));
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "before")));
            await lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice");
            Assert.True(lane.TryEnqueue(new EventContinuityLoss(EventTransportTestProfileIds.Alice, "Alice", "event.txt", 1, StreamGapReason.Replaced)));
            Assert.True(lane.TryEnqueue(Batch("Alice", "after")));
            await lane.CompleteAsync().WaitAsync(Timeout);

            Assert.Equal(["line:before", $"reset:{EventTransportTestProfileIds.Alice}", "continuity:Replaced", "line:after"], seen);
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task ProfileResetDoesNotWaitForAnotherProfileWorker()
    {
        using var releaseBob = new ManualResetEventSlim();
        var bobEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aliceReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new MiningEventLane(
            line =>
            {
                if (line.ProfileName == "Bob")
                {
                    bobEntered.TrySetResult();
                    releaseBob.Wait();
                }
            },
            resetProfile: profile =>
            {
                if (profile == EventTransportTestProfileIds.Alice) aliceReset.TrySetResult();
            });

        try
        {
            Assert.True(lane.TryEnqueue(Batch("Bob", "B")));
            await bobEntered.Task.WaitAsync(Timeout);

            await lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice").WaitAsync(Timeout);
            await aliceReset.Task.WaitAsync(Timeout);

            releaseBob.Set();
            await lane.CompleteAsync().WaitAsync(Timeout);
        }
        finally { releaseBob.Set(); try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task ResetQueuedDuringActiveOutputCannotClearLaterOutput()
    {
        using var releaseBefore = new ManualResetEventSlim();
        var beforeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new List<string>();
        var stateSync = new object();
        var lane = new MiningEventLane(
            line =>
            {
                if (line.Text == "before")
                {
                    beforeEntered.TrySetResult();
                    releaseBefore.Wait();
                }
                lock (stateSync) state.Add(line.Text);
            },
            resetProfile: _ => { lock (stateSync) state.Clear(); });

        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "before")));
            await beforeEntered.Task.WaitAsync(Timeout);
            var reset = lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice");
            Assert.True(lane.TryEnqueue(Batch("Alice", "after")));

            releaseBefore.Set();
            await reset.WaitAsync(Timeout);
            await lane.CompleteAsync().WaitAsync(Timeout);

            lock (stateSync) Assert.Equal(["after"], state);
        }
        finally { releaseBefore.Set(); try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task RepeatedResetAndCompletionDrainEveryAcceptedReset()
    {
        using var releaseBefore = new ManualResetEventSlim();
        var beforeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resets = 0;
        var lane = new MiningEventLane(
            line =>
            {
                if (line.Text == "before")
                {
                    beforeEntered.TrySetResult();
                    releaseBefore.Wait();
                }
            },
            resetProfile: _ => Interlocked.Increment(ref resets));

        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "before")));
            await beforeEntered.Task.WaitAsync(Timeout);
            var first = lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice");
            var second = lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice");
            var completion = lane.CompleteAsync();
            Assert.False(completion.IsCompleted);

            releaseBefore.Set();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            await completion.WaitAsync(Timeout);
            Assert.Equal(2, Volatile.Read(ref resets));
        }
        finally { releaseBefore.Set(); try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task ResetAfterWorkerFaultReturnsTheSameTerminalFault()
    {
        var lane = new MiningEventLane(_ => throw new InvalidOperationException("fault"));
        Assert.True(lane.TryEnqueue(Batch("Alice", "A")));

        var fault = await lane.GetProfileFaulted(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
        var resetFault = await Assert.ThrowsAsync<MiningLaneFaultException>(
            () => lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice"));
        var completionFault = await Assert.ThrowsAsync<MiningLaneFaultException>(
            () => lane.CompleteAsync().WaitAsync(Timeout));

        Assert.Same(fault, resetFault);
        Assert.Same(fault, completionFault);
    }

    [Fact]
    public async Task ProfileWorkersProcessIndependently()
    {
        using var releaseAlice = new ManualResetEventSlim();
        var aliceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bobProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new MiningEventLane(line =>
        {
            if (line.ProfileName == "Alice")
            {
                aliceStarted.TrySetResult();
                releaseAlice.Wait();
            }
            else
            {
                bobProcessed.TrySetResult();
            }
        });

        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "A")));
            await aliceStarted.Task.WaitAsync(Timeout);
            Assert.True(lane.TryEnqueue(Batch("Bob", "B")));
            await bobProcessed.Task.WaitAsync(Timeout);
            releaseAlice.Set();
            await lane.CompleteAsync().WaitAsync(Timeout);
        }
        finally { releaseAlice.Set(); try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task ProfileResetClearsMiningAndInspirationBeforeLaterOutput()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirations);
        var processor = new MiningEventProcessor(new MiningEventParser(), counter, tracker);
        var lane = new MiningEventLane(processor);

        Assert.True(lane.TryEnqueue(Batch("Alice",
            "You start to mine forwards.",
            "You have a moment of inspiration...",
            "You mine some ore.")));
        var reset = lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice");
        Assert.True(lane.TryEnqueue(Batch("Alice", "You mine some ore.")));

        await reset.WaitAsync(Timeout);
        await lane.CompleteAsync().WaitAsync(Timeout);

        var profile = Assert.Single(counter.GetProfileSnapshots());
        Assert.Equal(EventTransportTestProfileIds.Alice, profile.Key);
        Assert.Equal(1, profile.Value.TotalCount);
        Assert.Equal(1, profile.Value.Materials[MiningMaterial.Ore]);
        Assert.Empty(inspirations.GetProfileSnapshots());
    }

    [Fact]
    public async Task MiningInspirationResetRunsBetweenAcceptedBatchesWithoutChangingMiningCounters()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter, new MiningInspirationTracker(inspirations));
        var lane = new MiningEventLane(processor);
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice",
                "You start to mine forwards.",
                "You have a moment of inspiration...",
                "You mine some ore.")));
            var reset = lane.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice, "Alice");
            Assert.True(lane.TryEnqueue(Batch("Alice",
                "You start to mine forwards.",
                "You have a moment of inspiration...",
                "You mine some shards.")));

            await reset.WaitAsync(Timeout);
            await lane.CompleteAsync().WaitAsync(Timeout);

            Assert.Equal(2, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
            Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
            Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].PrimaryYieldCounts[MiningMaterial.Shards]);
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetCannotRunBetweenLinesOfEarlierAcceptedBatch()
    {
        var seen = new ConcurrentQueue<string>();
        var lane = new MiningEventLane(
            line => seen.Enqueue("line:" + line.Text),
            resetMiningInspiration: profile => seen.Enqueue("moi-reset:" + profile));
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "before-1", "before-2")));
            var reset = lane.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice, "Alice");
            Assert.True(lane.TryEnqueue(Batch("Alice", "after")));

            await reset.WaitAsync(Timeout);
            await lane.CompleteAsync().WaitAsync(Timeout);

            Assert.Equal(["line:before-1", "line:before-2", $"moi-reset:{EventTransportTestProfileIds.Alice}", "line:after"], seen);
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetDoesNotAffectAnotherProfile()
    {
        var counter = new MiningSessionCounter();
        var inspirations = new MiningInspirationCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter, new MiningInspirationTracker(inspirations));
        var lane = new MiningEventLane(processor);
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "You start to mine forwards.", "You have a moment of inspiration...", "You mine some ore.")));
            Assert.True(lane.TryEnqueue(Batch("Bob", "You start to mine forwards.", "You have a moment of inspiration...", "You mine some shards.")));
            await lane.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice, "Alice").WaitAsync(Timeout);
            await lane.CompleteAsync().WaitAsync(Timeout);

            Assert.False(inspirations.GetProfileSnapshots().ContainsKey(EventTransportTestProfileIds.Alice));
            Assert.Equal(1, inspirations.GetProfileSnapshots()[EventTransportTestProfileIds.Bob].TotalCount);
            Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
            Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Bob].TotalCount);
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetFaultDoesNotReportSuccess()
    {
        var lane = new MiningEventLane(_ => { }, resetMiningInspiration: _ => throw new InvalidOperationException("reset"));
        try
        {
            await Assert.ThrowsAsync<MiningLaneFaultException>(() => lane.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice, "Alice").WaitAsync(Timeout));
            await Assert.ThrowsAsync<MiningLaneFaultException>(() => lane.CompleteAsync().WaitAsync(Timeout));
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task MiningInspirationResetCreatesFreshLaneAfterProfileReleaseCompletes()
    {
        var resets = 0;
        var lane = new MiningEventLane(_ => { }, resetMiningInspiration: _ => Interlocked.Increment(ref resets));
        await lane.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice, "Alice").WaitAsync(Timeout);
        await lane.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);

        await lane.ResetMiningInspirationAsync(EventTransportTestProfileIds.Alice, "Alice").WaitAsync(Timeout);
        await lane.CompleteAsync().WaitAsync(Timeout);
        Assert.Equal(2, resets);
    }

    [Fact]
    public async Task ThrowingResetBoundaryCallbackDoesNotFaultLaneOrBlockLaterOutput()
    {
        var counter = new MiningSessionCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter);
        var resetProfiles = new ConcurrentQueue<PlayerProfileId>();
        var boundaryFailures = new ConcurrentQueue<(PlayerProfileId ProfileId, Exception Exception)>();
        var lane = new MiningEventLane(
            line => processor.Process(line),
            resetProfile: profileId =>
            {
                counter.ResetProfile(profileId);
                resetProfiles.Enqueue(profileId);
            },
            captureResetBoundary: _ => throw new InvalidOperationException("baseline callback failure"),
            reportResetBoundaryFailure: (profileId, exception) => boundaryFailures.Enqueue((profileId, exception)));

        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "You mine some ore.")));
            await lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice").WaitAsync(Timeout);
            Assert.True(lane.TryEnqueue(Batch("Alice", "You mine some ore.")));
            await lane.CompleteAsync().WaitAsync(Timeout);

            Assert.Equal([EventTransportTestProfileIds.Alice], resetProfiles);
            var failure = Assert.Single(boundaryFailures);
            Assert.Equal(EventTransportTestProfileIds.Alice, failure.ProfileId);
            Assert.IsType<InvalidOperationException>(failure.Exception);
            Assert.False(lane.GetMetrics(EventTransportTestProfileIds.Alice).Faulted);

            var profile = Assert.Single(counter.GetProfileSnapshots());
            Assert.Equal(EventTransportTestProfileIds.Alice, profile.Key);
            Assert.Equal(1, profile.Value.TotalCount);
        }
        finally
        {
            try { await lane.CompleteAsync(); }
            catch (MiningLaneFaultException) { }
        }
    }

    [Fact]
#pragma warning disable xUnit1031
    public async Task ResetBoundaryRunsBeforeProfileResetAndBlocksLaterOutput()
    {
        var order = new ConcurrentQueue<string>();
        var counter = new MiningSessionCounter();
        var processor = new MiningEventProcessor(new MiningEventParser(), counter);
        var boundaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new MiningEventLane(
            line =>
            {
                processor.Process(line);
                order.Enqueue(line.Text);
            },
            resetProfile: profileId =>
            {
                counter.ResetProfile(profileId);
                order.Enqueue("reset");
            },
            captureResetBoundary: _ =>
            {
                order.Enqueue("boundary");
                boundaryStarted.TrySetResult();
                releaseBoundary.Task.GetAwaiter().GetResult();
            });

        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "You mine some ore.")));
            var reset = lane.ResetProfileAsync(EventTransportTestProfileIds.Alice, "Alice");
            Assert.True(lane.TryEnqueue(Batch("Alice", "You mine some ore.")));

            await boundaryStarted.Task.WaitAsync(Timeout);
            Assert.False(reset.IsCompleted);
            Assert.Equal(["You mine some ore.", "boundary"], order);
            Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);

            releaseBoundary.TrySetResult();
            await reset.WaitAsync(Timeout);
            await lane.CompleteAsync().WaitAsync(Timeout);

            Assert.Equal(["You mine some ore.", "boundary", "reset", "You mine some ore."], order);
            Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
        }
        finally
        {
            releaseBoundary.TrySetResult();
            await lane.CompleteAsync();
        }
    }
#pragma warning restore xUnit1031

    [Fact]
    public async Task ReleaseDrainsOldGenerationAndReappearanceUsesFreshQueue()
    {
        var seen = new ConcurrentQueue<(string Text, int Index)>();
        var lane = new MiningEventLane(line => seen.Enqueue((line.Text, 0)));
        try
        {
            Assert.True(lane.TryEnqueue(Batch("Alice", "old")));
            await lane.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
            Assert.True(lane.TryEnqueue(Batch("Alice", "fresh")));
            await lane.CompleteAsync().WaitAsync(Timeout);
            Assert.Equal(["old", "fresh"], seen.Select(item => item.Text));
        }
        finally { try { await lane.CompleteAsync(); } catch (MiningLaneFaultException) { } }
    }

    [Fact]
    public async Task ReleasedFaultHistoryKeepsFirstAndRecentFaultsWithoutRetainingEveryGeneration()
    {
        var lane = new MiningEventLane(_ => throw new InvalidOperationException("terminal"));
        var faults = new List<MiningLaneFaultException>();
        try
        {
            for (var index = 0; index < 1000; index++)
            {
                var id = PlayerProfileId.CreateNew();
                Assert.True(lane.TryEnqueue(new EventReadBatch(id, "P" + index, "P.txt", 1, ["fault"])));
                var fault = await lane.GetProfileFaulted(id).WaitAsync(Timeout);
                faults.Add(fault);
                await Assert.ThrowsAsync<MiningLaneFaultException>(() => lane.ReleaseProfileAsync(id).WaitAsync(Timeout));
            }

            Assert.Equal((33, 967L), lane.TerminalFaultHistoryCountForTest);
            var completion = await Assert.ThrowsAsync<AggregateException>(() => lane.CompleteAsync().WaitAsync(Timeout));
            Assert.Contains(faults[0], completion.Flatten().InnerExceptions);
            Assert.Contains(faults[^1], completion.Flatten().InnerExceptions);
            var summary = Assert.Single(completion.Flatten().InnerExceptions.OfType<TerminalFaultHistorySummaryException>());
            Assert.Equal(967, summary.OmittedCount);
        }
        finally { try { await lane.CompleteAsync(); } catch (Exception) { } }
    }

    private static EventReadBatch Batch(string profile, params string[] lines) =>
        new(Id(profile), profile, $"C:\\{profile}\\_Event.txt", 1, lines);
}
