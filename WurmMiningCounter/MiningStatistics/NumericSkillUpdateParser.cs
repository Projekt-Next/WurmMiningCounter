using System.Globalization;
using System.Text.RegularExpressions;

namespace WurmMiningCounter.MiningStatistics;

/// <summary>
/// Parses complete numeric skill updates from the Wurm _Skills log format.
/// </summary>
public static class NumericSkillUpdateParser
{
    private static readonly CultureInfo DecimalCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly Regex UpdatePattern = new(
        @"^\[(?<timestamp>(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d)\] (?<skillName>\S(?:.*\S)?) (?<direction>increased|decreased) by (?<delta>\d+,(?:\d{4}|\d{6})) to (?<currentValue>-?\d+,(?:\d{4}|\d{6}))\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static NumericSkillUpdate? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var match = UpdatePattern.Match(line);
        if (!match.Success
            || !TimeOnly.TryParseExact(
                match.Groups["timestamp"].Value,
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp)
            || !TryParseDecimal(match.Groups["delta"].Value, out var delta)
            || !TryParseDecimal(match.Groups["currentValue"].Value, out var currentValue))
        {
            return null;
        }

        var direction = match.Groups["direction"].Value == "increased"
            ? NumericSkillUpdateDirection.Increased
            : NumericSkillUpdateDirection.Decreased;

        return new NumericSkillUpdate(
            timestamp,
            match.Groups["skillName"].Value,
            direction,
            delta,
            currentValue);
    }

    private static bool TryParseDecimal(string value, out decimal result) =>
        decimal.TryParse(
            value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            DecimalCulture,
            out result);
}
