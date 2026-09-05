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
    private readonly Dictionary<nint, string> _names = [];
    private int _completed;

    /// <summary>Instances of tracked (large) monsters seen on the last poll.</summary>
    public IReadOnlyCollection<nint> LiveInstances => _live.Keys;

    /// <summary>Tracked monsters from the last poll with their current HP.</summary>
    public IReadOnlyList<MonsterState> LastTracked { get; private set; } = [];

    /// <summary>Last poll's monster list for diagnostics: every monster SPL reports, tracked or not.</summary>
    public string LastMonsters { get; private set; } = "";

    public void Reset()
    {
        _live.Clear();
        _names.Clear();
        _completed = 0;
        LastTracked = [];
    }

    public int Poll()
    {
        HashSet<nint> seen = [];
        var described = new List<string>();
        var tracked = new List<MonsterState>();
        try
        {
            foreach (var monster in Monster.GetAllMonsters())
            {
                described.Add(Describe(monster));
                if (!TryState(monster, out var state))
                    continue;

                seen.Add(state.Instance);
                var dealt = (int)Math.Clamp(state.MaxHealth - Math.Max(state.Health, 0f), 0f, state.MaxHealth);
                if (_live.TryGetValue(state.Instance, out var prev))
                    dealt = Math.Max(prev, dealt);
                _live[state.Instance] = dealt;
                tracked.Add(state);
            }
        }
        catch
        {
            // Monster list can tear while entities despawn.
        }

        foreach (var instance in _live.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _completed += _live[instance];
            _live.Remove(instance);
        }

        LastTracked = tracked;
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

            state = new MonsterState(instance, type, NameOf(monster, instance, type), (int)monster.Variant, max, hp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Monster names are read once per instance; the string marshal is not free.</summary>
    private string NameOf(Monster monster, nint instance, MonsterType type)
    {
        if (_names.TryGetValue(instance, out var name))
            return name;

        try
        {
            name = monster.Name;
        }
        catch
        {
            name = null;
        }

        if (string.IsNullOrWhiteSpace(name))
            name = type.ToString();
        _names[instance] = name;
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
