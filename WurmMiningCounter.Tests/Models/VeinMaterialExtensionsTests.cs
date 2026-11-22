using WurmMiningCounter.Models;

namespace WurmMiningCounter.Tests.Models;

public sealed class VeinMaterialExtensionsTests
{
    [Theory]
    [InlineData(VeinMaterial.Iron, YieldKind.Ore)]
    [InlineData(VeinMaterial.Copper, YieldKind.Ore)]
    [InlineData(VeinMaterial.Tin, YieldKind.Ore)]
    [InlineData(VeinMaterial.Zinc, YieldKind.Ore)]
    [InlineData(VeinMaterial.Lead, YieldKind.Ore)]
    [InlineData(VeinMaterial.Silver, YieldKind.Ore)]
    [InlineData(VeinMaterial.Gold, YieldKind.Ore)]
    [InlineData(VeinMaterial.Adamantine, YieldKind.Ore)]
    [InlineData(VeinMaterial.Glimmersteel, YieldKind.Ore)]
    [InlineData(VeinMaterial.Sandstone, YieldKind.Shards)]
    [InlineData(VeinMaterial.Marble, YieldKind.Shards)]
    [InlineData(VeinMaterial.Slate, YieldKind.Shards)]
    [InlineData(VeinMaterial.RockSalt, YieldKind.RockSalt)]
    public void ToYieldKindMapsEveryVeinMaterial(VeinMaterial material, YieldKind expectedYieldKind)
    {
        Assert.Equal(expectedYieldKind, material.ToYieldKind());
    }

    [Fact]
    public void ToYieldKindWithUnsupportedValueThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((VeinMaterial)int.MaxValue).ToYieldKind());
    }
}
