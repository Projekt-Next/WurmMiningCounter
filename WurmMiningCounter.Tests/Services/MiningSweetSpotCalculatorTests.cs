using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class MiningSweetSpotCalculatorTests
{
    [Theory]
    [InlineData(VeinMaterial.Zinc, 2)]
    [InlineData(VeinMaterial.Iron, 3)]
    [InlineData(VeinMaterial.Tin, 10)]
    [InlineData(VeinMaterial.Copper, 20)]
    [InlineData(VeinMaterial.Slate, 20)]
    [InlineData(VeinMaterial.Lead, 20)]
    [InlineData(VeinMaterial.RockSalt, 30)]
    [InlineData(VeinMaterial.Silver, 35)]
    [InlineData(VeinMaterial.Gold, 40)]
    [InlineData(VeinMaterial.Marble, 40)]
    [InlineData(VeinMaterial.Sandstone, 45)]
    public void GetDifficulty_ReturnsExpectedDifficulty(VeinMaterial material, decimal expected) =>
        Assert.Equal(expected, MiningSweetSpotCalculator.GetDifficulty(material));

    [Fact]
    public void GetDifficulty_ThrowsForUnsupportedMaterial() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MiningSweetSpotCalculator.GetDifficulty((VeinMaterial)int.MaxValue));

    [Theory]
    [InlineData(70, VeinMaterial.RockSalt, 30)]
    [InlineData(70, VeinMaterial.Silver, 40)]
    [InlineData(70, VeinMaterial.Gold, 50)]
    [InlineData(70.3241, VeinMaterial.RockSalt, 29.6759)]
    public void CalculateRecommendedPickaxeQl_UsesDifficultyAndMiningSkill(
        decimal miningSkill,
        VeinMaterial material,
        decimal expected) =>
        Assert.Equal(expected, MiningSweetSpotCalculator.CalculateRecommendedPickaxeQl(miningSkill, material));
}
