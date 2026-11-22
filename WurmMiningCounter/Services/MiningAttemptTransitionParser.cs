using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public static class MiningAttemptTransitionParser
{
    private static readonly IReadOnlyDictionary<string, MiningAttemptTransitionKind> TransitionsByMessage =
        new Dictionary<string, MiningAttemptTransitionKind>(StringComparer.Ordinal)
        {
            [EventLogMessages.WallBreak] = MiningAttemptTransitionKind.WallBreak,
            [EventLogMessages.WallBreakSoon] = MiningAttemptTransitionKind.WallBreakSoon,
            ["A deep shaft will probably emerge."] = MiningAttemptTransitionKind.InformationalWarning,
            ["You stop mining."] = MiningAttemptTransitionKind.NoYieldTerminal,
            ["You chip away at the rock."] = MiningAttemptTransitionKind.ContextSensitiveNoYield,
            ["You realize you can't open a tunnel here, because the surface is too flat."] =
                MiningAttemptTransitionKind.ContextSensitiveNoYield,
            ["You're too busy."] = MiningAttemptTransitionKind.PreStartRejection
        };

    public static MiningAttemptTransitionKind? Parse(string line)
    {
        var normalizedLine = EventLogLineNormalizer.Normalize(line);

        return TransitionsByMessage.TryGetValue(normalizedLine, out var transition)
            ? transition
            : null;
    }
}
