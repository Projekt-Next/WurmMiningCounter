namespace WurmMiningCounter.Models;

public sealed class VeinContextChangedEventArgs : EventArgs
{
    public VeinContextChangedEventArgs(
        PlayerProfileId profileId,
        VeinContextSnapshot? snapshot,
        bool isLocalExactDecrement = false)
    {
        ProfileId = profileId;
        Snapshot = snapshot;
        IsLocalExactDecrement = isLocalExactDecrement;
    }

    public PlayerProfileId ProfileId { get; }

    public VeinContextSnapshot? Snapshot { get; }

    public bool IsLocalExactDecrement { get; }
}
