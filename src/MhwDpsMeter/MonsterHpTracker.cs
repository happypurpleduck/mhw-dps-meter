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
                if (!TryState(monster, out var state))
                    continue;

                seen.Add(state.Instance);
                var dealt = (int)Math.Clamp(state.MaxHealth - Math.Max(state.Health, 0f), 0f, state.MaxHealth);
                if (_live.TryGetValue(state.Instance, out var prev))
                {
                    // Recorder/gauge cache treat type, variant, name, max HP and
                    // significant HP restoration as a new generation even when
                    // the pointer does not disappear. Bank its earlier damage
                    // *regardless* of how little the first monster had taken.
                    var changedGeneration = _lastByInstance.TryGetValue(state.Instance, out var previous)
                        && MonsterGeneration.Changed(previous, state);
                    // Also preserve the original max-dealt recovery fallback for
                    // sparse/failed HP reads, where the most recent snapshot may
                    // have missed the old monster's lowest HP.
                    var resetFromBankedDamage = dealt + 0.25f * state.MaxHealth < prev;
                    if (changedGeneration || resetFromBankedDamage)
                    {
                        _completed += prev;
                        if (resetFromBankedDamage && !changedGeneration)
                            _names.Remove(state.Instance);
                    }
                    else
                    {
                        dealt = Math.Max(prev, dealt);
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

    private bool TryState(Monster monster, out MonsterState state)
    {
        state = default;
        try
        {
            var type = monster.Type;
            if (!IsTracked(type))
                return false;

            var instance = monster.Instance;
            var max = monster.MaxHealth;
            var hp = monster.Health;
            if (instance == 0 || max is < 800f or > 50_000_000f || float.IsNaN(max) || float.IsNaN(hp))
                return false;

            var variant = (int)monster.Variant;
            // A new max-HP template at this same pointer can also mean a
            // different monster of the same type/variant. Re-read its name.
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
