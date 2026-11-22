using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class VeinEmergenceParserTests
{
    [Theory]
    [InlineData("A vein of pure iron emerges here.", VeinMaterial.Iron)]
    [InlineData("A vein of pure zinc emerges here.", VeinMaterial.Zinc)]
    [InlineData("A vein of pure lead emerges here.", VeinMaterial.Lead)]
    [InlineData("A vein of pure gold emerges here.", VeinMaterial.Gold)]
    [InlineData("A vein of black adamantine emerges here.", VeinMaterial.Adamantine)]
    [InlineData("A vein of shiny glimmersteel emerges here.", VeinMaterial.Glimmersteel)]
    [InlineData("A vein of pure rock salt emerges here.", VeinMaterial.RockSalt)]
    [InlineData("A vein of an interlocking mosaic of carbonate crystals, otherwise known as marble, emerges here.", VeinMaterial.Marble)]
    [InlineData("A vein of fine-grained, foliated, homogeneous metamorphic rock, or slate as we know it, emerges here.", VeinMaterial.Slate)]
    public void TryParse_RecognizesKnownMaterial(string text, VeinMaterial expectedMaterial)
    {
        var parsed = VeinEmergenceParser.TryParse(text, out var material);

        Assert.True(parsed);
        Assert.Equal(expectedMaterial, material);
    }

    [Fact]
    public void TryParse_NormalizesTimestamp()
    {
        var parsed = VeinEmergenceParser.TryParse(
            "[12:00:00] A vein of pure iron emerges here.",
            out var material);

        Assert.True(parsed);
        Assert.Equal(VeinMaterial.Iron, material);
    }

    [Fact]
    public void TryParse_NormalizesTimestampForDescriptiveEmergence()
    {
        var parsed = VeinEmergenceParser.TryParse(
            "[12:00:00] A vein of an interlocking mosaic of carbonate crystals, otherwise known as marble, emerges here.",
            out var material);

        Assert.True(parsed);
        Assert.Equal(VeinMaterial.Marble, material);
    }

    [Theory]
    [InlineData("A vein of pure adamantine emerges here.")]
    [InlineData("A vein of pure glimmersteel emerges here.")]
    [InlineData("A vein of pure Iron emerges here.")]
    [InlineData("A vein of pure iron emerges here")]
    [InlineData("A vein of iron emerges here.")]
    public void TryParse_DoesNotInferUnknownOrMalformedMaterial(string text)
    {
        Assert.False(VeinEmergenceParser.TryParse(text, out _));
    }
}
