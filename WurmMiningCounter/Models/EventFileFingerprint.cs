namespace WurmMiningCounter.Models;

public sealed record EventFileFingerprint(
    int WindowLength,
    string? Hash);
