using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.Memory;

namespace MhwDpsMeter;

/// <summary>One hit seen by the deal-damage hook, stamped with <see cref="Stopwatch.GetTimestamp"/>.</summary>
internal readonly record struct HitRecord(
    long Timestamp,
    nint Target,
    int Damage,
    bool Crit,
    bool Tenderized,
    int AttackId,
    int ActionSet,
    int ActionId,
    int? Part);

/// <summary>
/// Hooks the game's deal-damage function (HunterPie's FUN_DEAL_DAMAGE) to get the
/// local hunter's hits in real time. Signature from HunterPie.Native
/// Games/World/Damage/Hooks.h:
///   void DealDamage(Monster* target, int damage, void* position, BOOL isTenderized,
///                   BOOL isCrit, int unk0, int unk1, char unk2, int attackId)
/// The game only runs this for hits simulated on this client, so it can only ever
/// attribute damage (and part) to the local player. Party damage comes from the award
/// table. Part id comes from a scoped hook on the caller that has the collision context.
/// </summary>
internal sealed class DamageTracker : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DealDamageDelegate(
        nint target,
        int damage,
        nint position,
        int isTenderized,
        int isCrit,
        int unk0,
        int unk1,
        byte unk2,
        int attackId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ShowDamageContextDelegate(nint controller, nint hitData, nint damageData);

    [ThreadStatic] private static MonsterPartResolver.HitContext? _partContext;
    private Hook<ShowDamageContextDelegate>? _partHook;
    private int _partTagged;
    private int _partUnknown;
    private string _partLast = "none";
    public string PartStatus { get; private set; } = "off";
    public string PartDiagnostics
    {
        get
        {
            lock (_gate)
                return $"{PartStatus}; tagged={_partTagged} unknown={_partUnknown} last={_partLast}";
        }
    }

    private readonly object _gate = new();
    private Hook<DealDamageDelegate>? _hook;
    private HashSet<nint> _largeMonsters = [];
    private int _localDamage;
    private int _localSlot;
    private int _hits;
    private int _calls;
    private int _ignored;
    private nint _lastTarget;
    private int _lastDamage;
    private long _firstHitTimestamp;
    private bool _acceptAllTargets;
    private bool _recordHits;
    private List<HitRecord> _pending = [];
    private int _actionSet;
    private int _actionId;

    /// <summary>Hits buffered between <see cref="DrainHits"/> calls; older ones are dropped past this.</summary>
    private const int MaxPendingHits = 4096;

    public bool Hooked => _hook?.IsEnabled == true;
    public string Status { get; private set; } = "off";
    public int Hits => _hits;
    public int Calls => _calls;
    public int Ignored => _ignored;

    /// <summary>
    /// Training area: the pole and wagon are not large monsters, so count every hit
    /// the game reports instead of filtering on the tracked-monster set.
    /// </summary>
    public bool AcceptAllTargets
    {
        get { lock (_gate) return _acceptAllTargets; }
        set { lock (_gate) _acceptAllTargets = value; }
    }

    /// <summary>Buffer individual hits for the fight log (quests only; training just needs totals).</summary>
    public bool RecordHits
    {
        get { lock (_gate) return _recordHits; }
        set
        {
            lock (_gate)
            {
                _recordHits = value;
                if (!value)
                    _pending.Clear();
            }
        }
    }

    /// <summary>Local hunter's current action (from OnPlayerAction), attached to each recorded hit.</summary>
    public void SetCurrentAction(int actionSet, int actionId)
    {
        lock (_gate)
        {
            _actionSet = actionSet;
            _actionId = actionId;
        }
    }

    /// <summary>Returns and clears the hits recorded since the last call.</summary>
    public List<HitRecord> DrainHits()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return [];
            var drained = _pending;
            _pending = [];
            return drained;
        }
    }

    /// <summary>Wall-clock time since the first counted hit after the last reset; zero if none yet.</summary>
    public TimeSpan SinceFirstHit
    {
        get
        {
            long first;
            lock (_gate)
                first = _firstHitTimestamp;
            return first == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(first);
        }
    }
    public string LastHit
    {
        get
        {
            lock (_gate)
                return $"target=0x{_lastTarget:X} dmg={_lastDamage}";
        }
    }

    public void Install(nint moduleBase, AddressMap map)
    {
        if (_hook is not null)
            return;

        if (!map.TryGetAddress("FUN_DEAL_DAMAGE", out var rva) || rva == 0)
        {
            Status = "no FUN_DEAL_DAMAGE";
            return;
        }

        try
        {
            var address = (long)(moduleBase + rva);
            _hook = Hook.Create<DealDamageDelegate>(address, OnDealDamage);
            _hook.Enable();
            Status = $"hook 0x{address:X}";
            Log.Info($"MhwDpsMeter: damage hook enabled at 0x{address:X}.");
            InstallPartHook(moduleBase, map);
        }
        catch (Exception ex)
        {
            Status = $"hook failed ({ex.GetType().Name})";
            Log.Warn($"MhwDpsMeter: {Status}: {ex.Message}");
        }
    }

    private void InstallPartHook(nint moduleBase, AddressMap map)
    {
        if (!map.TryGetAddress("FUN_SHOW_DAMAGE_CONTEXT", out var rva) || rva == 0)
        {
            PartStatus = "no verified context hook for this map";
            return;
        }

        try
        {
            var address = moduleBase + rva;
            // Fail closed if the function changes or another mod has patched it.
            byte[] expected = [0x40, 0x55, 0x56, 0x41, 0x55, 0x48, 0x81, 0xEC,
                0x80, 0, 0, 0, 0x48, 0x8B, 0xF2, 0x4C, 0x8B, 0xE9];
            if (!SafeMemory.TryReadBytes(address, expected.Length, out var actual)
                || !actual.SequenceEqual(expected))
            {
                PartStatus = "context hook signature mismatch";
                Log.Warn($"MhwDpsMeter: {PartStatus}.");
                return;
            }

            _partHook = Hook.Create<ShowDamageContextDelegate>((long)address, OnShowDamageContext);
            _partHook.Enable();
            PartStatus = $"collision hook 0x{address:X}";
            Log.Info($"MhwDpsMeter: {PartStatus}.");
        }
        catch (Exception ex)
        {
            PartStatus = $"context hook failed ({ex.GetType().Name})";
            Log.Warn($"MhwDpsMeter: {PartStatus}: {ex.Message}");
        }
    }

    private void OnShowDamageContext(nint controller, nint hitData, nint damageData)
    {
        var previous = _partContext;
        _partContext = null;
        try
        {
            try
            {
                // Training objects do not share the large-monster part layout.
                if (RecordHits && !AcceptAllTargets
                    && SafeMemory.TryRead<nint>(controller + 0x08, out var target))
                {
                    bool tracked;
                    lock (_gate) tracked = _largeMonsters.Contains(target);
                    if (tracked)
                        _partContext = MonsterPartResolver.Capture(controller, hitData);
                }
            }
            catch (Exception ex)
            {
                lock (_gate) _partLast = $"context read failed ({ex.GetType().Name})";
            }

            // The nested DealDamage callback consumes this context synchronously.
            _partHook?.Original(controller, hitData, damageData);
        }
        finally
        {
            _partContext = previous;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _localDamage = 0;
            _hits = 0;
            _calls = 0;
            _ignored = 0;
            _partTagged = 0;
            _partUnknown = 0;
            _partLast = "none";
            _lastTarget = 0;
            _lastDamage = 0;
            _firstHitTimestamp = 0;
            _pending.Clear();
        }
    }

    public void Uninstall()
    {
        try
        {
            _hook?.Disable();
        }
        catch
        {
            // already gone on unload
        }

        try
        {
            _partHook?.Disable();
        }
        catch
        {
            // already gone on unload
        }
        _partHook = null;
        PartStatus = "off";
        _hook = null;
        Status = "off";
        Reset();
    }

    public void Dispose() => Uninstall();

    /// <summary>Large monster instances currently alive; hits on anything else are ignored.</summary>
    public void UpdateMonsters(IEnumerable<nint> instances)
    {
        var set = new HashSet<nint>(instances);
        lock (_gate)
            _largeMonsters = set;
    }

    public void UpdateParty(IReadOnlyList<PartyMemberSnapshot> members)
    {
        var slot = 0;
        foreach (var member in members)
        {
            if (member.IsLocal && member.Slot is >= 0 and < PartyDamageReader.PartySlots)
            {
                slot = member.Slot;
                break;
            }
        }

        lock (_gate)
            _localSlot = slot;
    }

    /// <summary>Per-slot damage seen by the hook. Only the local slot is ever non-zero.</summary>
    public int[] Snapshot()
    {
        var result = new int[PartyDamageReader.PartySlots];
        lock (_gate)
            result[_localSlot] = _localDamage;
        return result;
    }

    public int LocalDamage
    {
        get
        {
            lock (_gate)
                return _localDamage;
        }
    }

    private void OnDealDamage(
        nint target, int damage, nint position, int isTenderized, int isCrit,
        int unk0, int unk1, byte unk2, int attackId)
    {
        var hook = _hook;
        try
        {
            hook?.Original(target, damage, position, isTenderized, isCrit, unk0, unk1, unk2, attackId);
        }
        finally
        {
            try
            {
                var context = _partContext;
                var part = context?.Match(target, position);
                var reason = context is null ? "no hit context"
                    : context.Value.Target != target || context.Value.Position != position
                        ? "hit context mismatch" : context.Value.Status;
                Record(target, damage, isTenderized != 0, isCrit != 0, attackId, part, reason);
            }
            catch
            {
                // never let bookkeeping break the game's damage call
            }
        }
    }

    private void Record(nint target, int damage, bool tenderized, bool crit, int attackId, int? part, string partReason)
    {
        lock (_gate)
        {
            _calls++;
            _lastTarget = target;
            _lastDamage = damage;
            if (damage is <= 0 or > 100_000)
            {
                _ignored++;
                return;
            }

            if (!_acceptAllTargets && !_largeMonsters.Contains(target))
            {
                _ignored++;
                return;
            }

            var next = _localDamage + damage;
            if (next is >= 0 and <= 50_000_000)
                _localDamage = next;
            var now = Stopwatch.GetTimestamp();
            if (_hits == 0)
                _firstHitTimestamp = now;
            _hits++;

            if (_recordHits && _pending.Count < MaxPendingHits)
            {
                if (part is not null) _partTagged++;
                else _partUnknown++;
                _partLast = partReason;
                _pending.Add(new HitRecord(now, target, damage, crit, tenderized, attackId, _actionSet, _actionId, part));
            }
        }
    }
}
