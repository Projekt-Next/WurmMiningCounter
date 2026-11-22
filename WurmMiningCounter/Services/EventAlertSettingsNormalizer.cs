using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

internal static class EventAlertSettingsNormalizer
{
    public static List<EventAlertTrigger> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = NormalizePersisted(settings.EventAlerts);
        settings.EventAlerts = normalized;
        return normalized;
    }

    private static List<EventAlertTrigger> NormalizePersisted(IReadOnlyList<EventAlertTrigger> persisted)
    {
        var duplicateIds = persisted
            .Where(trigger => trigger is not null)
            .GroupBy(trigger => trigger.Id)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var retainedBuiltInOptions = persisted
            .Where(trigger => trigger is not null
                && !duplicateIds.Contains(trigger.Id)
                && trigger.IsBuiltIn
                && IsBuiltInId(trigger.Id))
            .ToDictionary(trigger => trigger.Id, trigger => trigger);
        var normalized = EventAlertBuiltIns.Create()
            .Select(trigger => retainedBuiltInOptions.TryGetValue(trigger.Id, out var persistedTrigger)
                ? trigger with
                {
                    Enabled = persistedTrigger.Enabled,
                    SoundFileName = persistedTrigger.SoundFileName is null
                        || EventAlertSoundCatalog.IsSafeWavFileName(persistedTrigger.SoundFileName)
                        ? persistedTrigger.SoundFileName
                        : trigger.SoundFileName,
                    SoundEnabled = persistedTrigger.SoundEnabled,
                    PopupEnabled = persistedTrigger.PopupEnabled
                }
                : trigger)
            .ToList();
        var custom = persisted
            .Where(trigger => IsValidCustom(trigger, duplicateIds))
            .Select(trigger => EventAlertSoundCatalog.IsSafeWavFileName(trigger.SoundFileName)
                ? trigger
                : trigger with { SoundFileName = null })
            .OrderBy(trigger => trigger.Id)
            .ToList();
        normalized.AddRange(custom);
        return normalized;
    }

    private static bool IsValidCustom(EventAlertTrigger? trigger, ISet<Guid> duplicateIds) =>
        trigger is not null
        && !trigger.IsBuiltIn
        && trigger.Id != Guid.Empty
        && !IsBuiltInId(trigger.Id)
        && !duplicateIds.Contains(trigger.Id)
        && !string.IsNullOrWhiteSpace(trigger.Name)
        && !string.IsNullOrWhiteSpace(trigger.MatchText);

    private static bool IsBuiltInId(Guid id) =>
        id == EventAlertBuiltIns.WallBreakSoonId || id == EventAlertBuiltIns.WallBreakId;
}
