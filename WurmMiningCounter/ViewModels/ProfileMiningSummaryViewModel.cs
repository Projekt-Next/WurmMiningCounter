using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class ProfileMiningSummaryViewModel
{
    public ProfileMiningSummaryViewModel(
        PlayerProfileId profileId,
        string profileName,
        long totalCount,
        IReadOnlyList<MiningMaterialCountViewModel> materials)
    {
        ProfileId = profileId;
        ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
        TotalCount = totalCount;
        Materials = materials ?? throw new ArgumentNullException(nameof(materials));
    }

    public PlayerProfileId ProfileId { get; }

    public string ProfileName { get; }

    public IReadOnlyList<MiningMaterialCountViewModel> Materials { get; }

    public long TotalCount { get; }
}
