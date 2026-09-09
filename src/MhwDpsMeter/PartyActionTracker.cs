using System.Diagnostics;
using SharpPluginLoader.Core.Actions;
using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

/// <summary>
/// Per-move damage for teammates, estimated.
///
/// The game only runs its deal-damage function for the local hunter, but it does run every
/// hunter's action controller locally (SPL's OnEntityAction fires for all uPlayer entities),
/// and the quest-award damage table is synced live per slot. So: watch each hunter entity's
/// current action, and on every poll credit a slot's damage delta to the action of the entity
/// standing in that slot.
///
/// Nothing in the address map ties a hunter entity to a party slot, so the mapping is learned:
/// with one teammate it is immediate; with more, each slot's damage increments score the
/// entities that were mid-attack at that moment until one clearly dominates. Until an entity
/// is matched its slot's deltas are not attributed (totals are unaffected; they come from the
/// award table).
/// </summary>
internal sealed class PartyActionTracker
{
    /// <summary>Hits that register just after an action ends belong to the previous action.</summary>
    private const double ActionCarryOverSeconds = 0.15;
    private const double WeaponRefreshSeconds = 5;
    private const int MinCorrelationDamage = 400;
    private const double CorrelationDominance = 3.0;

    private sealed class Hunter
    {
        public nint Instance;
        public int Slot = -1;
        public string Matched = "";
        public string? Weapon;
        public long WeaponReadAt;
        public (int Set, int Id) Action;
        public (int Set, int Id) Previous;
        public long ActionStart;
        public int Actions;
        public readonly double[] Score = new double[PartyDamageReader.PartySlots];
    }

    private readonly object _gate = new();
    private readonly Dictionary<nint, Hunter> _hunters = [];
    private readonly Dictionary<nint, bool> _isPlayer = [];
    private readonly Dictionary<(nint, int, int), string?> _names = [];
    private Dictionary<int, (string Name, nint Instance)> _roster = [];
    private readonly Func<nint, int, int, string?> _resolveName;
    private readonly Func<nint, bool> _looksAlive;
    private readonly Func<nint, bool>? _isHunter;
    private nint _localInstance;
    private int _localSlot = -1;

