using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>
/// Clears only Mining Inspiration correlation state at ordered-stream boundaries.
/// Ordinary event lines deliberately have no route to the mining processor here.
/// </summary>
public sealed class MiningInspirationBoundaryObserver : IOrderedEventConsumer
{
    private readonly MiningInspirationTracker _tracker;

    public MiningInspirationBoundaryObserver(MiningInspirationTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public void Process(OrderedEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is StreamContinuityLost or ResetProfileBarrier)
            _tracker.ClearProfilePending(item.ProfileId);
    }

    public void Invalidate(PlayerProfileId profileId, string profileName)
    {
        _tracker.ClearProfilePending(profileId);
    }
}
