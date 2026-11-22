using WurmMiningCounter.MiningStatistics;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class LiveMiningProfilePanelViewModelTests
{
    [Theory]
    [InlineData(VeinMaterial.Adamantine)]
    [InlineData(VeinMaterial.Glimmersteel)]
    public void PickaxeSweetSpotIsUnavailableForUnconfirmedDifficulty(VeinMaterial material)
    {
        var panel = Panel(Snapshot("Alice", LiveSkillSourceState.Observing, Mining(70m, 0m)), material);
        Assert.False(panel.HasPickaxeSweetSpot);
        Assert.Equal(string.Empty, panel.PickaxeSweetSpotDisplay);
    }

    [Fact]
    public void DisplaysUnavailableForMissingMiningOrUnhealthySource()
    {
        var missing = Panel(null);
        var incomplete = Panel(Snapshot("Alice", LiveSkillSourceState.Incomplete, Mining(95m, 1m)));
        var noMining = Panel(Snapshot("Alice", LiveSkillSourceState.Observing, Skill("Prospecting", 10m, 1m)));

        Assert.Equal("\u2014", missing.LiveMiningCurrentDisplay);
        Assert.Equal("\u2014", missing.LiveMiningGainDisplay);
        Assert.Equal("\u2014", incomplete.LiveMiningCurrentDisplay);
        Assert.Equal("\u2014", incomplete.LiveMiningGainDisplay);
        Assert.Equal("\u2014", noMining.LiveMiningCurrentDisplay);
        Assert.Equal("\u2014", noMining.LiveMiningGainDisplay);
    }

    [Fact]
    public void FormatsCurrentWithAtMostSixFractionalDigits()
    {
        var panel = Panel(Snapshot("Alice", LiveSkillSourceState.Observing, Mining(95.7962954m, 0m)));

        Assert.Equal("95.796295", panel.LiveMiningCurrentDisplay);
    }

    [Fact]
    public void DisplaysSessionGainRatherThanLastGain()
    {
        var panel = Panel(Snapshot("Alice", LiveSkillSourceState.Observing, new SkillStateSnapshot("Mining", 95m, 0.000001m, 0.000192m)));

        Assert.Equal("+0.000192", panel.LiveMiningGainDisplay);
    }

    [Fact]
    public void DisplaysHealthyZeroSessionGain()
    {
        var panel = Panel(Snapshot("Alice", LiveSkillSourceState.Observing, Mining(95m, 0m)));

        Assert.Equal("0", panel.LiveMiningGainDisplay);
    }

    [Fact]
    public void DisplaysPickaxeSweetSpotForHealthyMiningAndVeinContext()
    {
        var panel = Panel(
            Snapshot("Alice", LiveSkillSourceState.Observing, Mining(70.3241m, 0m)),
            VeinMaterial.RockSalt);

        Assert.True(panel.HasPickaxeSweetSpot);
        Assert.Equal(29.6759m, panel.PickaxeSweetSpotQl);
        Assert.Equal("Pickaxe sweet spot: 29.68 QL", panel.PickaxeSweetSpotDisplay);
    }

    [Fact]
    public void PickaxeSweetSpotIsUnavailableWithoutVeinContext()
    {
        var panel = Panel(Snapshot("Alice", LiveSkillSourceState.Observing, Mining(70m, 0m)));

        Assert.False(panel.HasPickaxeSweetSpot);
    }

    [Fact]
    public void PickaxeSweetSpotIsUnavailableForUnhealthyLiveSkill()
    {
        var panel = Panel(
            Snapshot("Alice", LiveSkillSourceState.Incomplete, Mining(70m, 0m)),
            VeinMaterial.RockSalt);

        Assert.False(panel.HasPickaxeSweetSpot);
    }

    [Fact]
    public void PickaxeSweetSpotIsUnavailableWithoutMiningSkill()
    {
        var panel = Panel(
            Snapshot("Alice", LiveSkillSourceState.Observing, Skill("Prospecting", 10m, 0m)),
            VeinMaterial.RockSalt);

        Assert.False(panel.HasPickaxeSweetSpot);
    }

    [Theory]
    [InlineData(99.1, "Pickaxe sweet spot: <1 QL")]
    [InlineData(-41, "Pickaxe sweet spot: >100 QL")]
    public void DisplaysPickaxeSweetSpotOutsideNormalRange(decimal miningSkill, string expected)
    {
        var panel = Panel(
            Snapshot("Alice", LiveSkillSourceState.Observing, Mining(miningSkill, 0m)),
            VeinMaterial.RockSalt);

        Assert.Equal(expected, panel.PickaxeSweetSpotDisplay);
    }

    private static ProfilePanelViewModel Panel(
        LiveSkillProfileSnapshot? snapshot,
        VeinMaterial? material = null) =>
        new(EventTransportTestProfileIds.Alice, "Alice", null, material is { } value ? VeinContext(value) : null, liveSkill: snapshot);

    private static ProfileVeinContextViewModel VeinContext(VeinMaterial material) =>
        new(new VeinContextSnapshot(
            EventTransportTestProfileIds.Alice,
            "Alice",
            material,
            new QualityMeasurement.Unknown(),
            new RemainingMeasurement.Unknown(),
            VeinConfidence.TypeOnly));

    private static LiveSkillProfileSnapshot Snapshot(
        string profileName,
        LiveSkillSourceState state,
        params SkillStateSnapshot[] skills) =>
        new(EventTransportTestProfileIds.Alice, profileName, "C:\\wurm\\players\\profile\\logs\\_Skills.2026-09.txt", state, skills);

    private static SkillStateSnapshot Mining(decimal current, decimal sessionGain) =>
        Skill("Mining", current, sessionGain);

    private static SkillStateSnapshot Skill(string name, decimal current, decimal sessionGain) =>
        new(name, current, null, sessionGain);
}
