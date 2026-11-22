using System.Globalization;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public static class ProspectingLineParser
{
    private const string ProspectingStart = "You start to gather fragments of the rock.";
    private const string ProspectingStop = "You stop prospecting.";
    private const string NearbyScanPrefix = "There is ";
    private const string NearbyScanSuffix = " nearby.";
    private const string QualityPrefix = "It has a max quality of ";
    private const string RemainingPrefix = "You will be able to mine here ";
    private const string RemainingSuffix = " more times.";

    private static readonly IReadOnlyDictionary<string, VeinMaterial> MaterialsByName =
        new Dictionary<string, VeinMaterial>(StringComparer.Ordinal)
        {
            ["iron"] = VeinMaterial.Iron,
            ["copper"] = VeinMaterial.Copper,
            ["tin"] = VeinMaterial.Tin,
            ["zinc"] = VeinMaterial.Zinc,
            ["lead"] = VeinMaterial.Lead,
            ["silver"] = VeinMaterial.Silver,
            ["gold"] = VeinMaterial.Gold,
            ["sandstone"] = VeinMaterial.Sandstone,
            ["marble"] = VeinMaterial.Marble,
            ["slate"] = VeinMaterial.Slate,
            ["rock salt"] = VeinMaterial.RockSalt
        };

    private static readonly IReadOnlyDictionary<string, VeinMaterial> MaterialsByMessage =
        new Dictionary<string, VeinMaterial>(StringComparer.Ordinal)
        {
            ["You would mine iron ore here."] = MaterialsByName["iron"],
            ["You would mine copper ore here."] = MaterialsByName["copper"],
            ["You would mine tin ore here."] = MaterialsByName["tin"],
            ["You would mine zinc ore here."] = MaterialsByName["zinc"],
            ["You would mine lead ore here."] = MaterialsByName["lead"],
            ["You would mine silver ore here."] = MaterialsByName["silver"],
            ["You would mine gold ore here."] = MaterialsByName["gold"],
            ["You would mine adamantine ore here."] = VeinMaterial.Adamantine,
            ["You would mine glimmersteel ore here."] = VeinMaterial.Glimmersteel,
            ["You would mine sandstone shards here."] = MaterialsByName["sandstone"],
            ["You would mine marble shards here."] = MaterialsByName["marble"],
            ["You would mine slate shards here."] = MaterialsByName["slate"],
            ["You would mine rock salt here."] = MaterialsByName["rock salt"],
            ["You would mine salt rock salt here."] = MaterialsByName["rock salt"]
        };

    private static readonly IReadOnlySet<string> PlainRockMessages =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "You would mine stone shards here.",
            "You would mine stone stone shards here.",
            "You would mine stone rock shards here.",
            "You find only rock."
        };

    private static readonly IReadOnlyDictionary<string, ProspectingAugment> AugmentsByMessage =
        new Dictionary<string, ProspectingAugment>(StringComparer.Ordinal)
        {
            ["You will find salt here!"] = ProspectingAugment.Salt,
            ["You will find flint here!"] = ProspectingAugment.Flint
        };

    private static readonly IReadOnlyDictionary<string, ProspectingQualityGrade> QualityGradesByMessage =
        new Dictionary<string, ProspectingQualityGrade>(StringComparer.Ordinal)
        {
            ["It is of poor quality."] = ProspectingQualityGrade.Poor,
            ["It is of acceptable quality."] = ProspectingQualityGrade.Acceptable,
            ["It is of normal quality."] = ProspectingQualityGrade.Normal,
            ["It is of good quality."] = ProspectingQualityGrade.Good,
            ["It is of very good quality."] = ProspectingQualityGrade.VeryGood,
            ["It is of utmost quality."] = ProspectingQualityGrade.Utmost
        };

    private static readonly IReadOnlyDictionary<string, ProspectingRemainingBand> RemainingBandsByMessage =
        new Dictionary<string, ProspectingRemainingBand>(StringComparer.Ordinal)
        {
            ["There is plenty of ore left here."] = ProspectingRemainingBand.Plenty,
            ["Only a few weeks mining remain here."] = ProspectingRemainingBand.FewWeeks,
            ["The ore is starting to deplete."] = ProspectingRemainingBand.StartingToDeplete,
            ["You should start to prospect for another vein of this ore."] = ProspectingRemainingBand.ProspectAnotherVein,
            ["The ore will run out soon."] = ProspectingRemainingBand.RunOutSoon,
            ["The ore will run out any hour."] = ProspectingRemainingBand.RunOutAnyHour
        };

    public static bool IsProspectingStart(string text)
    {
        var normalizedText = EventLogLineNormalizer.Normalize(text);
        return string.Equals(normalizedText, ProspectingStart, StringComparison.Ordinal);
    }

    public static bool IsProspectingStop(string text)
    {
        var normalizedText = EventLogLineNormalizer.Normalize(text);
        return string.Equals(normalizedText, ProspectingStop, StringComparison.Ordinal);
    }

    public static bool TryParseMaterial(string text, out VeinMaterial material)
    {
        var normalizedText = EventLogLineNormalizer.Normalize(text);
        return MaterialsByMessage.TryGetValue(normalizedText, out material);
    }

    public static bool TryParseMaterialName(string materialName, out VeinMaterial material)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        return MaterialsByName.TryGetValue(materialName, out material);
    }

    public static bool IsPlainRock(string text)
    {
        var normalizedText = EventLogLineNormalizer.Normalize(text);
        return PlainRockMessages.Contains(normalizedText);
    }

    public static bool TryParseAugment(string text, out ProspectingAugment augment)
    {
        var normalizedText = EventLogLineNormalizer.Normalize(text);
        return AugmentsByMessage.TryGetValue(normalizedText, out augment);
    }

    public static bool TryParseNearbyScan(string text, out NearbyProspectingObservation observation)
    {
        observation = null!;

        var normalizedText = EventLogLineNormalizer.Normalize(text);
        if (!normalizedText.StartsWith(NearbyScanPrefix, StringComparison.Ordinal)
            || !normalizedText.EndsWith(NearbyScanSuffix, StringComparison.Ordinal)
            || normalizedText.Length <= NearbyScanPrefix.Length + NearbyScanSuffix.Length)
        {
            return false;
        }

        var rawDescription = normalizedText[
            NearbyScanPrefix.Length..
            ^NearbyScanSuffix.Length];

        if (string.Equals(rawDescription, "only rock", StringComparison.Ordinal))
        {
            observation = new NearbyProspectingObservation(rawDescription, [], true);
            return true;
        }

        var entries = rawDescription
            .Split([", ", " and "], StringSplitOptions.None)
            .Select(ParseNearbyScanEntry)
            .ToArray();
        if (entries.Any(entry => entry is null))
            return false;

        observation = new NearbyProspectingObservation(
            rawDescription,
            entries.Select(entry => entry!).ToArray(),
            false);
        return true;
    }

    private static NearbyProspectingEntry? ParseNearbyScanEntry(string entryText)
    {
        if (string.IsNullOrWhiteSpace(entryText))
            return null;

        const string qualitySuffix = " quality)";
        var qualityStart = entryText.LastIndexOf(" (", StringComparison.Ordinal);
        if (qualityStart < 0)
            return new NearbyProspectingEntry(entryText, null);

        if (!entryText.EndsWith(qualitySuffix, StringComparison.Ordinal)
            || qualityStart == 0)
        {
            return null;
        }

        var materialText = entryText[..qualityStart];
        var gradeText = entryText[(qualityStart + 2)..^qualitySuffix.Length];
        return string.IsNullOrWhiteSpace(gradeText)
            ? null
            : new NearbyProspectingEntry(materialText, gradeText);
    }

    public static bool TryParseExactMaxQuality(string text, out QualityMeasurement.ExactMax quality)
    {
        quality = null!;

        var normalizedText = EventLogLineNormalizer.Normalize(text);

        if (!normalizedText.StartsWith(QualityPrefix, StringComparison.Ordinal)
            || !normalizedText.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var value = normalizedText.AsSpan(
            QualityPrefix.Length,
            normalizedText.Length - QualityPrefix.Length - 1);
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return false;
        }

        quality = new QualityMeasurement.ExactMax(parsedValue);
        return true;
    }

    public static bool TryParseQualityGrade(string text, out QualityMeasurement.Grade quality)
    {
        quality = null!;

        var normalizedText = EventLogLineNormalizer.Normalize(text);
        if (!QualityGradesByMessage.TryGetValue(normalizedText, out var grade))
        {
            return false;
        }

        quality = new QualityMeasurement.Grade(grade);
        return true;
    }

    public static bool TryParseExactRemaining(string text, out RemainingMeasurement.Exact remaining)
    {
        remaining = null!;

        var normalizedText = EventLogLineNormalizer.Normalize(text);

        if (!normalizedText.StartsWith(RemainingPrefix, StringComparison.Ordinal)
            || !normalizedText.EndsWith(RemainingSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = normalizedText.AsSpan(
            RemainingPrefix.Length,
            normalizedText.Length - RemainingPrefix.Length - RemainingSuffix.Length);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return false;
        }

        remaining = new RemainingMeasurement.Exact(parsedValue);
        return true;
    }

    public static bool TryParseEstimateBand(string text, out RemainingMeasurement.EstimateBand remaining)
    {
        remaining = null!;

        var normalizedText = EventLogLineNormalizer.Normalize(text);
        if (!RemainingBandsByMessage.TryGetValue(normalizedText, out var band))
        {
            return false;
        }

        remaining = new RemainingMeasurement.EstimateBand(band);
        return true;
    }
}
