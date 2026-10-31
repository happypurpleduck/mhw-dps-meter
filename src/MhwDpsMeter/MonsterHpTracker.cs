using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

/// <summary>Per-poll view of one tracked (large) monster.</summary>
internal readonly record struct MonsterState(
    nint Instance,
    MonsterType Type,
    string Name,
    int Variant,
    float MaxHealth,
    float Health);

/// <summary>
/// Sums HP lost by large monsters, including ones that already despawned. Used as
/// the solo/arena fallback when the quest-award damage table is not allocated.
/// </summary>
internal sealed class MonsterHpTracker
{
    private readonly Dictionary<nint, int> _live = [];
    private readonly Dictionary<nint, (MonsterType Type, int Variant, string Name)> _names = [];
    // Match recorder/gauge generation boundaries when the engine recycles a pointer.
    private readonly Dictionary<nint, MonsterState> _lastByInstance = [];
    // Align raw (max HP - current HP) to the last cumulative damage when
    // player-count difficulty scaling rebases both health values.
    private readonly Dictionary<nint, int> _hpScaleOffsets = [];
    private int _completed;

    /// <summary>Instances of tracked (large) monsters seen on the last poll.</summary>
    public IReadOnlyCollection<nint> LiveInstances => _live.Keys;

    /// <summary>Tracked monsters from the last poll with their current HP.</summary>
    public IReadOnlyList<MonsterState> LastTracked { get; private set; } = [];

    /// <summary>False when native enumeration tore; LastTracked may then be from a prior poll.</summary>
    public bool LastPollComplete { get; private set; }

    /// <summary>Last poll's monster list for diagnostics: every monster SPL reports, tracked or not.</summary>
    public string LastMonsters { get; private set; } = "";

    public void Reset()
    {
        _live.Clear();
        _names.Clear();
        _lastByInstance.Clear();
        _hpScaleOffsets.Clear();
        _completed = 0;
        LastTracked = [];
        LastPollComplete = false;
    }

    public int Poll()
    {
        LastPollComplete = false;
        HashSet<nint> seen = [];
        var described = new List<string>();
        var tracked = new List<MonsterState>();
        try
        {
            foreach (var monster in Monster.GetAllMonsters())
            {
                // A failed HP/type read does not mean this instance despawned.
                seen.Add(monster.Instance);
                described.Add(Describe(monster));
                if (!TryState(monster, out var state, out var confirmedUntracked))
                {
                    // A verified small/excluded or low-HP entity at this
                    // address is positive evidence the former large monster
                    // is gone. Only unreadable type/HP should remain "seen".
                    if (confirmedUntracked)
                        seen.Remove(monster.Instance);
                    continue;
                }

                seen.Add(state.Instance);
                var rawDealt = (int)Math.Clamp(state.MaxHealth - Math.Max(state.Health, 0f), 0f, state.MaxHealth);
                var dealt = rawDealt;
                if (_live.TryGetValue(state.Instance, out var prev))
                {
                    var hadPrevious = _lastByInstance.TryGetValue(state.Instance, out var previous);
                    var changedGeneration = hadPrevious && MonsterGeneration.Changed(previous, state);
                    if (hadPrevious && MonsterGeneration.Rescaled(previous, state))
                    {
                        // The monster did not respawn. Rebase the raw HP-loss
                        // counter onto the existing cumulative total, instead
                        // of treating the larger pool as newly inflicted damage.
                        // If a hit coincides with this scaling tick we cannot
                        // separate it from scaling; preserve the known total.
                        _hpScaleOffsets[state.Instance] = prev - rawDealt;
                        dealt = prev;
                    }
                    else
                    {
                        var adjustedDealt = rawDealt + _hpScaleOffsets.GetValueOrDefault(state.Instance);
                        // Legacy fallback for a genuine large HP reset that was
                        // missed by sparse reads. Never apply across rescaling.
                        var resetFromBankedDamage = adjustedDealt + 0.25f * state.MaxHealth < prev;
                        if (changedGeneration || resetFromBankedDamage)
                        {
                            _completed += prev;
                            _hpScaleOffsets.Remove(state.Instance);
                            if (resetFromBankedDamage && !changedGeneration)
                                _names.Remove(state.Instance);
                        }
                        else
                        {
                            dealt = Math.Max(prev, adjustedDealt);
                        }
                    }
                }
                _live[state.Instance] = dealt;
                _lastByInstance[state.Instance] = state;
                tracked.Add(state);
            }
        }
        catch
        {
            // An incomplete enumeration cannot establish which monsters despawned.
            return _completed + _live.Values.Sum();
        }

        foreach (var instance in _live.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _completed += _live[instance];
            _live.Remove(instance);
            _names.Remove(instance);
            _lastByInstance.Remove(instance);
            _hpScaleOffsets.Remove(instance);
        }

        LastTracked = tracked;
        LastPollComplete = true;
        LastMonsters = described.Count == 0 ? "(none)" : string.Join(" | ", described);
        return _completed + _live.Values.Sum();
    }

    private static string Describe(Monster monster)
    {
        try
        {
            return $"{monster.Type}@0x{monster.Instance:X} {monster.Health:0}/{monster.MaxHealth:0}";
        }
        catch
        {
            return "?";
        }
    }

    private bool TryState(Monster monster, out MonsterState state, out bool confirmedUntracked)
    {
        state = default;
        confirmedUntracked = false;
        try
        {
            var type = monster.Type;
            if (!IsTracked(type))
            {
                confirmedUntracked = true;
                return false;
            }

            var instance = monster.Instance;
            var max = monster.MaxHealth;
            // Only a strictly positive, readable small HP pool positively
            // identifies an untracked entity. Zero/negative values can be
            // transient native reads; do not retire the previous large monster.
            if (float.IsFinite(max) && max is > 0f and < 800f)
            {
                confirmedUntracked = true;
                return false;
            }
            var hp = monster.Health;
            if (instance == 0 || max <= 0f || max > 50_000_000f
                || !float.IsFinite(max) || !float.IsFinite(hp))
                return false;

            var variant = (int)monster.Variant;
            // Max HP can also change on a party-size transition. Refresh
            // the name to retain an independent replacement identity signal.
            if (_lastByInstance.TryGetValue(instance, out var last)
                && Math.Abs(last.MaxHealth - max) > 0.01f)
                _names.Remove(instance);
            state = new MonsterState(instance, type, NameOf(monster, instance, type, variant), variant, max, hp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Monster names are read once per instance; the string marshal is not free.</summary>
    private string NameOf(Monster monster, nint instance, MonsterType type, int variant)
    {
        if (_names.TryGetValue(instance, out var cached)
            && cached.Type == type && cached.Variant == variant)
            return cached.Name;

        string? name;
        try
        {
            name = monster.Name;
        }
        catch
        {
            name = null;
        }

        if (GameNames.IsPlaceholder(name))
            name = GameNames.Monster((int)type);
        _names[instance] = (type, variant, name);
        return name;
    }

    private static bool IsTracked(MonsterType type) => type is not (
        MonsterType.SmallBarrel or
        MonsterType.LargeBarrel or
        MonsterType.TrainingPole or
        MonsterType.TrainingWagon or
        MonsterType.Magmacore or
        MonsterType.Magmacore2 or
        MonsterType.Unavaliable);
}
