using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public static class EventAlertBuiltIns
{
    public static readonly Guid WallBreakSoonId = new("5C61B657-516B-433A-9C7F-040B6D6C9197");
    public static readonly Guid WallBreakId = new("CB06F5AB-05EF-4CF6-8590-6E8675988948");

    public static ImmutableArray<EventAlertTrigger> Create() =>
    [
        new EventAlertTrigger(
            WallBreakSoonId,
            "Wall Break Soon",
            EventLogMessages.WallBreakSoon,
            Enabled: true,
            null,
            "Wall Break Soon",
            IsBuiltIn: true,
            SoundEnabled: true,
            PopupEnabled: true),
        new EventAlertTrigger(
            WallBreakId,
            "Wall Break",
            EventLogMessages.WallBreak,
            Enabled: true,
            null,
            "Wall Break",
            IsBuiltIn: true,
            SoundEnabled: true,
            PopupEnabled: true)
    ];
}
