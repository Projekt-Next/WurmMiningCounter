namespace WurmMiningCounter.Models;

public enum MiningAttemptTransitionKind
{
    WallBreak,
    WallBreakSoon,
    InformationalWarning,
    NoYieldTerminal,
    ContextSensitiveNoYield,
    PreStartRejection
}
