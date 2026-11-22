using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class ResetSelectionViewModelTests
{
    [Fact]
    public void NoSelectionDisablesReset()
    {
        var selection = new ResetSelectionViewModel(CreateViewModel());

        Assert.False(selection.CanReset);
    }

    [Fact]
    public void ResetSessionDisablesAndClearsPerProfileSelections()
    {
        var selection = new ResetSelectionViewModel(CreateViewModel(withVeinContext: true));
        var option = Assert.Single(selection.ProfileOptions);
        option.ResetMaterialCounters = true;
        option.ResetVeinContext = true;
        Assert.Single(option.MaterialOptions).IsSelected = true;

        selection.ResetSession = true;

        Assert.True(selection.CanReset);
        Assert.False(option.IsMaterialCountersResetEnabled);
        Assert.False(option.IsVeinContextResetEnabled);
        Assert.False(option.ResetMaterialCounters);
        Assert.False(option.ResetVeinContext);
        Assert.False(option.HasSelectedMaterials);
    }

    [Fact]
    public void ClearingResetSessionEnablesPerProfileOptionsAgain()
    {
        var selection = new ResetSelectionViewModel(CreateViewModel(withVeinContext: true));
        var option = Assert.Single(selection.ProfileOptions);
        selection.ResetSession = true;

        selection.ResetSession = false;

        Assert.True(option.IsMaterialCountersResetEnabled);
        Assert.True(option.IsVeinContextResetEnabled);
        option.ResetMaterialCounters = true;
        Assert.True(selection.CanReset);
    }

    [Fact]
    public async Task MaterialCountersResetPreservesTotalAndVeinContext()
    {
        var viewModel = CreateViewModel(withVeinContext: true);
        var selection = new ResetSelectionViewModel(viewModel);
        Assert.Single(selection.ProfileOptions).ResetMaterialCounters = true;

        var completed = await selection.ConfirmAndExecuteAsync(_ => true);

        Assert.True(completed);
        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal(1, profile.TotalCount);
        Assert.Empty(profile.Materials);
        Assert.True(Assert.Single(viewModel.ProfilePanels).HasVeinContext);
    }

    [Fact]
    public async Task VeinContextResetPreservesCountersAndTotalAndLeavesOtherProfilesUntouched()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Bob, "Bob", new MiningEvent(MiningMaterial.Diamond));
        MainViewModel? viewModel = null;
        viewModel = new MainViewModel(
            counter,
            initialVeinContexts: [Context("Alice", VeinMaterial.Iron), Context("Bob", VeinMaterial.Zinc)],
            resetVeinContextAsync: profileId =>
            {
                viewModel!.ApplyVeinContextSnapshot(profileId, null);
                return Task.CompletedTask;
            });
        var selection = new ResetSelectionViewModel(viewModel);
        selection.ProfileOptions.Single(option => option.ProfileName == "Alice").ResetVeinContext = true;

        await selection.ConfirmAndExecuteAsync(_ => true);

        var alice = viewModel.Profiles.Single(profile => profile.ProfileName == "Alice");
        Assert.Equal(1, alice.TotalCount);
        Assert.Equal(1, alice.Materials.Single().Count);
        Assert.False(viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Alice").HasVeinContext);
        var bob = viewModel.Profiles.Single(profile => profile.ProfileName == "Bob");
        Assert.Equal(0, bob.TotalCount);
        Assert.Equal(1, bob.Materials.Single().Count);
        Assert.True(viewModel.ProfilePanels.Single(panel => panel.ProfileName == "Bob").HasVeinContext);
    }

    [Fact]
    public void BothPerProfileResetsCanBeSelectedTogether()
    {
        var selection = new ResetSelectionViewModel(CreateViewModel(withVeinContext: true));
        var option = Assert.Single(selection.ProfileOptions);

        option.ResetMaterialCounters = true;
        option.ResetVeinContext = true;

        Assert.True(selection.CanReset);
        Assert.Contains("- Material counters", selection.ConfirmationText);
        Assert.Contains("- Vein Context", selection.ConfirmationText);
    }

    [Fact]
    public void MaterialOptionsContainOnlyPositiveSnapshotMaterialsWithDisplayData()
    {
        var option = new ProfileResetOptionViewModel(
            EventTransportTestProfileIds.Alice,
            "Alice",
            [
                new MiningMaterialCountViewModel(MiningMaterial.Ore, 2),
                new MiningMaterialCountViewModel(MiningMaterial.Flint, 0),
                new MiningMaterialCountViewModel(MiningMaterial.Ruby, -1)
            ],
            canResetVeinContext: false);

        var material = Assert.Single(option.MaterialOptions);
        Assert.Equal(MiningMaterial.Ore, material.Material);
        Assert.Equal("Ore", material.DisplayName);
        Assert.Equal(2, material.Count);
    }

    [Fact]
    public void SelectAllAndClearMaterialSelectionsUpdateSelectionState()
    {
        var selection = new ResetSelectionViewModel(CreateViewModelWithMaterials());
        var option = Assert.Single(selection.ProfileOptions);

        Assert.Equal("Select All", option.SelectAllMaterialsText);
        option.SelectAllMaterials();

        Assert.True(option.HasSelectedMaterials);
        Assert.True(option.AreAllMaterialsSelected);
        Assert.Equal("Clear All", option.SelectAllMaterialsText);
        Assert.All(option.MaterialOptions, material => Assert.True(material.IsSelected));
        Assert.True(selection.CanReset);

        option.SelectAllMaterials();

        Assert.False(option.HasSelectedMaterials);
        Assert.False(option.AreAllMaterialsSelected);
        Assert.Equal("Select All", option.SelectAllMaterialsText);
        Assert.All(option.MaterialOptions, material => Assert.False(material.IsSelected));
        Assert.False(selection.CanReset);
    }

    [Fact]
    public void PartialSelectionKeepsSelectAllTextAndClearAllPreservesOtherResetOperations()
    {
        var selection = new ResetSelectionViewModel(CreateViewModelWithMaterials(withVeinContext: true));
        var option = Assert.Single(selection.ProfileOptions);
        option.MaterialOptions[0].IsSelected = true;

        Assert.Equal("Select All", option.SelectAllMaterialsText);

        option.ResetVeinContext = true;
        option.SelectAllMaterials();
        option.SelectAllMaterials();

        Assert.False(option.HasSelectedMaterials);
        Assert.True(option.ResetVeinContext);
        Assert.True(selection.CanReset);
    }

    [Fact]
    public void ResetAllAndSelectiveMaterialSelectionsAreMutuallyExclusive()
    {
        var selection = new ResetSelectionViewModel(CreateViewModelWithMaterials());
        var option = Assert.Single(selection.ProfileOptions);
        option.MaterialOptions[0].IsSelected = true;

        option.ResetMaterialCounters = true;

        Assert.False(option.HasSelectedMaterials);
        Assert.True(option.ResetMaterialCounters);

        option.MaterialOptions[1].IsSelected = true;

        Assert.False(option.ResetMaterialCounters);
        Assert.True(option.HasSelectedMaterials);
    }

    [Fact]
    public void ResetVeinContextRemainsIndependentFromSelectiveMaterials()
    {
        var selection = new ResetSelectionViewModel(CreateViewModelWithMaterials(withVeinContext: true));
        var option = Assert.Single(selection.ProfileOptions);

        option.ResetVeinContext = true;
        option.MaterialOptions[0].IsSelected = true;

        Assert.True(option.ResetVeinContext);
        Assert.True(option.HasSelectedMaterials);
        Assert.False(option.ResetMaterialCounters);
    }

    [Fact]
    public void SelectiveResetConfirmationIncludesSelectedMaterialCountsAndPreservationNotice()
    {
        var selection = new ResetSelectionViewModel(CreateViewModelWithMaterials());
        var option = Assert.Single(selection.ProfileOptions);
        option.MaterialOptions.Single(material => material.Material == MiningMaterial.Ore).IsSelected = true;
        option.MaterialOptions.Single(material => material.Material == MiningMaterial.Flint).IsSelected = true;

        var confirmation = selection.ConfirmationText;

        Assert.Contains("Ore: 1", confirmation);
        Assert.Contains("Flint: 1", confirmation);
        Assert.Contains("Total and other counters will not be affected.", confirmation);
    }

    [Fact]
    public async Task ExecuteSelectiveResetResetsAllSelectedMaterialsInOneCounterChange()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Flint));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ruby));
        var changes = 0;
        counter.CountsChanged += (_, eventArgs) =>
        {
            if (eventArgs.ProfileId == EventTransportTestProfileIds.Alice)
                changes++;
        };
        var selection = new ResetSelectionViewModel(new MainViewModel(counter));
        var option = Assert.Single(selection.ProfileOptions);
        option.MaterialOptions.Single(material => material.Material == MiningMaterial.Ore).IsSelected = true;
        option.MaterialOptions.Single(material => material.Material == MiningMaterial.Flint).IsSelected = true;

        await selection.ExecuteAsync();

        var profile = counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice];
        Assert.Equal(1, profile.TotalCount);
        Assert.DoesNotContain(MiningMaterial.Ore, profile.Materials.Keys);
        Assert.DoesNotContain(MiningMaterial.Flint, profile.Materials.Keys);
        Assert.Equal(1, profile.Materials[MiningMaterial.Ruby]);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task NoMaterialSelectionDoesNotExecuteSelectiveReset()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        var selection = new ResetSelectionViewModel(new MainViewModel(counter));

        await selection.ExecuteAsync();

        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].Materials[MiningMaterial.Ore]);
    }

    [Fact]
    public async Task CancellingConfirmationExecutesNoOperations()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        var resetSessionCalls = 0;
        var viewModel = new MainViewModel(counter, () =>
        {
            resetSessionCalls++;
            counter.Reset();
            return Task.CompletedTask;
        });
        var selection = new ResetSelectionViewModel(viewModel) { ResetSession = true };

        var completed = await selection.ConfirmAndExecuteAsync(_ => false);

        Assert.False(completed);
        Assert.Equal(0, resetSessionCalls);
        Assert.Equal(1, counter.GetProfileSnapshots()[EventTransportTestProfileIds.Alice].TotalCount);
    }

    [Fact]
    public async Task ResetSessionDoesNotExecuteAdditionalPerProfileResets()
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        var resetSessionCalls = 0;
        var resetVeinCalls = 0;
        var viewModel = new MainViewModel(
            counter,
            () =>
            {
                resetSessionCalls++;
                counter.Reset();
                return Task.CompletedTask;
            },
            initialVeinContexts: [Context("Alice", VeinMaterial.Iron)],
            resetVeinContextAsync: _ =>
            {
                resetVeinCalls++;
                return Task.CompletedTask;
            });
        var selection = new ResetSelectionViewModel(viewModel);
        var option = Assert.Single(selection.ProfileOptions);
        option.ResetMaterialCounters = true;
        option.ResetVeinContext = true;
        selection.ResetSession = true;

        await selection.ConfirmAndExecuteAsync(_ => true);

        Assert.Equal(1, resetSessionCalls);
        Assert.Equal(0, resetVeinCalls);
        Assert.Empty(counter.GetProfileSnapshots());
    }

    private static MainViewModel CreateViewModel(bool withVeinContext = false)
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        return new MainViewModel(
            counter,
            initialVeinContexts: withVeinContext ? [Context("Alice", VeinMaterial.Iron)] : []);
    }

    private static MainViewModel CreateViewModelWithMaterials(bool withVeinContext = false)
    {
        var counter = new MiningSessionCounter();
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Ore));
        counter.Add(EventTransportTestProfileIds.Alice, "Alice", new MiningEvent(MiningMaterial.Flint));
        return new MainViewModel(
            counter,
            initialVeinContexts: withVeinContext ? [Context("Alice", VeinMaterial.Iron)] : []);
    }

    private static VeinContextSnapshot Context(string profileName, VeinMaterial material) =>
        new(
            profileName == "Alice" ? EventTransportTestProfileIds.Alice : EventTransportTestProfileIds.Bob,
            profileName,
            material,
            new QualityMeasurement.ExactMax(12.5m),
            new RemainingMeasurement.Exact(1103),
            VeinConfidence.Synchronized);
}
