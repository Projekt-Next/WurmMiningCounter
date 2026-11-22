using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningInspirationBoundaryObserverTests
{
    [Fact]
    public async Task Coordinator_OrderedEventLineDoesNotReachMiningEventProcessor()
    {
        var miningCounter = new MiningSessionCounter();
        var inspirationCounter = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirationCounter);
        var observer = new MiningInspirationBoundaryObserver(tracker);
        using var coordinator = new EventLineCoordinator();
        coordinator.RegisterConsumer(observer);

        coordinator.AcceptOutput(new EventReadBatch(EventTransportTestProfileIds.Alice, "Alice", "Event.txt", 1, ["You mine some ore."]));
        await coordinator.CompleteAsync();

        Assert.Empty(miningCounter.GetProfileSnapshots());
        Assert.Empty(inspirationCounter.GetProfileSnapshots());
    }

    [Theory]
    [InlineData(typeof(StreamContinuityLost))]
    [InlineData(typeof(ResetProfileBarrier))]
    public void Process_BoundaryClearsPendingMoi(Type boundaryType)
    {
        var miningCounter = new MiningSessionCounter();
        var inspirationCounter = new MiningInspirationCounter();
        var tracker = new MiningInspirationTracker(inspirationCounter);
        var processor = new MiningEventProcessor(new MiningEventParser(), miningCounter, tracker);
        var observer = new MiningInspirationBoundaryObserver(tracker);
        var profile = "Alice";

        processor.Process(new EventLogLine(EventTransportTestProfileIds.For(profile), profile, "Event.txt", "You start to mine forwards."));
        processor.Process(new EventLogLine(EventTransportTestProfileIds.For(profile), profile, "Event.txt", "You have a moment of inspiration..."));

        observer.Process(CreateBoundary(boundaryType, profile));
        processor.Process(new EventLogLine(EventTransportTestProfileIds.For(profile), profile, "Event.txt", "You mine some ore."));

        Assert.Empty(inspirationCounter.GetProfileSnapshots());
        Assert.Equal(1, miningCounter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    private static OrderedEventItem CreateBoundary(Type boundaryType, string profileName) =>
        boundaryType == typeof(StreamContinuityLost)
            ? new StreamContinuityLost(EventTransportTestProfileIds.For(profileName), profileName, 1, StreamGapReason.RotationOrderLost, "Event.txt")
            : new ResetProfileBarrier(EventTransportTestProfileIds.For(profileName), profileName, 1);
}
