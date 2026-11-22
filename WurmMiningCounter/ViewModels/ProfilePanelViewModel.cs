using System.Globalization;
using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.ViewModels;

public sealed class ProfilePanelViewModel
{
    public ProfilePanelViewModel(
        PlayerProfileId profileId,
        string profileName,
        ProfileMiningSummaryViewModel? miningSummary,
        ProfileVeinContextViewModel? veinContext,
        long miningInspirationCount = 0,
        ProfileNearbyScanViewModel? nearbyScan = null,
        ProfilePlainRockProspectingResultViewModel? plainRockProspectingResult = null,
        LiveSkillProfileSnapshot? liveSkill = null,
        bool hasAnalyseResult = false)
    {
        ProfileId = profileId;
        ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
        MiningSummary = miningSummary;
        VeinContext = veinContext;
        MiningInspirationCount = miningInspirationCount;
        NearbyScan = nearbyScan;
        PlainRockProspectingResult = plainRockProspectingResult;
        LiveSkill = liveSkill;
        HasAnalyseResult = hasAnalyseResult;
    }

    public PlayerProfileId ProfileId { get; }

    public string ProfileName { get; }

    public ProfileMiningSummaryViewModel? MiningSummary { get; }

    public IReadOnlyList<MiningMaterialCountViewModel> Materials =>
        MiningSummary?.Materials ?? Array.Empty<MiningMaterialCountViewModel>();

    public long TotalCount => MiningSummary?.TotalCount ?? 0;

    public long MiningInspirationCount { get; }

    public bool HasMiningInspirations => MiningInspirationCount > 0;

    public ProfileVeinContextViewModel? VeinContext { get; }

    public bool HasVeinContext => VeinContext is not null;

    public ProfilePlainRockProspectingResultViewModel? PlainRockProspectingResult { get; }

    public bool HasPlainRockProspectingResult => PlainRockProspectingResult is not null;

    public bool HasProspectingResult => HasVeinContext || HasPlainRockProspectingResult;

    public ProfileNearbyScanViewModel? NearbyScan { get; }

    public bool HasNearbyScan => NearbyScan is not null;

    public LiveSkillProfileSnapshot? LiveSkill { get; }

    public bool HasAnalyseResult { get; }

    public string LiveMiningCurrentDisplay => LiveSkill is { IsHealthy: true }
        && MiningSkill is { } mining
        ? mining.CurrentValue.ToString("0.######", CultureInfo.InvariantCulture)
        : "\u2014";

    public string LiveMiningGainDisplay => LiveSkill is { IsHealthy: true }
        && MiningSkill is { } mining
        ? mining.SessionGain == 0m ? "0" : "+" + mining.SessionGain.ToString("0.######", CultureInfo.InvariantCulture)
        : "\u2014";

    public decimal? PickaxeSweetSpotQl => LiveSkill is { IsHealthy: true }
        && MiningSkill is { } mining
        && VeinContext is { } veinContext
        && veinContext.Material is not (VeinMaterial.Adamantine or VeinMaterial.Glimmersteel)
        ? MiningSweetSpotCalculator.CalculateRecommendedPickaxeQl(mining.CurrentValue, veinContext.Material)
        : null;

    public bool HasPickaxeSweetSpot => PickaxeSweetSpotQl is not null;

    public string PickaxeSweetSpotDisplay => PickaxeSweetSpotQl switch
    {
        null => string.Empty,
        < 1m => "Pickaxe sweet spot: <1 QL",
        > 100m => "Pickaxe sweet spot: >100 QL",
        var sweetSpot => $"Pickaxe sweet spot: {sweetSpot.Value.ToString("0.##", CultureInfo.InvariantCulture)} QL"
    };

    private SkillStateSnapshot? MiningSkill => LiveSkill?.Skills.FirstOrDefault(skill =>
        string.Equals(skill.SkillName, "Mining", StringComparison.Ordinal));
}
