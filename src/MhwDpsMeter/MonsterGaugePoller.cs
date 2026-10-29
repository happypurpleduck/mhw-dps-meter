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
    public MonsterGaugeState[] Current { get; private set; } = [];

    public void Reset()
    {
        Current = [];
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

        // A despawn/failed monster list must not leave that monster on screen
        // for the rest of the interval. New monsters are observed next tick.
        if (Current.Any(g => !monsters.Any(m => m.Instance == g.Instance)))
            Reset();

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
