using System.Collections.Concurrent;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

/// <summary>Tests the active EventPipeline Context ingress rather than legacy line-admission APIs.</summary>
public sealed class OrderedContextLaneTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static PlayerProfileId Id(string profile) => profile switch
    {
        "Alice" => EventTransportTestProfileIds.Alice,
        "Bob" => EventTransportTestProfileIds.Bob,
        _ => EventTransportTestProfileIds.Default
    };

    [Fact]
    public async Task AcceptOutputPreservesWholeBatchAndFollowingBatchOrder()
    {
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new RecordingConsumer(seen));

        context.AcceptOutput(Batch("Alice", "A1", "A2"));
        context.AcceptOutput(Batch("Alice", "A3"));
        await context.CompleteAsync().WaitAsync(Timeout);

        Assert.Equal(["A1", "A2", "A3"], seen);
    }

    [Fact]
    public async Task ResetProfileIsOrderedBetweenEventPipelineOutputs()
    {
        var events = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new ControlRecordingConsumer(events));

        context.AcceptOutput(Batch("Alice", "before"));
        await context.ResetProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
        context.AcceptOutput(Batch("Alice", "after"));
        await context.CompleteAsync().WaitAsync(Timeout);

        Assert.Equal(["line:before", "reset:Alice", "line:after"], events);
    }

    [Fact]
    public async Task ContinuityIsOrderedBetweenEventPipelineOutputs()
    {
        var events = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new ControlRecordingConsumer(events));

        context.AcceptOutput(Batch("Alice", "before"));
        context.AcceptOutput(new EventContinuityLoss(EventTransportTestProfileIds.Alice, "Alice", "event.txt", 1, StreamGapReason.Replaced));
        context.AcceptOutput(Batch("Alice", "after"));
        await context.CompleteAsync().WaitAsync(Timeout);

        Assert.Equal(["line:before", "continuity:Replaced", "line:after"], events);
    }

    [Fact]
    public async Task EventPipelineIngressOverflowFaultsOnlyTheProfileAndDoesNotBlockAnotherProfile()
    {
        using var release = new ManualResetEventSlim();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bob = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new DelegateConsumer(item =>
        {
            if (item is not OrderedEventLine line) return;
            if (line.ProfileName == "Alice" && line.Line.Text == "active")
            {
                active.TrySetResult();
                release.Wait();
            }
            if (line.ProfileName == "Bob") bob.TrySetResult();
        }));
        try
        {
            context.AcceptOutput(Batch("Alice", "active"));
            await active.Task.WaitAsync(Timeout);
            for (var index = 0; index < EventLineCoordinator.QueueCapacityPerProfile; index++)
                context.AcceptOutput(Batch("Alice", index.ToString()));

            Assert.Throws<EventContextLaneFaultException>(() => context.AcceptOutput(Batch("Alice", "overflow")));
            context.AcceptOutput(Batch("Bob", "healthy"));
            await bob.Task.WaitAsync(Timeout);
            release.Set();
            await Assert.ThrowsAsync<EventContextLaneFaultException>(() => context.CompleteAsync().WaitAsync(Timeout));
        }
        finally { release.Set(); try { context.Dispose(); } catch (EventContextLaneFaultException) { } }
    }

    [Fact]
    public async Task ReleaseDrainsEventPipelineOutputAndAllowsFreshGeneration()
    {
        var seen = new ConcurrentQueue<string>();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new RecordingConsumer(seen));

        context.AcceptOutput(Batch("Alice", "old"));
        await context.ReleaseProfileAsync(EventTransportTestProfileIds.Alice).WaitAsync(Timeout);
        context.AcceptOutput(Batch("Alice", "fresh"));
        await context.CompleteAsync().WaitAsync(Timeout);

        Assert.Equal(["old", "fresh"], seen);
    }

    [Fact]
    public async Task EventPipelineBatchAdmissionDoesNotWaitForPendingMarkerDispatch()
    {
        using var release = new ManualResetEventSlim();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var markerProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new DelegateConsumer(item =>
        {
            if (item is OrderedEventLine { Line.Text: "A1" }) { active.TrySetResult(); release.Wait(); }
            if (item is TestMarker) markerProcessed.TrySetResult();
        }));

        context.AcceptOutput(Batch("Alice", "A1"));
        await active.Task.WaitAsync(Timeout);
        var marker = context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", (profileId, profileName, sequence) => new TestMarker(profileId, profileName, sequence));
        await Task.Run(() => context.AcceptOutput(Batch("Alice", "A2"))).WaitAsync(Timeout);
        Assert.False(marker.IsCompleted);

        release.Set();
        await marker.WaitAsync(Timeout);
        await markerProcessed.Task.WaitAsync(Timeout);
        await context.CompleteAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task MarkerAtFullEventPipelineContextQueueFaultsInsteadOfPretendingSuccess()
    {
        using var release = new ManualResetEventSlim();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new DelegateConsumer(item =>
        {
            if (item is OrderedEventLine { Line.Text: "active" }) { active.TrySetResult(); release.Wait(); }
        }));
        try
        {
            context.AcceptOutput(Batch("Alice", "active"));
            await active.Task.WaitAsync(Timeout);
            for (var index = 0; index < EventLineCoordinator.QueueCapacityPerProfile; index++)
                context.AcceptOutput(Batch("Alice", index.ToString()));

            await Assert.ThrowsAsync<EventContextLaneFaultException>(() =>
                context.EnqueueProfileMarkerAsync(EventTransportTestProfileIds.Alice, "Alice", (profileId, profileName, sequence) => new TestMarker(profileId, profileName, sequence)));
            release.Set();
            await Assert.ThrowsAsync<EventContextLaneFaultException>(() => context.CompleteAsync().WaitAsync(Timeout));
        }
        finally { release.Set(); try { context.Dispose(); } catch (EventContextLaneFaultException) { } }
    }

    [Fact]
    public async Task EventPipelineContextMaintainsProspectingThenMiningVeinDecrementOrder()
    {
        var processor = new VeinContextProcessor();
        using var context = new EventLineCoordinator();
        context.RegisterConsumer(processor);

        context.AcceptOutput(Batch("Alice",
            "You start to gather fragments of the rock.",
            "You would mine iron ore here.",
            "It has a max quality of 12.5.",
            "You will be able to mine here 1103 more times."));
        context.AcceptOutput(Batch("Alice", "You start to mine forwards.", "You mine some ore."));
        await context.CompleteAsync().WaitAsync(Timeout);

        Assert.True(processor.TryGetActiveContext(EventTransportTestProfileIds.Alice, out var vein));
        Assert.Equal(1102, Assert.IsType<RemainingMeasurement.Exact>(vein!.Remaining).Value);
        Assert.Equal(VeinConfidence.Tracking, vein.Confidence);
    }

    [Fact]
    public async Task ReleasedFaultHistoryKeepsFirstAndRecentFaultsWithoutRetainingEveryProfile()
    {
        var context = new EventLineCoordinator();
        context.RegisterConsumer(new DelegateConsumer(_ => throw new InvalidOperationException("terminal")));
        var faults = new List<EventContextLaneFaultException>();
        try
        {
            for (var index = 0; index < 1000; index++)
            {
                var id = PlayerProfileId.CreateNew();
                context.AcceptOutput(new EventReadBatch(id, "P" + index, "P.txt", 1, ["fault"]));
                var fault = await context.GetProfileFaulted(id).WaitAsync(Timeout);
                faults.Add(fault);
                await Assert.ThrowsAsync<EventContextLaneFaultException>(() => context.ReleaseProfileAsync(id).WaitAsync(Timeout));
            }

            Assert.Equal((33, 967L), context.ReleasedProfileFaultHistoryCountForTest);
            var completion = await Assert.ThrowsAsync<AggregateException>(() => context.CompleteAsync().WaitAsync(Timeout));
            Assert.Contains(faults[0], completion.Flatten().InnerExceptions);
            Assert.Contains(faults[^1], completion.Flatten().InnerExceptions);
            Assert.Equal(967, Assert.Single(completion.Flatten().InnerExceptions
                .OfType<TerminalFaultHistorySummaryException>()).OmittedCount);
        }
        finally { try { context.Dispose(); } catch (Exception) { } }
    }

    private static EventReadBatch Batch(string profile, params string[] lines) =>
        new(Id(profile), profile, $"C:\\{profile}\\_Event.txt", 1, lines);

    private sealed class RecordingConsumer(ConcurrentQueue<string> seen) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item)
        {
            if (item is OrderedEventLine line) seen.Enqueue(line.Line.Text);
        }
        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }

    private sealed class ControlRecordingConsumer(ConcurrentQueue<string> seen) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) => seen.Enqueue(item switch
        {
            OrderedEventLine line => "line:" + line.Line.Text,
            ResetProfileBarrier reset => "reset:" + reset.ProfileName,
            StreamContinuityLost loss => "continuity:" + loss.Reason,
            _ => item.GetType().Name
        });
        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }

    private sealed class DelegateConsumer(Action<OrderedEventItem> process) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) => process(item);
        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }

    private sealed record TestMarker(PlayerProfileId ProfileId, string ProfileName, long SequenceNumber)
        : OrderedEventItem(ProfileId, ProfileName, SequenceNumber);
}
