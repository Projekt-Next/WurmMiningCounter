using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Models;

public sealed class MiningMaterialExtensionsTests
{
    [Theory]
    [InlineData(MiningMaterial.StoneShards, MiningYieldRole.Primary)]
    [InlineData(MiningMaterial.Ore, MiningYieldRole.Primary)]
    [InlineData(MiningMaterial.Shards, MiningYieldRole.Primary)]
    [InlineData(MiningMaterial.RockSalt, MiningYieldRole.Primary)]
    [InlineData(MiningMaterial.Salt, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.Flint, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.SourceCrystal, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.Sapphire, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.Diamond, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.Emerald, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.Ruby, MiningYieldRole.Byproduct)]
    [InlineData(MiningMaterial.Opal, MiningYieldRole.Byproduct)]
    public void GetYieldRoleClassifiesEveryMiningMaterial(MiningMaterial material, MiningYieldRole expected)
    {
        Assert.Equal(expected, material.GetYieldRole());
    }

    [Theory]
    [InlineData(MiningMaterial.StoneShards, false)]
    [InlineData(MiningMaterial.Ore, false)]
    [InlineData(MiningMaterial.Shards, false)]
    [InlineData(MiningMaterial.RockSalt, false)]
    [InlineData(MiningMaterial.Salt, true)]
    [InlineData(MiningMaterial.Flint, true)]
    [InlineData(MiningMaterial.SourceCrystal, true)]
    [InlineData(MiningMaterial.Sapphire, true)]
    [InlineData(MiningMaterial.Diamond, true)]
    [InlineData(MiningMaterial.Emerald, true)]
    [InlineData(MiningMaterial.Ruby, true)]
    [InlineData(MiningMaterial.Opal, true)]
    public void IsBonusOrByproductClassifiesEveryMiningMaterial(MiningMaterial material, bool expected)
    {
        Assert.Equal(expected, material.IsBonusOrByproduct());
    }

    [Theory]
    [InlineData(MiningMaterial.StoneShards, null)]
    [InlineData(MiningMaterial.Ore, YieldKind.Ore)]
    [InlineData(MiningMaterial.Shards, YieldKind.Shards)]
    [InlineData(MiningMaterial.RockSalt, YieldKind.RockSalt)]
    [InlineData(MiningMaterial.Salt, null)]
    [InlineData(MiningMaterial.Flint, null)]
    [InlineData(MiningMaterial.SourceCrystal, null)]
    [InlineData(MiningMaterial.Sapphire, null)]
    [InlineData(MiningMaterial.Diamond, null)]
    [InlineData(MiningMaterial.Emerald, null)]
    [InlineData(MiningMaterial.Ruby, null)]
    [InlineData(MiningMaterial.Opal, null)]
    public void ToDepletionCompatibleYieldKindClassifiesEveryMiningMaterial(
        MiningMaterial material,
        YieldKind? expected)
    {
        Assert.Equal(expected, material.ToDepletionCompatibleYieldKind());
    }
}
