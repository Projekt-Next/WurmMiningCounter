using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public static class MiningSweetSpotCalculator
{
    public static decimal GetDifficulty(VeinMaterial material) => material switch
    {
        VeinMaterial.Zinc => 2m,
        VeinMaterial.Iron => 3m,
        VeinMaterial.Tin => 10m,
        VeinMaterial.Copper => 20m,
        VeinMaterial.Slate => 20m,
        VeinMaterial.Lead => 20m,
        VeinMaterial.RockSalt => 30m,
        VeinMaterial.Silver => 35m,
        VeinMaterial.Gold => 40m,
        VeinMaterial.Marble => 40m,
        VeinMaterial.Sandstone => 45m,
        _ => throw new ArgumentOutOfRangeException(nameof(material), material, "Unsupported vein material.")
    };

    public static decimal CalculateRecommendedPickaxeQl(decimal miningSkill, VeinMaterial material) =>
        2m * (GetDifficulty(material) + 20m) - miningSkill;
}
