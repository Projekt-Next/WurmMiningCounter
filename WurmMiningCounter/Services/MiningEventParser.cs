using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class MiningEventParser
{
    private static readonly IReadOnlyDictionary<string, MiningMaterial> MaterialsByMessage =
        new Dictionary<string, MiningMaterial>(StringComparer.Ordinal)
        {
            ["You mine some stone shards."] = MiningMaterial.StoneShards,
            ["You mine some ore."] = MiningMaterial.Ore,
            ["You mine some shards."] = MiningMaterial.Shards,
            ["You mine some rock salt."] = MiningMaterial.RockSalt,
            ["You mine some pink crystals."] = MiningMaterial.SourceCrystal,
            ["You mine some salt."] = MiningMaterial.Salt,
            ["You find flint!"] = MiningMaterial.Flint,
            ["You get a source crystal."] = MiningMaterial.SourceCrystal,
            ["You find a sapphire!"] = MiningMaterial.Sapphire,
            ["You find a diamond!"] = MiningMaterial.Diamond,
            ["You find an emerald!"] = MiningMaterial.Emerald,
            ["You find a ruby!"] = MiningMaterial.Ruby,
            ["You find an opal!"] = MiningMaterial.Opal
        };

    public MiningEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var message = EventLogLineNormalizer.Normalize(line);
        return MaterialsByMessage.TryGetValue(message, out var material)
            ? new MiningEvent(material)
            : null;
    }
}
