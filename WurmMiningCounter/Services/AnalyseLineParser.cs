using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public static class AnalyseLineParser
{
    private const string AnalyseShardStart = "You start to analyse the shard.";
    private const string AnalyseOreStart = "You start to analyse the ore.";
    private const string AnalyseShardFinish = "You finish analysing the shard.";
    private const string AnalyseOreFinish = "You finish analysing the ore.";

    public static bool IsAnalyseStart(string text)
        => TryParseStart(text, out _);

    public static bool IsAnalyseFinish(string text)
        => TryParseFinish(text, out _);

    public static bool TryParseStart(string text, out AnalyseInput input) =>
        TryParseBoundary(text, AnalyseShardStart, AnalyseOreStart, out input);

    public static bool TryParseFinish(string text, out AnalyseInput input) =>
        TryParseBoundary(text, AnalyseShardFinish, AnalyseOreFinish, out input);

    private static bool TryParseBoundary(string text, string shard, string ore, out AnalyseInput input)
    {
        var normalizedText = EventLogLineNormalizer.Normalize(text);
        input = normalizedText == ore ? AnalyseInput.Ore : AnalyseInput.Shard;
        return normalizedText == shard || normalizedText == ore;
    }

    // A marker follows the named strength; callers retain input order themselves.
    public static bool TryParseStageMarker(string text, out AnalyseStrength strength)
    {
        strength = EventLogLineNormalizer.Normalize(text) switch
        {
            "You take a closer look." => AnalyseStrength.Trace,
            "You study it a bit more." => AnalyseStrength.Slight,
            "You study it real hard." => AnalyseStrength.Faint,
            "You peer at it." => AnalyseStrength.Minuscule,
            "You go cross-eyed studying it." => AnalyseStrength.Vague,
            _ => (AnalyseStrength)(-1)
        };
        return (int)strength >= 0;
    }

    public static bool TryParseNoTrace(string text, out AnalyseNoTrace observation)
    {
        var normalized = EventLogLineNormalizer.Normalize(text);
        AnalyseStrength? strength = normalized switch
        {
            "You do not notice any unusual traces." or "You cannot see any unusual traces of anything." => AnalyseStrength.Trace,
            "You do not notice any unusual slight traces." or "You cannot see any unusual slight traces of anything." => AnalyseStrength.Slight,
            "You do not notice any unusual faint traces." or "You cannot see any unusual faint traces of anything." => AnalyseStrength.Faint,
            "You do not notice any unusual minuscule traces." => AnalyseStrength.Minuscule,
            "You do not notice any unusual vague traces." or "You cannot see any unusual vague traces of anything." => AnalyseStrength.Vague,
            "You cannot see any unusual indistinct traces of anything." => AnalyseStrength.Indistinct,
            _ => null
        };
        if (strength is null && normalized != "You cannot see anything unusual.")
        {
            observation = null!;
            return false;
        }
        observation = new AnalyseNoTrace(strength, text);
        return true;
    }

    public static bool TryParseTrace(string text, out AnalyseTrace trace)
    {
        trace = null!;
        var line = EventLogLineNormalizer.Normalize(text);
        var prefix = line.StartsWith("You notice ", StringComparison.Ordinal) ? "You notice "
            : line.StartsWith("You see ", StringComparison.Ordinal) ? "You see "
            : line.StartsWith("You spot ", StringComparison.Ordinal) ? "You spot " : null;
        if (prefix is null || !line.EndsWith(".", StringComparison.Ordinal))
            return false;

        var directionStart = line.LastIndexOf(" (", StringComparison.Ordinal);
        string? direction = null;
        var bodyEnd = line.Length - 1;
        if (directionStart >= 0)
        {
            if (directionStart < prefix.Length || !line.EndsWith(").", StringComparison.Ordinal))
                return false;
            direction = line[(directionStart + 2)..^2];
            if (direction is not ("north" or "south" or "east" or "west" or "northeast" or "northwest" or "southeast" or "southwest"
                or "east of north" or "west of north" or "north of east" or "south of east" or "east of south" or "west of south" or "north of west" or "south of west"))
                return false;
            bodyEnd = directionStart;
        }

        var body = line[prefix.Length..bodyEnd];
        var hasAn = body.StartsWith("an ", StringComparison.Ordinal);
        if (!hasAn && !body.StartsWith("a ", StringComparison.Ordinal))
            return false;
        body = body[(hasAn ? 3 : 2)..];
        var separator = body.IndexOf(" of ", StringComparison.Ordinal);
        if (separator < 0 || !TryParseStrength(body[..separator], out var strength))
            return false;
        body = body[(separator + 4)..];
        if (body == "something, but cannot quite make it out")
        {
            if (!hasAn && strength != AnalyseStrength.Vague)
                return false;
            trace = new AnalyseTrace(strength, null, null, direction, text);
            return true;
        }

        if (hasAn != (strength == AnalyseStrength.Indistinct))
            return false;

        string? quality = null;
        var qualityEnd = body.IndexOf(" quality ", StringComparison.Ordinal);
        if (qualityEnd >= 0)
        {
            quality = body[..qualityEnd];
            if (quality is not ("poor" or "acceptable" or "normal" or "good" or "very good" or "utmost"))
                return false;
            body = body[(qualityEnd + " quality ".Length)..];
        }
        if (body is not ("iron" or "copper" or "tin" or "zinc" or "lead" or "silver" or "gold" or "sandstone" or "marble" or "slate" or "rock salt" or "salt" or "flint"))
            return false;
        if (quality is null && body is not ("salt" or "flint"))
            return false;
        if (quality is not null && body is ("salt" or "flint"))
            return false;
        trace = new AnalyseTrace(strength, body, quality, direction, text);
        return true;
    }

    private static bool TryParseStrength(string text, out AnalyseStrength strength)
    {
        strength = text switch
        {
            "trace" => AnalyseStrength.Trace,
            "slight trace" => AnalyseStrength.Slight,
            "faint trace" => AnalyseStrength.Faint,
            "minuscule trace" => AnalyseStrength.Minuscule,
            "vague trace" => AnalyseStrength.Vague,
            "indistinct trace" => AnalyseStrength.Indistinct,
            _ => (AnalyseStrength)(-1)
        };
        return (int)strength >= 0;
    }
}
