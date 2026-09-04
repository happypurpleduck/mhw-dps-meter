using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

internal sealed class MonsterHpTracker
{
    private readonly Dictionary<nint, int> _live = [];
    private int _completed;

    /// <summary>Instances of tracked (large) monsters seen on the last poll.</summary>
    public IReadOnlyCollection<nint> LiveInstances => _live.Keys;

    /// <summary>Last poll's monster list for diagnostics: every monster SPL reports, tracked or not.</summary>
    public string LastMonsters { get; private set; } = "";

    public void Reset()
    {
        _live.Clear();
        _completed = 0;
    }

    public int Poll()
    {
        HashSet<nint> seen = [];
        var described = new List<string>();
        try
        {
            foreach (var monster in Monster.GetAllMonsters())
            {
                described.Add(Describe(monster));
                if (!TryDealt(monster, out var instance, out var dealt))
                    continue;

                seen.Add(instance);
                if (_live.TryGetValue(instance, out var prev))
                    dealt = Math.Max(prev, dealt);
                _live[instance] = dealt;
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

    private static bool TryDealt(Monster monster, out nint instance, out int dealt)
    {
        instance = 0;
        dealt = 0;
        try
        {
            if (!IsTracked(monster.Type))
                return false;

            instance = monster.Instance;
            var max = monster.MaxHealth;
            var hp = monster.Health;
            if (instance == 0 || max is < 800f or > 50_000_000f || float.IsNaN(max) || float.IsNaN(hp))
                return false;

            dealt = (int)Math.Clamp(max - Math.Max(hp, 0f), 0f, max);
            return true;
        }
        catch
        {
            return false;
        }
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
