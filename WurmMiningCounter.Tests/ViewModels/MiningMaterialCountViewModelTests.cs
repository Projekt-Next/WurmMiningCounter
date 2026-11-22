using WurmMiningCounter.Models;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Tests.ViewModels;

public sealed class MiningMaterialCountViewModelTests
{
    [Theory]
    [InlineData(MiningMaterial.StoneShards, "Stone Shards")]
    [InlineData(MiningMaterial.Ore, "Ore")]
    [InlineData(MiningMaterial.Shards, "Shards")]
    [InlineData(MiningMaterial.RockSalt, "Rock Salt")]
    [InlineData(MiningMaterial.Salt, "Salt")]
    [InlineData(MiningMaterial.Flint, "Flint")]
    [InlineData(MiningMaterial.SourceCrystal, "Source Crystals")]
    [InlineData(MiningMaterial.Sapphire, "Sapphire")]
    [InlineData(MiningMaterial.Diamond, "Diamond")]
    [InlineData(MiningMaterial.Emerald, "Emerald")]
    [InlineData(MiningMaterial.Ruby, "Ruby")]
    [InlineData(MiningMaterial.Opal, "Opal")]
    public void DisplayNameMatchesMaterial(MiningMaterial material, string expectedDisplayName)
    {
        var viewModel = new MiningMaterialCountViewModel(material, 1);

        Assert.Equal(expectedDisplayName, viewModel.DisplayName);
    }
}
