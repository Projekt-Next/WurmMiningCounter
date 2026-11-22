namespace WurmMiningCounter.Models;

public static class VeinMaterialExtensions
{
    public static YieldKind ToYieldKind(this VeinMaterial material)
    {
        return material switch
        {
            VeinMaterial.Iron or
            VeinMaterial.Copper or
            VeinMaterial.Tin or
            VeinMaterial.Zinc or
            VeinMaterial.Lead or
            VeinMaterial.Silver or
            VeinMaterial.Gold or
            VeinMaterial.Adamantine or
            VeinMaterial.Glimmersteel => YieldKind.Ore,
            VeinMaterial.Sandstone or
            VeinMaterial.Marble or
            VeinMaterial.Slate => YieldKind.Shards,
            VeinMaterial.RockSalt => YieldKind.RockSalt,
            _ => throw new ArgumentOutOfRangeException(nameof(material), material, null)
        };
    }
}
