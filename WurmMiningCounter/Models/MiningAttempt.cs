namespace WurmMiningCounter.Models;

public sealed record MiningAttempt(
    MiningActionContext ActionContext,
    ActiveVeinContext? BoundVeinContext,
    MiningAttemptPhase Phase);
