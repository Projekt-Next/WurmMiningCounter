using System.Collections.Immutable;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>Thread-safe owner of immutable Event alert trigger snapshots.</summary>
public sealed class EventAlertCatalog
{
    private readonly object _sync = new();
    private ImmutableArray<EventAlertTrigger> _triggers = EventAlertBuiltIns.Create();

    public EventAlertCatalog() { }

    /// <summary>Creates a catalog from persisted settings while retaining canonical built-ins.</summary>
    public EventAlertCatalog(IEnumerable<EventAlertTrigger> persistedTriggers)
    {
        ArgumentNullException.ThrowIfNull(persistedTriggers);

        var settings = new AppSettings { EventAlerts = [.. persistedTriggers] };
        _triggers = [.. EventAlertSettingsNormalizer.Normalize(settings)];
    }

    public ImmutableArray<EventAlertTrigger> GetSnapshot()
    {
        lock (_sync)
            return _triggers;
    }

    public void AddCustom(EventAlertTrigger trigger)
    {
        ValidateCustom(trigger);
        lock (_sync)
        {
            if (IndexOf(trigger.Id) >= 0)
                throw new ArgumentException("An alert trigger with this Id already exists.", nameof(trigger));

            _triggers = _triggers.Add(trigger);
        }
    }

    public void EditCustom(EventAlertTrigger trigger)
    {
        ValidateCustom(trigger);
        lock (_sync)
        {
            var index = IndexOf(trigger.Id);
            if (index < 0)
                throw new KeyNotFoundException("The alert trigger does not exist.");
            if (_triggers[index].IsBuiltIn)
                throw new InvalidOperationException("Built-in alert triggers cannot be edited.");

            _triggers = _triggers.SetItem(index, trigger);
        }
    }

    public void SetEnabled(Guid id, bool enabled)
        => SetOptions(id, enabled: enabled, soundEnabled: null, popupEnabled: null);

    public void SetSoundEnabled(Guid id, bool enabled)
        => SetOptions(id, enabled: null, soundEnabled: enabled, popupEnabled: null);

    public void SetPopupEnabled(Guid id, bool enabled)
        => SetOptions(id, enabled: null, soundEnabled: null, popupEnabled: enabled);

    private void SetOptions(Guid id, bool? enabled, bool? soundEnabled, bool? popupEnabled)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("The alert trigger Id must not be empty.", nameof(id));

        lock (_sync)
        {
            var index = IndexOf(id);
            if (index < 0)
                throw new KeyNotFoundException("The alert trigger does not exist.");

            var trigger = _triggers[index];
            _triggers = _triggers.SetItem(index, trigger with
            {
                Enabled = enabled ?? trigger.Enabled,
                SoundEnabled = soundEnabled ?? trigger.SoundEnabled,
                PopupEnabled = popupEnabled ?? trigger.PopupEnabled
            });
        }
    }

    public void DeleteCustom(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("The alert trigger Id must not be empty.", nameof(id));

        lock (_sync)
        {
            var index = IndexOf(id);
            if (index < 0)
                throw new KeyNotFoundException("The alert trigger does not exist.");
            if (_triggers[index].IsBuiltIn)
                throw new InvalidOperationException("Built-in alert triggers cannot be deleted.");

            _triggers = _triggers.RemoveAt(index);
        }
    }

    internal void ReplacePersisted(IEnumerable<EventAlertTrigger> persistedTriggers)
    {
        ArgumentNullException.ThrowIfNull(persistedTriggers);

        var settings = new AppSettings { EventAlerts = [.. persistedTriggers] };
        var normalized = EventAlertSettingsNormalizer.Normalize(settings);
        lock (_sync)
            _triggers = [.. normalized];
    }

    private int IndexOf(Guid id)
    {
        for (var index = 0; index < _triggers.Length; index++)
        {
            if (_triggers[index].Id == id)
                return index;
        }

        return -1;
    }

    private static void ValidateCustom(EventAlertTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (trigger.Id == Guid.Empty)
            throw new ArgumentException("The alert trigger Id must not be empty.", nameof(trigger));
        if (trigger.IsBuiltIn)
            throw new ArgumentException("Custom alert triggers cannot be built-in.", nameof(trigger));
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger.MatchText);
        if (!EventAlertTextMatcher.IsValidCustomPattern(trigger.MatchText))
            throw new ArgumentException(EventAlertTextMatcher.InvalidCustomPatternMessage, nameof(trigger));
        if (trigger.SoundFileName is not null && !EventAlertSoundCatalog.IsSafeWavFileName(trigger.SoundFileName))
            throw new ArgumentException("Alert trigger sound file name is invalid.", nameof(trigger));
    }
}
