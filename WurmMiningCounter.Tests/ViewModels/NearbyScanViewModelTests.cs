using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class NearbyScanViewModelTests
{
    [Fact]
    public void NearbyOnlyProfileRemainsVisibleAndShowsEntriesInOrder()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.ApplyNearbyScanSnapshot(EventTransportTestProfileIds.Alice, Scan(EventTransportTestProfileIds.Alice, "Alice", false,
            new NearbyProspectingEntry("iron", "normal"),
            new NearbyProspectingEntry("iron", "good"),
            new NearbyProspectingEntry("rock salt", null)));

        var panel = Assert.Single(viewModel.ProfilePanels);
        Assert.Equal("Alice", panel.ProfileName);
        Assert.Equal(0, panel.TotalCount);
        Assert.True(panel.HasNearbyScan);
        Assert.Equal(
            ["iron (normal quality)", "iron (good quality)", "rock salt"],
            panel.NearbyScan!.Entries.Select(entry => entry.DisplayText));
    }

    [Fact]
    public void OnlyRockScanIsVisibleWithoutEntries()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.ApplyNearbyScanSnapshot(EventTransportTestProfileIds.Alice, Scan(EventTransportTestProfileIds.Alice, "Alice", true));

        var scan = Assert.Single(viewModel.ProfilePanels).NearbyScan;
        Assert.NotNull(scan);
        Assert.True(scan.IsOnlyRock);
        Assert.Empty(scan.Entries);
    }

    [Fact]
    public void NearbyScansRemainIndependentPerProfile()
    {
        var viewModel = new MainViewModel(new MiningSessionCounter());
        viewModel.ApplyNearbyScanSnapshot(EventTransportTestProfileIds.Alice, Scan(EventTransportTestProfileIds.Alice, "Alice", false, new NearbyProspectingEntry("iron", null)));
        viewModel.ApplyNearbyScanSnapshot(EventTransportTestProfileIds.Bob, Scan(EventTransportTestProfileIds.Bob, "Bob", false, new NearbyProspectingEntry("tin", "good")));

        Assert.Equal(2, viewModel.ProfilePanels.Count);
        Assert.Equal("iron", viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice").NearbyScan!.Entries.Single().MaterialText);
        Assert.Equal("tin", viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob").NearbyScan!.Entries.Single().MaterialText);
    }

    private static NearbyScanSnapshot Scan(PlayerProfileId profileId, string profileName, bool onlyRock, params NearbyProspectingEntry[] entries) =>
        new(profileId, profileName, onlyRock ? "only rock" : string.Join(", ", entries.Select(entry => entry.MaterialText)), entries, onlyRock);
}
