namespace WurmMiningCounter.Models;

public enum StreamGapReason
{
    Truncated,
    Replaced,
    RotationOrderLost,
    UnrecoverableReadGap
}
