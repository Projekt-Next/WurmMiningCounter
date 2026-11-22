using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class VeinExamineParser
{
    private static readonly IReadOnlyDictionary<string, VeinMaterial> MaterialsByExamine =
        new Dictionary<string, VeinMaterial>(StringComparer.Ordinal)
        {
            ["A vein of sand-sized minerals or rock grains, composed of quartz or feldspar, because these are the most common minerals in the Wurm's crust."] = VeinMaterial.Sandstone,
            ["A vein of crystalized sediment formed over time by evaporation."] = VeinMaterial.RockSalt
        };

    public VeinMaterial? Parse(string line)
    {
        var normalizedLine = EventLogLineNormalizer.Normalize(line);

        return MaterialsByExamine.TryGetValue(normalizedLine, out var material)
            ? material
            : null;
    }
}
