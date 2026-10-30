namespace MhwDpsMeter;

/// <summary>
/// Keeps expensive experimental native gauge reads on an independent cadence
/// without delaying the 100 ms party/DPS poll. The caller supplies only the
/// latest successfully enumerated monster list; stale/unsupported observations
/// are cleared rather than presented as current.
/// </summary>
internal sealed class MonsterGaugePoller
{
    public const double RefreshIntervalSeconds = 1.0;

    private double _nextRefreshAt;
    private readonly Dictionary<nint, MonsterState> _seen = [];
    public MonsterGaugeState[] Current { get; private set; } = [];

    // Changes when a sampled roster is replaced or invalidated. A transient
    // manual UI selection must not survive an entity generation or hunt change.
    public long SelectionEpoch { get; private set; }

    public void Reset()
    {
        if (_seen.Count > 0 || Current.Length > 0)
            SelectionEpoch++;
        Current = [];
        _seen.Clear();
        _nextRefreshAt = 0;
    }

    /// <param name="monotonicSeconds">Elapsed process-monotonic time, never the jump-prone quest timer.</param>
    /// <returns>A fresh sample only if a new protected read was performed,
    /// otherwise null. Current remains available between refreshes.</returns>
    public MonsterGaugeState[]? Tick(
        double monotonicSeconds, bool enabled, bool enumerationComplete,
        IReadOnlyList<MonsterState> monsters,
        Func<IReadOnlyList<MonsterState>, MonsterGaugeState[]> reader)
    {
        if (!enabled || !enumerationComplete || monsters.Count == 0)
        {
            Reset();
            return null;
        }

        // Keep snapshots generation-bound, not just pointer-bound: the engine
        // reuses entity addresses, sometimes without a frame where they vanish.
        // Detect a new type, variant, identity or large proportional HP
        // reset. A party-size max-HP rescale alone is not a new generation.
        var replaced = _seen.Count != monsters.Count
            || monsters.Any(m => !_seen.TryGetValue(m.Instance, out var previous)
                || MonsterGeneration.Changed(previous, m));
        if (replaced)
        {
            SelectionEpoch++;
            Current = [];
            _nextRefreshAt = 0; // immediate read for the replacement generation
        }
        _seen.Clear();
        foreach (var monster in monsters)
            _seen[monster.Instance] = monster;

        if (monotonicSeconds < _nextRefreshAt)
            return null;

        _nextRefreshAt = monotonicSeconds + RefreshIntervalSeconds;
        try
        {
            Current = reader(monsters);
            return Current;
        }
        catch
        {
            Current = [];
            return null;
        }
    }
}
