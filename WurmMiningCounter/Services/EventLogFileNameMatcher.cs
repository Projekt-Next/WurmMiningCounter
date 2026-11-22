namespace WurmMiningCounter.Services;

internal static class EventLogFileNameMatcher
{
    public static bool IsMatch(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)
            || !fileName.StartsWith("_Event.", StringComparison.Ordinal)
            || !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fileName.Length switch
        {
            18 => IsMonthOnlyFormat(fileName),
            21 => IsMonthAndDayFormat(fileName),
            _ => false
        };
    }

    private static bool IsMonthOnlyFormat(string fileName)
    {
        return fileName[11] == '-'
            && AreAsciiDigits(fileName, startIndex: 7, length: 4)
            && TryReadTwoAsciiDigits(fileName, startIndex: 12, out var month)
            && month is >= 1 and <= 12;
    }

    private static bool IsMonthAndDayFormat(string fileName)
    {
        return fileName[11] == '-'
            && fileName[14] == '-'
            && AreAsciiDigits(fileName, startIndex: 7, length: 4)
            && TryReadTwoAsciiDigits(fileName, startIndex: 12, out var month)
            && month is >= 1 and <= 12
            && TryReadTwoAsciiDigits(fileName, startIndex: 15, out var day)
            && day is >= 1 and <= 31;
    }

    private static bool AreAsciiDigits(string value, int startIndex, int length)
    {
        for (var index = startIndex; index < startIndex + length; index++)
        {
            if (value[index] is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static bool TryReadTwoAsciiDigits(string value, int startIndex, out int number)
    {
        var firstDigit = value[startIndex];
        var secondDigit = value[startIndex + 1];
        if (firstDigit is < '0' or > '9' || secondDigit is < '0' or > '9')
        {
            number = 0;
            return false;
        }

        number = ((firstDigit - '0') * 10) + (secondDigit - '0');
        return true;
    }
}
