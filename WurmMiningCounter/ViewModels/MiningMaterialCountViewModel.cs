using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class MiningMaterialCountViewModel
{
    public MiningMaterialCountViewModel(MiningMaterial material, long count)
    {
        Material = material;
        Count = count;
    }

    public MiningMaterial Material { get; }

    public string DisplayName => GetDisplayName(Material);

    public static string GetDisplayName(MiningMaterial material) => material switch
    {
        MiningMaterial.StoneShards => "Stone Shards",
        MiningMaterial.Ore => "Ore",
        MiningMaterial.Shards => "Shards",
        MiningMaterial.RockSalt => "Rock Salt",
        MiningMaterial.Salt => "Salt",
        MiningMaterial.Flint => "Flint",
        MiningMaterial.SourceCrystal => "Source Crystals",
        MiningMaterial.Sapphire => "Sapphire",
        MiningMaterial.Diamond => "Diamond",
        MiningMaterial.Emerald => "Emerald",
        MiningMaterial.Ruby => "Ruby",
        MiningMaterial.Opal => "Opal",
        _ => material.ToString()
    };

    public long Count { get; }
}
