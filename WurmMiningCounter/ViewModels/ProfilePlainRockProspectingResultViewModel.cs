using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class ProfilePlainRockProspectingResultViewModel
{
    public ProfilePlainRockProspectingResultViewModel(ProspectingResult.PlainRock result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Quality = result.Quality;
    }

    public string Title => "Plain Rock";

    public QualityMeasurement Quality { get; }

    public bool HasKnownQuality => Quality is not QualityMeasurement.Unknown;

    public string QualityText => ProfileVeinContextViewModel.FormatQuality(Quality);
}
