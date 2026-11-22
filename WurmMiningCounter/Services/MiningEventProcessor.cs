using WurmMiningCounter.Models;

namespace WurmMiningCounter.Services;

public sealed class MiningEventProcessor
{
    private readonly MiningEventParser _parser;
    private readonly MiningSessionCounter _counter;
    private readonly MiningInspirationTracker? _inspirationTracker;

    public MiningEventProcessor(
        MiningEventParser parser,
        MiningSessionCounter counter,
        MiningInspirationTracker? inspirationTracker = null)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _inspirationTracker = inspirationTracker;
    }

    public bool Process(EventLogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var miningEvent = _parser.Parse(line.Text);
        _inspirationTracker?.Process(line, miningEvent);
        if (miningEvent is null)
            return false;

        _counter.Add(line.ProfileId, line.ProfileName, miningEvent);
        return true;
    }

    /// <summary>Resets the complete Mining session state for one profile.</summary>
    internal void ResetProfile(PlayerProfileId profileId)
    {
        _counter.ResetProfile(profileId);
        _inspirationTracker?.ResetProfile(profileId);
    }

    /// <summary>Resets only the Mining Inspiration state for one profile.</summary>
    internal void ResetMiningInspirationProfile(PlayerProfileId profileId)
    {
        _inspirationTracker?.ResetProfileInspiration(profileId);
    }

}
