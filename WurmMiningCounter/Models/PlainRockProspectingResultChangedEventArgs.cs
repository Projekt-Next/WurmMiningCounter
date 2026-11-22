namespace WurmMiningCounter.Models;

public sealed class PlainRockProspectingResultChangedEventArgs : EventArgs
{
    public PlainRockProspectingResultChangedEventArgs(
        PlayerProfileId profileId,
        string profileName,
        ProspectingResult.PlainRock? result)
    {
        ProfileId = profileId;
        ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
        Result = result;
    }

    public PlayerProfileId ProfileId { get; }

    public string ProfileName { get; }

    public ProspectingResult.PlainRock? Result { get; }
}
