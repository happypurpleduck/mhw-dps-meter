using System.Runtime.InteropServices;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.Memory;

namespace MhwDpsMeter;

/// <summary>
/// Hooks the game's deal-damage function (HunterPie's FUN_DEAL_DAMAGE) to get the
/// local hunter's hits in real time. Signature from HunterPie.Native
/// Games/World/Damage/Hooks.h:
///   void DealDamage(Monster* target, int damage, void* position, BOOL isTenderized,
///                   BOOL isCrit, int unk0, int unk1, char unk2, int attackId)
/// The game only runs this for hits simulated on this client, so it can only ever
/// attribute damage to the local player. Party damage comes from the award table.
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

    public bool Hooked => _hook?.IsEnabled == true;
    public string Status { get; private set; } = "off";
    public int Hits => _hits;
    public int Calls => _calls;
    public int Ignored => _ignored;
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
        }
        catch (Exception ex)
        {
            Status = $"hook failed ({ex.GetType().Name})";
            Log.Warn($"MhwDpsMeter: {Status}: {ex.Message}");
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
            _lastTarget = 0;
            _lastDamage = 0;
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
                Record(target, damage);
            }
            catch
            {
                // never let bookkeeping break the game's damage call
            }
        }
    }

    private void Record(nint target, int damage)
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

            if (!_largeMonsters.Contains(target))
            {
                _ignored++;
                return;
            }

            var next = _localDamage + damage;
            if (next is >= 0 and <= 50_000_000)
                _localDamage = next;
            _hits++;
        }
    }
}
