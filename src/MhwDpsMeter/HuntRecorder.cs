using System.Diagnostics;
using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

/// <summary>
/// Collects everything a fight log needs beyond the party totals: the monsters seen,
/// the local hunter's individual hits, and timeline events (enrage, death, weapon
/// swaps, hunters joining). Fed from the poll loop and from SPL callbacks, which
/// may run on different threads, hence the lock.
/// </summary>
internal sealed class HuntRecorder
{
    public const int MaxHits = 50_000;
    public const int MaxEvents = 5_000;

    private readonly object _gate = new();
    private readonly List<FightLogHit> _hits = [];
    private readonly List<FightLogEvent> _events = [];
    private readonly Dictionary<nint, FightLogMonster> _monsters = [];
    private readonly Dictionary<(int Set, int Id), string?> _actionNames = [];
    private readonly Dictionary<int, string> _roster = [];
    private readonly Func<int, int, string?> _resolveActionName;
    private string? _localWeapon;

    public HuntRecorder(Func<int, int, string?> resolveActionName)
    {
        _resolveActionName = resolveActionName;
    }

    public string? LocalWeapon
    {
        get { lock (_gate) return _localWeapon; }
    }

    public int HitCount
    {
        get { lock (_gate) return _hits.Count; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _hits.Clear();
            _events.Clear();
            _monsters.Clear();
            _actionNames.Clear();
            _roster.Clear();
            _localWeapon = null;
        }
    }

    public void ObserveMonsters(float elapsed, IReadOnlyList<MonsterState> monsters)
    {
        lock (_gate)
        {
            foreach (var state in monsters)
            {
                if (!_monsters.TryGetValue(state.Instance, out var entry))
                {
                    entry = new FightLogMonster
                    {
                        Id = $"m{_monsters.Count + 1}",
                        Type = (int)state.Type,
                        Name = state.Name,
                        Variant = state.Variant,
                        MaxHealth = state.MaxHealth,
                        FirstSeenT = elapsed
                    };
                    _monsters[state.Instance] = entry;
                }

                entry.LastHealth = state.Health;
                if (entry.MaxHealth <= 0 && state.MaxHealth > 0)
                    entry.MaxHealth = state.MaxHealth;
                if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(state.Name))
                    entry.Name = state.Name;
            }
        }
    }

    /// <summary>Records join/leave events when the roster changes between polls.</summary>
    public void ObserveParty(float elapsed, IReadOnlyList<PartyMemberSnapshot> members)
    {
        lock (_gate)
        {
            var current = members
                .Where(member => member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                .ToDictionary(member => member.Slot, member => member.Name);

            foreach (var (slot, name) in current)
            {
                if (_roster.TryGetValue(slot, out var known) && known == name)
                    continue;
                if (known is not null)
                    AddEventLocked(elapsed, "leave", slot: slot, detail: known);
                AddEventLocked(elapsed, "join", slot: slot, detail: name);
                _roster[slot] = name;
            }

            foreach (var slot in _roster.Keys.Where(slot => !current.ContainsKey(slot)).ToArray())
            {
                AddEventLocked(elapsed, "leave", slot: slot, detail: _roster[slot]);
                _roster.Remove(slot);
            }
        }
    }

    public void ObserveWeapon(float elapsed, WeaponType? weapon, int localSlot)
    {
        if (weapon is null or WeaponType.None)
            return;

        var name = weapon.Value.ToString();
        lock (_gate)
        {
            if (_localWeapon == name)
                return;
            _localWeapon = name;
            AddEventLocked(elapsed, "weapon", slot: localSlot < 0 ? null : localSlot, detail: name);
        }
    }

    /// <summary>Hunt hits: <paramref name="elapsed"/> is the hunt clock now; each hit's age puts it on that clock.</summary>
    public void AddHits(float elapsed, IReadOnlyList<HitRecord> hits, int localSlot)
    {
        if (hits.Count == 0)
            return;

        var now = Stopwatch.GetTimestamp();
        AddHitsCore(hits, localSlot, hit => elapsed - (float)Stopwatch.GetElapsedTime(hit.Timestamp, now).TotalSeconds);
    }

    /// <summary>Trial hits: exact offset from the trial's first-hit timestamp.</summary>
    public void AddHitsRelativeTo(long startTimestamp, IReadOnlyList<HitRecord> hits, int localSlot)
    {
        if (hits.Count == 0)
            return;

        AddHitsCore(hits, localSlot, hit => (float)Stopwatch.GetElapsedTime(startTimestamp, hit.Timestamp).TotalSeconds);
    }

    private void AddHitsCore(IReadOnlyList<HitRecord> hits, int localSlot, Func<HitRecord, float> timeOf)
    {
        lock (_gate)
        {
            foreach (var hit in hits)
            {
                if (_hits.Count >= MaxHits)
                    return;

                _monsters.TryGetValue(hit.Target, out var monster);
                _hits.Add(new FightLogHit
                {
                    T = Math.Max(0f, timeOf(hit)),
                    Slot = Math.Max(0, localSlot),
                    Monster = monster?.Id,
                    Damage = hit.Damage,
                    Crit = hit.Crit,
                    Tenderized = hit.Tenderized,
                    AttackId = hit.AttackId,
                    ActionSet = hit.ActionSet,
                    ActionId = hit.ActionId,
                    Action = ActionNameLocked(hit.ActionSet, hit.ActionId)
                });
            }
        }
    }

    /// <summary>Records a monster event; ignored for small monsters (Wulg, Popo, ...) that were never tracked.</summary>
    public void AddMonsterEvent(float elapsed, string type, nint instance, string? detail = null)
    {
        lock (_gate)
        {
            if (!_monsters.TryGetValue(instance, out var monster))
                return;
            if (type == "death")
                monster.DiedT ??= elapsed;
            AddEventLocked(elapsed, type, monster: monster.Id, detail: detail);
        }
    }

    public FightLogMonster[] Monsters()
    {
        lock (_gate)
            return _monsters.Values.OrderBy(monster => monster.FirstSeenT).ToArray();
    }

    public FightLogHit[] Hits()
    {
        lock (_gate)
            return _hits.ToArray();
    }

    public FightLogEvent[] Events()
    {
        lock (_gate)
            return _events.OrderBy(evt => evt.T).ToArray();
    }

    private void AddEventLocked(float elapsed, string type, int? slot = null, string? monster = null, string? detail = null)
    {
        if (_events.Count >= MaxEvents)
            return;

        _events.Add(new FightLogEvent
        {
            T = elapsed,
            Type = type,
            Slot = slot,
            Monster = monster,
            Detail = detail
        });
    }

    private string? ActionNameLocked(int set, int id)
    {
        if (_actionNames.TryGetValue((set, id), out var cached))
            return cached;

        string? name = null;
        try
        {
            name = _resolveActionName(set, id);
        }
        catch
        {
            // action list unreadable; leave null and do not retry this pair
        }

        _actionNames[(set, id)] = string.IsNullOrWhiteSpace(name) ? null : name;
        return _actionNames[(set, id)];
    }
}
