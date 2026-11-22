using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>Applies validated alert configuration only after the settings save succeeds.</summary>
public sealed class EventAlertConfigurationController
{
    private readonly AppSettings _settings;
    private readonly EventAlertCatalog _catalog;
    private readonly Func<AppSettings, bool> _save;

    public EventAlertConfigurationController(
        AppSettings settings,
        EventAlertCatalog catalog,
        Func<AppSettings, bool> save)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    public IReadOnlyList<EventAlertTrigger> GetSnapshot() => _catalog.GetSnapshot();

    public bool TryAddCustom(EventAlertTrigger trigger, out string? error)
    {
        if (!TryValidateEditedCustom(trigger, out error))
            return false;

        return TryApply(AddCustom(_catalog.GetSnapshot(), trigger), out error);
    }

    public bool TryEdit(EventAlertTrigger trigger, out string? error)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var current = _catalog.GetSnapshot();
        var existing = current.FirstOrDefault(item => item.Id == trigger.Id);
        if (existing is null)
        {
            error = "The alert trigger no longer exists.";
            return false;
        }

        var replacement = existing.IsBuiltIn
            ? existing with
            {
                SoundFileName = trigger.SoundFileName,
                Enabled = trigger.Enabled,
                SoundEnabled = trigger.SoundEnabled,
                PopupEnabled = trigger.PopupEnabled
            }
            : trigger;
        if (!replacement.IsBuiltIn && !TryValidateEditedCustom(replacement, out error))
            return false;

        return TryApply(current.Select(item => item.Id == replacement.Id ? replacement : item), out error);
    }

    public bool TrySetEnabled(Guid id, bool enabled, out string? error)
        => TrySetOptions(id, enabled, soundEnabled: null, popupEnabled: null, out error);

    public bool TrySetSoundEnabled(Guid id, bool enabled, out string? error)
        => TrySetOptions(id, enabled: null, soundEnabled: enabled, popupEnabled: null, out error);

    public bool TrySetPopupEnabled(Guid id, bool enabled, out string? error)
        => TrySetOptions(id, enabled: null, soundEnabled: null, popupEnabled: enabled, out error);

    private bool TrySetOptions(Guid id, bool? enabled, bool? soundEnabled, bool? popupEnabled, out string? error)
    {
        var current = _catalog.GetSnapshot();
        if (!current.Any(item => item.Id == id))
        {
            error = "The alert trigger no longer exists.";
            return false;
        }

        return TryApply(current.Select(item => item.Id == id ? item with
        {
            Enabled = enabled ?? item.Enabled,
            SoundEnabled = soundEnabled ?? item.SoundEnabled,
            PopupEnabled = popupEnabled ?? item.PopupEnabled
        } : item), out error);
    }

    public bool TryDeleteCustom(Guid id, out string? error)
    {
        var current = _catalog.GetSnapshot();
        var existing = current.FirstOrDefault(item => item.Id == id);
        if (existing is null || existing.IsBuiltIn)
        {
            error = "Built-in alert triggers cannot be deleted.";
            return false;
        }

        return TryApply(current.Where(item => item.Id != id), out error);
    }

    private bool TryApply(IEnumerable<EventAlertTrigger> triggers, out string? error)
    {
        var triggerList = triggers.ToList();
        try { Validate(triggerList); }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        var candidate = CopySettings(triggerList);
        EventAlertSettingsNormalizer.Normalize(candidate);
        if (!_save(candidate))
        {
            error = "Settings save failed.";
            return false;
        }

        _catalog.ReplacePersisted(candidate.EventAlerts);
        _settings.EventAlerts = [.. candidate.EventAlerts];
        error = null;
        return true;
    }

    private static IEnumerable<EventAlertTrigger> AddCustom(
        IEnumerable<EventAlertTrigger> current, EventAlertTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return current.Append(trigger);
    }

    private AppSettings CopySettings(IReadOnlyList<EventAlertTrigger> eventAlerts) => new()
    {
        AlwaysOnTop = _settings.AlwaysOnTop,
        StartMinimizedToTray = _settings.StartMinimizedToTray,
        DiagnosticsEnabled = _settings.DiagnosticsEnabled,
        EventAlerts = [.. eventAlerts],
        AdditionalWurmDataLocations = [.. _settings.AdditionalWurmDataLocations],
        KnownLogLocations = KnownLogLocations.CopyAll(_settings.KnownLogLocations),
        WindowPlacement = _settings.WindowPlacement
    };

    private static void Validate(IReadOnlyList<EventAlertTrigger> triggers)
    {
        if (triggers.Select(trigger => trigger.Id).Distinct().Count() != triggers.Count)
            throw new ArgumentException("Alert trigger Ids must be unique.");

        foreach (var trigger in triggers)
        {
            ArgumentNullException.ThrowIfNull(trigger);
            if (trigger.IsBuiltIn)
                continue;
            if (trigger.Id == Guid.Empty || EventAlertBuiltIns.Create().Any(builtIn => builtIn.Id == trigger.Id))
                throw new ArgumentException("Custom alert trigger Id is invalid.");
            ArgumentException.ThrowIfNullOrWhiteSpace(trigger.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(trigger.MatchText);
            if (trigger.SoundFileName is not null && !EventAlertSoundCatalog.IsSafeWavFileName(trigger.SoundFileName))
                throw new ArgumentException("Alert trigger sound file name is invalid.");
        }
    }

    private static bool TryValidateEditedCustom(EventAlertTrigger? trigger, out string? error)
    {
        if (trigger is not null && EventAlertTextMatcher.IsValidCustomPattern(trigger.MatchText))
        {
            error = null;
            return true;
        }

        error = EventAlertTextMatcher.InvalidCustomPatternMessage;
        return false;
    }
}
