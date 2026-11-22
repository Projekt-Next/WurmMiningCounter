using System.Globalization;

namespace WurmMiningCounter.Services;

public sealed class SessionBoundaryParser
{
    private const string LoggingStartedPrefix = "Logging started ";
    private const string ReconnectingPrefix = "Reconnecting ";

    public bool IsLoggingStarted(string line)
    {
        var normalizedLine = EventLogLineNormalizer.Normalize(line);
        if (!normalizedLine.StartsWith(LoggingStartedPrefix, StringComparison.Ordinal))
            return false;

        var date = normalizedLine.AsSpan(LoggingStartedPrefix.Length);
        return DateOnly.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    public bool IsReconnect(string line)
    {
        var normalizedLine = EventLogLineNormalizer.Normalize(line);
        if (!normalizedLine.StartsWith(ReconnectingPrefix, StringComparison.Ordinal)
            || !normalizedLine.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var playerAndServer = normalizedLine[
            ReconnectingPrefix.Length..
            ^1];
        const string separator = "! ";
        var separatorIndex = playerAndServer.IndexOf(separator, StringComparison.Ordinal);
        return separatorIndex > 0
            && separatorIndex == playerAndServer.LastIndexOf(separator, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(playerAndServer[..separatorIndex])
            && !string.IsNullOrWhiteSpace(playerAndServer[(separatorIndex + separator.Length)..]);
    }
}
