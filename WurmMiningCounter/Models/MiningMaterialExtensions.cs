namespace WurmMiningCounter.Models;

public static class MiningMaterialExtensions
{
    public static MiningYieldRole GetYieldRole(this MiningMaterial material)
    {
        return material switch
        {
            MiningMaterial.StoneShards or
            MiningMaterial.Ore or
            MiningMaterial.Shards or
            MiningMaterial.RockSalt => MiningYieldRole.Primary,

            MiningMaterial.Salt or
            MiningMaterial.Flint or
            MiningMaterial.SourceCrystal or
            MiningMaterial.Sapphire or
            MiningMaterial.Diamond or
            MiningMaterial.Emerald or
            MiningMaterial.Ruby or
            MiningMaterial.Opal => MiningYieldRole.Byproduct,

            _ => throw new ArgumentOutOfRangeException(nameof(material), material, null)
        };
    }

    public static bool IsPrimaryForCounter(this MiningMaterial material) =>
        material.GetYieldRole() == MiningYieldRole.Primary;

    public static bool IsBonusOrByproduct(this MiningMaterial material)
    {
        return material.GetYieldRole() == MiningYieldRole.Byproduct;
    }

    public static YieldKind? ToDepletionCompatibleYieldKind(this MiningMaterial material)
    {
        return material switch
        {
            MiningMaterial.Ore => YieldKind.Ore,
            MiningMaterial.Shards => YieldKind.Shards,
            MiningMaterial.RockSalt => YieldKind.RockSalt,
            _ => null
        };
    }
}
