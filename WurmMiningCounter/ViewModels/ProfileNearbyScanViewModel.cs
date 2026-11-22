using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class ProfileNearbyScanViewModel
{
    public ProfileNearbyScanViewModel(NearbyScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ProfileId = snapshot.ProfileId;
        RawDescription = snapshot.RawDescription;
        IsOnlyRock = snapshot.IsOnlyRock;
        Entries = snapshot.Entries
            .Select(entry => new NearbyScanEntryViewModel(entry.MaterialText, entry.GradeText))
            .ToArray();
    }

    public PlayerProfileId ProfileId { get; }

    public string RawDescription { get; }

    public bool IsOnlyRock { get; }

    public IReadOnlyList<NearbyScanEntryViewModel> Entries { get; }
}

public sealed class NearbyScanEntryViewModel
{
    public NearbyScanEntryViewModel(string materialText, string? gradeText)
    {
        MaterialText = materialText ?? throw new ArgumentNullException(nameof(materialText));
        GradeText = gradeText;
    }

    public string MaterialText { get; }

    public string? GradeText { get; }

    public string DisplayText => GradeText is null
        ? MaterialText
        : $"{MaterialText} ({GradeText} quality)";
}