    /// <param name="isHunter">
    /// Decides whether an action-hook entity is a hunter without calling into the object.
    /// When null, falls back to Entity.Is("uPlayer"), which is only safe for simulated entities.
    /// </param>
    public PartyActionTracker(
        Func<nint, int, int, string?> resolveName,
        Func<nint, bool>? looksAlive = null,
        Func<nint, bool>? isHunter = null)
    {
        _resolveName = resolveName;
        _looksAlive = looksAlive ?? (_ => true);
        _isHunter = isHunter;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _hunters.Clear();
            _names.Clear();
            _isPlayer.Clear();
            _roster.Clear();
            _localInstance = 0;
            _localSlot = -1;
        }
    }

    /// <summary>
    /// Roster pointers identify occupants, not hunter entities. On a roster change,
    /// discard learned associations and wait for fresh entity actions before rematching.
    /// Returns true when award deltas need a new baseline.
    /// </summary>
    public bool ObserveParty(IReadOnlyList<PartyMemberSnapshot> members)
    {
        lock (_gate)
        {
            var current = members.ToDictionary(m => m.Slot, m => (m.Name, m.Instance));
            var changed = _roster.Count > 0 &&
                (_roster.Count != current.Count || _roster.Any(entry =>
                    !current.TryGetValue(entry.Key, out var occupant) || occupant != entry.Value));
            _roster = current;
            if (changed)
            {
                _hunters.Clear();
                _names.Clear();
                _isPlayer.Clear();
            }
            return changed;
        }
    }

    public void SetLocal(nint instance, int slot)
    {
        lock (_gate)
        {
            _localInstance = instance;
            _localSlot = slot;
            if (instance != 0 && _hunters.TryGetValue(instance, out var me) && me.Slot < 0)
            {
                me.Slot = slot;
                me.Matched = "local";
            }
        }
    }

    /// <summary>Party slot for a hunter entity once matched (or local); -1 when unknown.</summary>
    public int SlotOf(nint instance)
    {
        lock (_gate)
        {
            if (instance != 0 && instance == _localInstance)
                return _localSlot;
            return _hunters.TryGetValue(instance, out var hunter) ? hunter.Slot : -1;
        }
    }

    /// <summary>SPL OnEntityAction: keep the current/previous action of every hunter entity.</summary>
    public void OnAction(Entity entity, ActionInfo action)
    {
        var instance = entity.Instance;
        if (instance == 0)
            return;

        lock (_gate)
        {
            if (!_isPlayer.TryGetValue(instance, out var isPlayer))
            {
                // SPL's action hook covers every action controller in the game, and some owner
                // pointers are not live entities. Entity.Is() calls through the object's vtable;
                // on a bad pointer that is an AccessViolation no managed catch can stop
                // (game crash 2026-09-06). Prefer the pointer-validated check.
                isPlayer = _isHunter is not null ? _isHunter(instance) : SafeIs(entity, "uPlayer");
                _isPlayer[instance] = isPlayer;
            }

            if (!isPlayer)
                return;

            if (!_hunters.TryGetValue(instance, out var hunter))
            {
                hunter = new Hunter { Instance = instance };
                if (instance == _localInstance)
                {
                    hunter.Slot = _localSlot;
                    hunter.Matched = "local";
                }

                _hunters[instance] = hunter;
            }

            var next = (action.ActionSet, action.ActionId);
            if (next != hunter.Action)
            {
                hunter.Previous = hunter.Action;
                hunter.Action = next;
                hunter.ActionStart = Stopwatch.GetTimestamp();
            }

            hunter.Actions++;
        }
    }

    /// <summary>
    /// Credits this poll's per-slot damage deltas to the matched hunters' actions and returns
    /// them as estimated hits. <paramref name="onMatched"/> fires once per newly matched slot.
    /// </summary>
    public List<FightLogHit> Attribute(
        float elapsed,
        int[] slotDeltas,
        IReadOnlyList<PartyMemberSnapshot> members,
        string? monsterId,
        Action<int, string> onMatched)
    {
        var hits = new List<FightLogHit>();
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            // Despawned entities must not remain candidates or keep occupying a slot.
            foreach (var instance in _hunters.Keys.Where(instance => !_looksAlive(instance)).ToArray())
            {
                _hunters.Remove(instance);
                _isPlayer.Remove(instance);
                foreach (var key in _names.Keys.Where(key => key.Item1 == instance).ToArray())
                    _names.Remove(key);
            }
            RefreshWeapons(now);

            var occupied = members.Where(m => m.Slot is >= 0 and < PartyDamageReader.PartySlots).Select(m => m.Slot).ToHashSet();
            var unmappedSlots = occupied.Where(s => s != _localSlot && !_hunters.Values.Any(h => h.Slot == s)).ToList();
            var unmappedHunters = _hunters.Values.Where(h => h.Slot < 0 && h.Instance != _localInstance).ToList();

            // Learn the mapping from correlation between slot deltas and attacking entities.
            for (var slot = 0; slot < slotDeltas.Length && slot < PartyDamageReader.PartySlots; slot++)
            {
                var delta = slotDeltas[slot];
                if (delta <= 0 || slot == _localSlot || !unmappedSlots.Contains(slot))
                    continue;
                foreach (var hunter in unmappedHunters)
                {
                    if (IsAttacking(hunter, now))
                        hunter.Score[slot] += delta;
                }
            }

            if (unmappedSlots.Count == 1 && unmappedHunters.Count == 1)
            {
                Match(unmappedHunters[0], unmappedSlots[0], "only-candidate", onMatched);
            }
            else
            {
                foreach (var hunter in unmappedHunters)
                {
                    var best = -1;
                    double bestScore = 0, second = 0;
                    foreach (var slot in unmappedSlots)
                    {
                        var score = hunter.Score[slot];
                        if (score > bestScore)
                        {
                            second = bestScore;
                            bestScore = score;
                            best = slot;
                        }
                        else if (score > second)
                        {
                            second = score;
                        }
                    }

                    // The winner must dominate in BOTH directions. Otherwise simultaneous
                    // attacks give several hunters the same score for a slot and dictionary
                    // iteration order silently decides whose weapon that player gets.
                    var rivalScore = best < 0 ? 0 : unmappedHunters
                        .Where(other => other != hunter && other.Slot < 0)
                        .Select(other => other.Score[best]).DefaultIfEmpty(0).Max();
                    if (best >= 0 && bestScore >= MinCorrelationDamage
                        && bestScore >= CorrelationDominance * Math.Max(second, 1)
                        && bestScore >= CorrelationDominance * Math.Max(rivalScore, 1))
                    {
                        Match(hunter, best, $"correlation {bestScore:0} vs {second:0}", onMatched);
                        unmappedSlots.Remove(best);
                    }
                }
            }

            // Attribute deltas of matched, non-local slots.
            for (var slot = 0; slot < slotDeltas.Length && slot < PartyDamageReader.PartySlots; slot++)
            {
                var delta = slotDeltas[slot];
                if (delta <= 0 || slot == _localSlot)
                    continue;
                var hunter = _hunters.Values.FirstOrDefault(h => h.Slot == slot);
                if (hunter is null)
                    continue;

                var action = Stopwatch.GetElapsedTime(hunter.ActionStart, now).TotalSeconds < ActionCarryOverSeconds && hunter.Previous != default
                    ? hunter.Previous
                    : hunter.Action;
                hits.Add(new FightLogHit
                {
                    T = elapsed,
                    Slot = slot,
                    Monster = monsterId,
                    Damage = delta,
                    Estimated = true,
                    AttackId = -1,
                    ActionSet = action.Set,
                    ActionId = action.Id,
                    Action = NameOf(hunter.Instance, action.Set, action.Id)
                });
            }
        }

        return hits;
    }

    /// <summary>Weapon type per matched slot (local included when its entity was seen).</summary>
    public IReadOnlyList<(int Slot, string Weapon)> SlotWeapons()
    {
        lock (_gate)
            return _hunters.Values.Where(h => h.Slot >= 0 && h.Weapon is not null).Select(h => (h.Slot, h.Weapon!)).ToList();
    }

    public string Diagnostics()
    {
        lock (_gate)
        {
            if (_hunters.Count == 0)
                return "no hunter entities seen";
            // Shown from the hub too, when these entities may be gone: cached names only, no dereference.
            return string.Join(" | ", _hunters.Values.Select(h =>
                $"0x{h.Instance:X} slot={(h.Slot < 0 ? "?" : h.Slot.ToString())}({h.Matched}) {h.Weapon ?? "-"} acts={h.Actions} cur={CachedName(h.Instance, h.Action.Set, h.Action.Id) ?? $"{h.Action.Set}/{h.Action.Id}"}"));
        }
    }

    private void Match(Hunter hunter, int slot, string how, Action<int, string> onMatched)
    {
        hunter.Slot = slot;
        hunter.Matched = how;
        onMatched(slot, how);
    }

    private bool IsAttacking(Hunter hunter, long now)
    {
        // Weapon actions are "WP_xx::..."; common movement is "Common::...". Recently started
        // actions still count because hits land during them.
        var name = NameOf(hunter.Instance, hunter.Action.Set, hunter.Action.Id);
        if (name is not null)
            return name.StartsWith("WP_", StringComparison.Ordinal);
        return Stopwatch.GetElapsedTime(hunter.ActionStart, now).TotalSeconds < 2.0;
    }

    private void RefreshWeapons(long now)
    {
        foreach (var hunter in _hunters.Values)
        {
            if (hunter.Weapon is not null && Stopwatch.GetElapsedTime(hunter.WeaponReadAt, now).TotalSeconds < WeaponRefreshSeconds)
                continue;
            hunter.WeaponReadAt = now;
            if (!_looksAlive(hunter.Instance))
                continue;
            try
            {
                var weapon = GameNames.Weapon(new Player(hunter.Instance).CurrentWeaponType);
                if (weapon is not null && hunter.Weapon != weapon)
                {
                    foreach (var key in _names.Keys.Where(key => key.Item1 == hunter.Instance).ToArray())
                        _names.Remove(key);
                    hunter.Weapon = weapon;
                }
            }
            catch
            {
                // entity torn down
            }
        }
    }

    private string? CachedName(nint instance, int set, int id) =>
        _names.TryGetValue((instance, set, id), out var cached) ? cached : null;

    private string? NameOf(nint instance, int set, int id)
    {
        if (_names.TryGetValue((instance, set, id), out var cached))
            return cached;
        if (!_looksAlive(instance))
            return null;
        string? name = null;
        try
        {
            name = _resolveName(instance, set, id);
        }
        catch
        {
            // action list unreadable
        }

        _names[(instance, set, id)] = string.IsNullOrWhiteSpace(name) ? null : name;
        return _names[(instance, set, id)];
    }

    private static bool SafeIs(Entity entity, string dti)
    {
        try
        {
            return entity.Is(dti);
        }
        catch
        {
            return false;
        }
    }
}
