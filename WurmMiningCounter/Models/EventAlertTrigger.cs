namespace WurmMiningCounter.Models;

/// <summary>Immutable configuration for one exact Event log alert.</summary>
public sealed record EventAlertTrigger(
    Guid Id,
    string Name,
    string MatchText,
    bool Enabled,
    string? SoundFileName,
    string PopupText,
    bool IsBuiltIn,
    bool SoundEnabled = true,
    bool PopupEnabled = true);
