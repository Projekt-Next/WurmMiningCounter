namespace WurmMiningCounter.MiningStatistics;

/// <summary>
/// Thread-safe, in-memory state for the skills of one profile.
/// Source discovery, lifecycle and profile ownership remain outside this type.
/// </summary>
public sealed class SkillStateTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SkillState> _skills = new(StringComparer.Ordinal);

    /// <summary>
    /// Seeds a current value from pre-session information without creating session gain.
    /// </summary>
    public void SeedCurrent(string skillName, decimal currentValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        lock (_sync)
            _skills[skillName] = new SkillState(currentValue, null, 0m, 0);
    }

    /// <summary>
    /// Applies one live update. Only positive updates contribute to session gain.
    /// </summary>
    public void Apply(NumericSkillUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_sync)
        {
            if (!_skills.TryGetValue(update.SkillName, out var state))
                state = new SkillState(update.CurrentValue, null, 0m, 0);

            state.CurrentValue = update.CurrentValue;
            if (update.Direction == NumericSkillUpdateDirection.Increased)
            {
                state.LastGain = update.Delta;
                state.SessionGain += update.Delta;
                state.PositiveUpdateCount++;
            }

            _skills[update.SkillName] = state;
        }
    }

    /// <summary>Returns an immutable, ordinally ordered snapshot of all known skills.</summary>
    public IReadOnlyList<SkillStateSnapshot> GetSnapshot()
    {
        lock (_sync)
        {
            return _skills
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SkillStateSnapshot(
                    pair.Key,
                    pair.Value.CurrentValue,
                    pair.Value.LastGain,
                    pair.Value.SessionGain,
                    pair.Value.PositiveUpdateCount))
                .ToArray();
        }
    }

    private sealed class SkillState(decimal currentValue, decimal? lastGain, decimal sessionGain, long positiveUpdateCount)
    {
        public decimal CurrentValue { get; set; } = currentValue;
        public decimal? LastGain { get; set; } = lastGain;
        public decimal SessionGain { get; set; } = sessionGain;
        public long PositiveUpdateCount { get; set; } = positiveUpdateCount;
    }
}
