using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WurmMiningCounter.Services;

internal enum EventLogSegmentFormat
{
    Unknown,
    Monthly,
    Daily
}

internal readonly record struct EventLogSegment(
    EventLogSegmentFormat Format,
    DateOnly Start,
    DateOnly EndExclusive);

internal static partial class EventLogSegmentOrder
{
    public static EventLogSegment Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var name = Path.GetFileName(filePath);
        var match = EventLogFileName().Match(name);
        if (!match.Success
            || !int.TryParse(match.Groups["year"].Value, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(match.Groups["month"].Value, CultureInfo.InvariantCulture, out var month))
        {
            return default;
        }

        try
        {
            if (!match.Groups["day"].Success)
            {
                var start = new DateOnly(year, month, 1);
                return new EventLogSegment(EventLogSegmentFormat.Monthly, start, start.AddMonths(1));
            }

            if (!int.TryParse(match.Groups["day"].Value, CultureInfo.InvariantCulture, out var day))
                return default;

            var dailyStart = new DateOnly(year, month, day);
            return new EventLogSegment(EventLogSegmentFormat.Daily, dailyStart, dailyStart.AddDays(1));
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    [GeneratedRegex("^_Event\\.(?<year>\\d{4})-(?<month>\\d{2})(?:-(?<day>\\d{2}))?\\.(?i:txt)$", RegexOptions.CultureInvariant)]
    private static partial Regex EventLogFileName();
}
