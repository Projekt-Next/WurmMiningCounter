using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public static class VeinEmergenceParser
{
    private const string Prefix = "A vein of pure ";
    private const string Suffix = " emerges here.";

    private static readonly IReadOnlyDictionary<string, VeinMaterial> MaterialsByDescription =
        new Dictionary<string, VeinMaterial>(StringComparer.Ordinal)
        {
            ["A vein of black adamantine emerges here."] = VeinMaterial.Adamantine,
            ["A vein of shiny glimmersteel emerges here."] = VeinMaterial.Glimmersteel,
            ["A vein of an interlocking mosaic of carbonate crystals, otherwise known as marble, emerges here."] = VeinMaterial.Marble,
            ["A vein of fine-grained, foliated, homogeneous metamorphic rock, or slate as we know it, emerges here."] = VeinMaterial.Slate
        };

    public static bool TryParse(string text, out VeinMaterial material)
    {
        material = default;

        var normalizedText = EventLogLineNormalizer.Normalize(text);
        if (MaterialsByDescription.TryGetValue(normalizedText, out material))
            return true;

        if (!normalizedText.StartsWith(Prefix, StringComparison.Ordinal)
            || !normalizedText.EndsWith(Suffix, StringComparison.Ordinal)
            || normalizedText.Length <= Prefix.Length + Suffix.Length)
        {
            return false;
        }

        var materialName = normalizedText[Prefix.Length..^Suffix.Length];
        return ProspectingLineParser.TryParseMaterialName(materialName, out material);
    }
}
