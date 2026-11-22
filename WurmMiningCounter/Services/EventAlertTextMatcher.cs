namespace WurmMiningCounter.Services;

/// <summary>Provides validation and matching rules for custom Event alert text.</summary>
internal static class EventAlertTextMatcher
{
    public const string InvalidCustomPatternMessage =
        "Trigger must contain one word with at least 3 characters or at least two words.";

    public static bool IsValidCustomPattern(string? pattern)
    {
        var tokens = Tokenize(pattern);
        return tokens.Length >= 2 || tokens.Length == 1 && tokens[0].Length >= 3;
    }

    public static bool IsCustomMatch(string? pattern, string? text)
    {
        var patternTokens = Tokenize(pattern);
        if (patternTokens.Length == 0
            || !IsValidCustomPattern(pattern))
        {
            return false;
        }

        var textTokens = Tokenize(text);
        for (var start = 0; start <= textTokens.Length - patternTokens.Length; start++)
        {
            var allTokensMatch = true;
            for (var offset = 0; offset < patternTokens.Length; offset++)
            {
                if (!string.Equals(patternTokens[offset], textTokens[start + offset], StringComparison.OrdinalIgnoreCase))
                {
                    allTokensMatch = false;
                    break;
                }
            }

            if (allTokensMatch)
                return true;
        }

        return false;
    }

    private static string[] Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<string>();
        var tokenStart = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsLetterOrDigit(text[index]))
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            AddToken(text, tokenStart, index, tokens);
            tokenStart = -1;
        }

        AddToken(text, tokenStart, text.Length, tokens);
        return [.. tokens];
    }

    private static void AddToken(string text, int start, int end, List<string> tokens)
    {
        if (start >= 0)
            tokens.Add(text[start..end]);
    }
}
