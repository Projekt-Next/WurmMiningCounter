using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

/// <summary>
/// Stateless ordered-line observer that schedules no work itself and never
/// allows alert failures to fault the Context lane.
/// </summary>
public sealed class EventAlertModule : IOrderedEventConsumer
{
    private readonly EventAlertCatalog _catalog;
    private readonly Action<EventAlertNotification> _sink;

    public EventAlertModule(EventAlertCatalog catalog, Action<EventAlertNotification> sink)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public void Process(OrderedEventItem item)
    {
        try
        {
            if (item is not OrderedEventLine line)
                return;

            var normalizedText = EventLogLineNormalizer.Normalize(line.Line.Text);
            foreach (var trigger in _catalog.GetSnapshot())
            {
                if (!trigger.Enabled
                    || !IsMatch(trigger, normalizedText))
                {
                    continue;
                }

                try { _sink(new EventAlertNotification(line.ProfileName, trigger)); }
                catch { /* Alert delivery must not affect Context processing. */ }
            }
        }
        catch
        {
            // Alert matching must not affect Context processing.
        }
    }

    public void Invalidate(PlayerProfileId profileId, string profileName)
    {
        // This observer owns no per-profile state.
    }

    private static bool IsMatch(EventAlertTrigger trigger, string normalizedText) =>
        trigger.IsBuiltIn
            ? string.Equals(trigger.MatchText, normalizedText, StringComparison.Ordinal)
            : EventAlertTextMatcher.IsCustomMatch(trigger.MatchText, normalizedText);
}
