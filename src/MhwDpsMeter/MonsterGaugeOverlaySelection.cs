namespace MhwDpsMeter;

/// <summary>Transient, source-only selection from *current cached* samples.
/// Never an active target/attacker identity, and never persisted as an entity
/// address. The poller epoch invalidates even same-pointer monster reuse.</summary>
internal sealed class MonsterGaugeOverlaySelection
{
    // Cap F9 controls independently of the source reader's work bounds.
    public const int MaxMenuChoices = 8;

    private long _epoch = long.MinValue;
    private nint? _selectedInstance;
    private string? _selectedName;

    public bool IsManual => _selectedInstance.HasValue;

    public bool Matches(MonsterGaugeState sample) =>
        _selectedInstance == sample.Instance
        && string.Equals(_selectedName, sample.Name, StringComparison.Ordinal);

    public void Clear()
    {
        _selectedInstance = null;
        _selectedName = null;
    }

    private void ObserveEpoch(long epoch)
    {
        if (_epoch == epoch)
            return;
        _epoch = epoch;
        Clear();
    }

    /// <summary>Apply the same display gate used by both F9 and the HUD.
    /// Age-only staleness hides rows but must not act like an empty roster:
    /// the next fresh sample may still contain the same selected monster.
    /// A changed generation clears the choice even while readings are stale.
    /// All other unavailable states invalidate the manual choice.</summary>
    public void ObserveAvailability(long epoch, IReadOnlyList<MonsterGaugeState>? gauges,
        MonsterGaugeOverlayAvailability availability)
    {
        if (availability.ShowStaleHudHint)
        {
            ObserveEpoch(epoch);
            return;
        }

        Observe(epoch, availability.MayRenderGauges ? gauges : null);
    }

    public void Observe(long epoch, IReadOnlyList<MonsterGaugeState>? gauges)
    {
        ObserveEpoch(epoch);

        // An unreadable/empty roster must never retain a stale manual target.
        if (_selectedInstance is nint instance
            && (gauges is null || !gauges.Any(g =>
                g.Instance == instance && g.Name == _selectedName)))
            Clear();
    }

    public bool Select(MonsterGaugeState sample, long epoch,
        IReadOnlyList<MonsterGaugeState> current)
    {
        Observe(epoch, current);
        if (!current.Any(g => g.Instance == sample.Instance && g.Name == sample.Name))
            return false;
        _selectedInstance = sample.Instance;
        _selectedName = sample.Name;
        return true;
    }

    public IReadOnlyList<MonsterGaugeState> Visible(
        IReadOnlyList<MonsterGaugeState> current)
    {
        if (_selectedInstance is nint instance)
        {
            foreach (var sample in current)
                if (sample.Instance == instance && sample.Name == _selectedName)
                    return new[] { sample };
        }

        return current;
    }
}
