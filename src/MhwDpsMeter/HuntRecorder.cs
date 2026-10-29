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
    private readonly List<FightLogGaugeSample> _gaugeSamples = [];
    private readonly Dictionary<string, double> _lastGaugeSampleTime = [];
    private readonly GaugeCaptureTimeline _gaugeTimeline = new();
    public const int MaxGaugeSamples = 3600;
    private int _gaugeSamplesDropped;
    private const double GaugeSamplePeriod = 2.0;
    /// <summary>Latest monster entry per instance; the game reuses instances, so a second
    /// Beotodus spawning after the first died gets its own entry ("m2") under the same pointer.</summary>
    private readonly Dictionary<nint, FightLogMonster> _monsters = [];
    private readonly List<FightLogMonster> _allMonsters = [];
    private readonly Dictionary<int, string> _weaponBySlot = [];
    private int _estimatedHits;
    private readonly Dictionary<(int Set, int Id), string?> _actionNames = [];
    private readonly Dictionary<int, string> _roster = [];
    private readonly Dictionary<int, int> _cartsBySlot = [];
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
            _gaugeSamples.Clear();
            _lastGaugeSampleTime.Clear();
            _gaugeTimeline.Reset();
            _gaugeSamplesDropped = 0;
            _monsters.Clear();
            _allMonsters.Clear();
            _weaponBySlot.Clear();
            _estimatedHits = 0;
            _actionNames.Clear();
            _roster.Clear();
            _cartsBySlot.Clear();
            _localWeapon = null;
        }
    }

    public void ObserveMonsters(float elapsed, IReadOnlyList<MonsterState> monsters)
    {
        lock (_gate)
        {
            foreach (var state in monsters)
            {
                var exists = _monsters.TryGetValue(state.Instance, out var entry);
                // Same pointer, but HP jumped back up (after a death or a large heal-from-zero):
                // the game re-used the instance for a new monster.
                // A new monster can reuse the same pointer without disappearing.
                // Compare identity as well as the large HP reset of a new spawn.
                var respawned = exists && (
                    (int)state.Type != entry!.Type
                    || state.Variant != entry.Variant
                    || !string.Equals(state.Name, entry.Name, StringComparison.Ordinal)
                    || Math.Abs(state.MaxHealth - entry.MaxHealth) > 0.01f
                    || (entry.MaxHealth > 0
                        && (entry.DiedT.HasValue || state.Health > entry.LastHealth + 0.25f * entry.MaxHealth)
                        && state.Health > 0.5f * state.MaxHealth));
                if (!exists || respawned)
                {
                    entry = new FightLogMonster
                    {
                        Id = $"m{_allMonsters.Count + 1}",
                        Type = (int)state.Type,
                        Name = state.Name,
                        Variant = state.Variant,
                        MaxHealth = state.MaxHealth,
                        FirstSeenT = elapsed
                    };
                    _monsters[state.Instance] = entry;
                    _allMonsters.Add(entry);
                }

                entry!.LastHealth = state.Health;
                if (entry.MaxHealth <= 0 && state.MaxHealth > 0)
                    entry.MaxHealth = state.MaxHealth;
                if (string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(state.Name))
                    entry.Name = state.Name;
            }
        }
    }

    /// <summary>Captures sparse locally observed state, not attribution to a hunter.
    /// Scheduling uses a process-monotonic timebase independent of the hunt timestamp.</summary>
    public void ObserveGauges(float elapsed, double monotonicSeconds, string huntClockSource, IReadOnlyList<MonsterGaugeState> gauges)
    {
        if (gauges.Count == 0)
            return;

        lock (_gate)
        {
            var capture = _gaugeTimeline.Advance(elapsed, monotonicSeconds, huntClockSource);
            foreach (var gauge in gauges)
            {
                if (!_monsters.TryGetValue(gauge.Instance, out var monster))
                    continue;
                if (gauge.Stamina is null && gauge.Parts.Length == 0 && gauge.Ailments.Length == 0)
                    continue;
                if (_lastGaugeSampleTime.TryGetValue(monster.Id, out var previous)
                    && monotonicSeconds < previous + GaugeSamplePeriod)
                    continue;

                _lastGaugeSampleTime[monster.Id] = monotonicSeconds;
                if (_gaugeSamples.Count >= MaxGaugeSamples)
                {
                    // Count due samples only, not repeated 100ms polls.
                    if (_gaugeSamplesDropped < int.MaxValue)
                        _gaugeSamplesDropped++;
                    continue;
                }
                _gaugeSamples.Add(new FightLogGaugeSample
                {
                    T = capture.T,
                    RawHuntT = Math.Max(0, elapsed),
                    ClockAdjusted = capture.Adjusted,
                    Monster = monster.Id,
                    Source = "local-unverified",
                    Stamina = gauge.Stamina,
                    MaxStamina = gauge.MaxStamina,
                    Parts = gauge.Parts.ToArray(),
                    Ailments = gauge.Ailments.ToArray()
                });
            }
        }
    }

    public int GaugeClockDiscontinuities
    {
        get { lock (_gate) return _gaugeTimeline.Discontinuities; }
    }

    public int GaugeSamplesDropped
    {
        get { lock (_gate) return _gaugeSamplesDropped; }
    }

    public FightLogGaugeSample[] GaugeSamples()
    {
        lock (_gate)
            return _gaugeSamples.ToArray(); // capture order, never re-sort on raw quest time
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
                {
                    AddEventLocked(elapsed, "leave", slot: slot, detail: known);
                    _weaponBySlot.Remove(slot);
                }
                AddEventLocked(elapsed, "join", slot: slot, detail: name);
                _roster[slot] = name;
            }

            foreach (var slot in _roster.Keys.Where(slot => !current.ContainsKey(slot)).ToArray())
            {
                AddEventLocked(elapsed, "leave", slot: slot, detail: _roster[slot]);
                _roster.Remove(slot);
                _weaponBySlot.Remove(slot);
            }
        }
    }

    public void ObserveWeapon(float elapsed, WeaponType? weapon, int localSlot)
    {
        if (weapon is null or WeaponType.None)
            return;

        var name = GameNames.Weapon(weapon);
        if (name is null)
            return;
        lock (_gate)
        {
            if (localSlot >= 0)
                _weaponBySlot[localSlot] = name;
            if (_localWeapon == name)
                return;
            _actionNames.Clear();
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
                string? partName = null;
                if (hit.Part is int partId && monster is not null)
                    partName = MonsterParts.Name(monster.Type, partId);
                _hits.Add(new FightLogHit
                {
                    T = Math.Max(0f, timeOf(hit)),
                    Slot = Math.Max(0, localSlot),
                    Monster = monster?.Id,
                    Damage = hit.Damage,
                    Crit = hit.Crit,
                    Tenderized = hit.Tenderized,
                    AttackId = hit.AttackId,
                    Part = hit.Part,
                    PartName = partName,
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
            return _allMonsters.OrderBy(monster => monster.FirstSeenT).ToArray();
    }

    /// <summary>Id of the only large monster currently alive, or null when zero or several are.</summary>
    public string? SingleLiveMonsterId()
    {
        lock (_gate)
        {
            var alive = _monsters.Values.Where(m => !m.DiedT.HasValue && m.LastHealth > 0).ToList();
            return alive.Count == 1 ? alive[0].Id : null;
        }
    }

    /// <summary>Teammate weapons come from their hunter entity once matched to a slot.</summary>
    public void ObserveSlotWeapons(float elapsed, IReadOnlyList<(int Slot, string Weapon)> weapons)
    {
        lock (_gate)
        {
            foreach (var (slot, weapon) in weapons)
            {
                if (_weaponBySlot.TryGetValue(slot, out var known) && known == weapon)
                    continue;
                _weaponBySlot[slot] = weapon;
                AddEventLocked(elapsed, "weapon", slot: slot, detail: weapon);
            }
        }
    }

    public string? WeaponOf(int slot)
    {
        lock (_gate)
            return _weaponBySlot.TryGetValue(slot, out var weapon) ? weapon : null;
    }

    /// <summary>Teammate rows from <see cref="PartyActionTracker"/>; already carry their time and slot.</summary>
    public void AddEstimatedHits(IReadOnlyList<FightLogHit> hits)
    {
        if (hits.Count == 0)
            return;
        lock (_gate)
        {
            foreach (var hit in hits)
            {
                if (_hits.Count >= MaxHits)
                    return;
                _hits.Add(hit);
                _estimatedHits++;
            }
        }
    }

    public void AddEvent(float elapsed, string type, int? slot = null, string? detail = null)
    {
        lock (_gate)
            AddEventLocked(elapsed, type, slot: slot, detail: detail);
    }

    /// <summary>Records a hunter cart from the quest death counter; slot may be null when unattributed.</summary>
    public void AddCart(float elapsed, int? slot, string? name)
    {
        lock (_gate)
        {
            if (slot is >= 0 and < PartyDamageReader.PartySlots)
                _cartsBySlot[slot.Value] = _cartsBySlot.GetValueOrDefault(slot.Value) + 1;
            AddEventLocked(elapsed, "cart", slot: slot, detail: name);
        }
    }

    public int CartsOf(int slot)
    {
        lock (_gate)
            return _cartsBySlot.GetValueOrDefault(slot);
    }

    /// <summary>"party-estimated" once any teammate rows exist, else "local".</summary>
    public string HitCoverage
    {
        get { lock (_gate) return _estimatedHits > 0 ? "party-estimated" : "local"; }
    }

    public FightLogHit[] Hits()
    {
        lock (_gate)
            return _hits.OrderBy(hit => hit.T).ToArray();
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
