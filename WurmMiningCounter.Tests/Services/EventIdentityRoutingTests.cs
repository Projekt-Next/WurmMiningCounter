using System.Collections.Concurrent;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class EventIdentityRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SameDisplayNameUsesSeparateFifoQueuesAndReleaseLifecycle()
    {
        var first = PlayerProfileId.CreateNew();
        var second = PlayerProfileId.CreateNew();
        var miningLines = new ConcurrentQueue<string>();
        var contextLines = new ConcurrentQueue<(PlayerProfileId? Id, string Text)>();
        var miningCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is not OrderedEventLine line)
                return;

            contextLines.Enqueue((item.ProfileId, line.Line.Text));
            if (contextLines.Count == 4)
                contextCompleted.TrySetResult();
        }));
        var mining = new MiningEventLane(line =>
        {
            miningLines.Enqueue(line.Text);
            if (miningLines.Count == 4)
                miningCompleted.TrySetResult();
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(first, "Alice", "first.txt", 1, ["A1", "A2"])));
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(second, "Alice", "second.txt", 1, ["B1", "B2"])));

            await Task.WhenAll(miningCompleted.Task, contextCompleted.Task).WaitAsync(Timeout);

            Assert.True(mining.HasProfileQueue(first));
            Assert.True(mining.HasProfileQueue(second));
            Assert.True(context.HasProfileQueue(first));
            Assert.True(context.HasProfileQueue(second));
            Assert.Equal(["A1", "A2"], miningLines.Where(line => line.StartsWith('A')));
            Assert.Equal(["B1", "B2"], miningLines.Where(line => line.StartsWith('B')));
            Assert.Equal(["A1", "A2"], contextLines.Where(line => line.Id == first).Select(line => line.Text));
            Assert.Equal(["B1", "B2"], contextLines.Where(line => line.Id == second).Select(line => line.Text));
            await dispatcher.ResetProfileAsync(first).WaitAsync(Timeout);

            await dispatcher.ReleaseProfileAsync(first).WaitAsync(Timeout);

            Assert.False(mining.HasProfileQueue(first));
            Assert.False(context.HasProfileQueue(first));
            Assert.True(mining.HasProfileQueue(second));
            Assert.True(context.HasProfileQueue(second));
        }
        finally
        {
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ResetAndFaultAreIsolatedByProfileIdWhenNamesMatch()
    {
        var faulting = PlayerProfileId.CreateNew();
        var healthy = PlayerProfileId.CreateNew();
        var resetIds = new ConcurrentQueue<PlayerProfileId?>();
        var healthyProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var context = new EventLineCoordinator();
        context.RegisterConsumer(new Consumer(item =>
        {
            if (item is ResetProfileBarrier)
                resetIds.Enqueue(item.ProfileId);
        }));
        var mining = new MiningEventLane(line =>
        {
            if (line.Text == "fault")
                throw new InvalidOperationException("Expected fault.");
            if (line.Text == "healthy")
                healthyProcessed.TrySetResult();
        });
        var dispatcher = new FastEventDispatcher(mining, context);
        try
        {
            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(faulting, "Alice", "fault.txt", 1, ["fault"])));
            var failure = await mining.GetProfileFaulted(faulting).WaitAsync(Timeout);
            Assert.Equal(faulting, failure.ProfileId);

            Assert.Equal(EventPipelineHandoffResult.Complete,
                dispatcher.Handoff(new EventReadBatch(healthy, "Alice", "healthy.txt", 1, ["healthy"])));
            await healthyProcessed.Task.WaitAsync(Timeout);
            Assert.Null(mining.GetProfileFault(healthy));

            await dispatcher.ResetProfileAsync(healthy).WaitAsync(Timeout);
            Assert.Contains(healthy, resetIds);
            Assert.DoesNotContain(faulting, resetIds);
            Assert.True(dispatcher.HasProfileLifecycle(faulting));
            Assert.True(dispatcher.HasProfileLifecycle(healthy));
        }
        finally
        {
            try { await dispatcher.CompleteAsync(); } catch (AggregateException) { }
        }
    }

    private sealed class Consumer(Action<OrderedEventItem> accept) : IOrderedEventConsumer
    {
        public void Process(OrderedEventItem item) => accept(item);
        public void Invalidate(PlayerProfileId profileId, string profileName) { }
    }
}
