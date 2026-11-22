namespace WurmMiningCounter.Models;

public sealed class NearbyScanChangedEventArgs : EventArgs
{
    public NearbyScanChangedEventArgs(PlayerProfileId profileId, NearbyScanSnapshot? snapshot)
    {
        ProfileId = profileId;
        Snapshot = snapshot;
    }

    public PlayerProfileId ProfileId { get; }

    public NearbyScanSnapshot? Snapshot { get; }
}
