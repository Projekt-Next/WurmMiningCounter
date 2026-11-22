namespace WurmMiningCounter.Services;

public static class EventLogLineNormalizer
{
    public static string Normalize(string line)
    {
        return HasTimestampPrefix(line) ? line[11..] : line;
    }

    private static bool HasTimestampPrefix(string line)
    {
        if (line.Length < 11
            || line[0] != '['
            || line[3] != ':'
            || line[6] != ':'
            || line[9] != ']'
            || line[10] != ' ')
        {
            return false;
        }

        return TryReadTwoAsciiDigits(line, 1, out var hours)
            && TryReadTwoAsciiDigits(line, 4, out var minutes)
            && TryReadTwoAsciiDigits(line, 7, out var seconds)
            && hours <= 23
            && minutes <= 59
            && seconds <= 59;
    }

    private static bool TryReadTwoAsciiDigits(string value, int index, out int number)
    {
        var firstDigit = value[index];
        var secondDigit = value[index + 1];
        if (firstDigit is < '0' or > '9' || secondDigit is < '0' or > '9')
        {
            number = 0;
            return false;
        }

        number = ((firstDigit - '0') * 10) + (secondDigit - '0');
        return true;
    }
}
