namespace WurmMiningCounter.Models;

public sealed class MiningSessionCounterChangedEventArgs : EventArgs
{
    public MiningSessionCounterChangedEventArgs(PlayerProfileId? profileId)
    {
        ProfileId = profileId;
    }

    public PlayerProfileId? ProfileId { get; }
}
