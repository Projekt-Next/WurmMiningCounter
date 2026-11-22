using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Tests.Services;

public sealed class VeinExamineParserTests
{
    private const string SandstoneExamine =
        "A vein of sand-sized minerals or rock grains, composed of quartz or feldspar, because these are the most common minerals in the Wurm's crust.";
    private const string RockSaltExamine =
        "A vein of crystalized sediment formed over time by evaporation.";

    [Fact]
    public void Parse_RecognizesConfirmedSandstoneExamine()
    {
        var result = new VeinExamineParser().Parse(SandstoneExamine);

        Assert.Equal(VeinMaterial.Sandstone, result);
    }

    [Theory]
    [InlineData(RockSaltExamine)]
    [InlineData("[12:00:00] A vein of crystalized sediment formed over time by evaporation.")]
    public void Parse_RecognizesConfirmedRockSaltExamine(string line)
    {
        var result = new VeinExamineParser().Parse(line);

        Assert.Equal(VeinMaterial.RockSalt, result);
    }

    [Theory]
    [InlineData("A vein of crystallized sediment formed over time by evaporation.")]
    [InlineData("A vein of crystalized sediment formed over time by evaporation")]
    [InlineData("a vein of crystalized sediment formed over time by evaporation.")]
    [InlineData("A vein of crystalized sediment formed by evaporation.")]
    public void Parse_DoesNotRecognizeSimilarOrCaseChangedRockSaltText(string line)
    {
        var result = new VeinExamineParser().Parse(line);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_DoesNotRecognizeSimilarChangedText()
    {
        var result = new VeinExamineParser().Parse(
            "A vein of sand-sized minerals or rock grains, composed of quartz or granite, because these are the most common minerals in the Wurm's crust.");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_DoesNotRecognizeUnrelatedText()
    {
        var result = new VeinExamineParser().Parse("Some unrelated message.");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("You would mine iron here.")]
    [InlineData("You would mine rock salt here.")]
    public void Parse_DoesNotRecognizeOtherMaterialMessages(string line)
    {
        var result = new VeinExamineParser().Parse(line);

        Assert.Null(result);
    }
}
