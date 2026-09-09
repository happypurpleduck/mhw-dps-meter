using System.Diagnostics;
using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

/// <summary>One cart detected from the quest death counter, optionally attributed to a hunter.</summary>
internal readonly record struct CartRecord(int? Slot, string? Name, int Deaths, int MaxDeaths);

/// <summary>
/// Reads the quest cart counter (HunterPie's <c>QUEST_EXTRA_DATA_OFFSETS</c>: MaxDeaths + Deaths)
/// and attributes each increment when possible. The counter is party-wide; slot comes from local
/// HP hitting zero and/or a recent death-like action on a hunter entity.
/// </summary>
internal sealed class CartTracker
{
    private const double CandidateWindowSeconds = 4.0;
    private const float LocalDeadHealth = 0.5f;

    private readonly AddressMap _map;
    private readonly nint _moduleBase;
    private readonly object _gate = new();
    private int _prevDeaths = -1;
    private int _deaths;
    private int _maxDeaths;
    private bool _localWasAlive = true;
    private long _localDiedAt;
    private int _localSlot = -1;
    private string? _localName;
    private long _candidateAt;
    private int _candidateSlot = -1;
    private string? _candidateName;

    public CartTracker(AddressMap map, nint moduleBase)
    {
        _map = map;
        _moduleBase = moduleBase;
    }

    public int Deaths
    {
        get { lock (_gate) return _deaths; }
    }

    public int MaxDeaths
    {
        get { lock (_gate) return _maxDeaths; }
    }

    public string LastError { get; private set; } = "";

    public void Reset()
    {
        lock (_gate)
        {
            _prevDeaths = -1;
            _deaths = 0;
            _maxDeaths = 0;
            _localWasAlive = true;
            _localDiedAt = 0;
            _localSlot = -1;
            _localName = null;
            _candidateAt = 0;
            _candidateSlot = -1;
            _candidateName = null;
            LastError = "";
        }
    }

    /// <summary>Death-like action on a hunter entity: used when the quest counter ticks.</summary>
    public void NoteDeathAction(int slot, string? name)
    {
        if (slot < 0)
            return;
        lock (_gate)
        {
            _candidateSlot = slot;
            _candidateName = name;
            _candidateAt = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Polls the quest counter and local HP. Returns one entry per new cart since the last poll
    /// (usually 0 or 1). Empty when the offset is missing or the counter is unreadable.
    /// </summary>
    public IReadOnlyList<CartRecord> Poll(int localSlot, string? localName)
    {
        if (!_map.TryGetOffsets("QUEST_EXTRA_DATA_OFFSETS", out var offsets)
            && !_map.TryGetOffsets("QUEST_DEATH_COUNTER_OFFSETS", out offsets))
        {
            LastError = "no death-counter offsets";
            return [];
        }

        if (!_map.TryGetAddress("QUEST_DATA_ADDRESS", out var questAddress))
        {
            LastError = "no QUEST_DATA_ADDRESS";
            return [];
        }

        var basePtr = SafeMemory.Follow(_moduleBase + questAddress, offsets, out var followError);
        if (basePtr == 0)
        {
            LastError = string.IsNullOrEmpty(followError) ? "quest data unreadable" : followError;
            return [];
        }

        // HunterPie layout: MaxDeaths at +0, Deaths at +4. QUEST_DEATH_COUNTER_OFFSETS alone
        // points at Deaths, so MaxDeaths sits 4 bytes earlier.
        nint maxPtr, deathsPtr;
        if (_map.TryGetOffsets("QUEST_EXTRA_DATA_OFFSETS", out _))
        {
            maxPtr = basePtr;
            deathsPtr = basePtr + 4;
        }
        else
        {
            deathsPtr = basePtr;
            maxPtr = basePtr - 4;
        }

        if (!SafeMemory.TryRead(deathsPtr, out int deaths) || deaths is < 0 or > 99)
        {
            LastError = $"bad deaths at 0x{deathsPtr:X}";
            return [];
        }

        var maxDeaths = 0;
        if (SafeMemory.TryRead(maxPtr, out int max) && max is >= 0 and <= 99)
            maxDeaths = max;

        ObserveLocalHealth(localSlot, localName);

        lock (_gate)
        {
            _deaths = deaths;
            _maxDeaths = maxDeaths;
            LastError = "";

            if (_prevDeaths < 0)
            {
                // First successful read in this hunt: baseline only (SOS join mid-quest).
                _prevDeaths = deaths;
                return [];
            }

            if (deaths <= _prevDeaths)
            {
                _prevDeaths = deaths;
                return [];
            }

            var added = deaths - _prevDeaths;
            _prevDeaths = deaths;
            var carts = new List<CartRecord>(added);
            for (var i = 0; i < added; i++)
            {
                var (slot, name) = AttributeCart();
                carts.Add(new CartRecord(slot, name, deaths, maxDeaths));
            }

            return carts;
        }
    }

    private void ObserveLocalHealth(int localSlot, string? localName)
    {
        float health;
        try
        {
            var player = Player.MainPlayer;
            if (player is null)
                return;
            health = player.Health;
        }
        catch
        {
            return;
        }

        lock (_gate)
        {
            _localSlot = localSlot;
            _localName = localName;
            if (health <= LocalDeadHealth)
            {
                if (_localWasAlive)
                {
                    _localWasAlive = false;
                    _localDiedAt = Stopwatch.GetTimestamp();
                }
            }
            else
            {
                _localWasAlive = true;
                _localDiedAt = 0;
            }
        }
    }

    private (int? Slot, string? Name) AttributeCart()
    {
        var now = Stopwatch.GetTimestamp();
        if (!_localWasAlive && _localSlot >= 0
            && Stopwatch.GetElapsedTime(_localDiedAt, now).TotalSeconds <= CandidateWindowSeconds)
        {
            return (_localSlot, _localName);
        }

        if (_candidateSlot >= 0
            && Stopwatch.GetElapsedTime(_candidateAt, now).TotalSeconds <= CandidateWindowSeconds)
        {
            var slot = _candidateSlot;
            var name = _candidateName;
            _candidateSlot = -1;
            _candidateName = null;
            _candidateAt = 0;
            return (slot, name);
        }

        return (null, null);
    }

    /// <summary>True for action names that look like a faint / cart animation.</summary>
    public static bool LooksLikeDeathAction(string? actionName)
    {
        if (string.IsNullOrWhiteSpace(actionName))
            return false;

        var name = actionName.AsSpan().Trim();
        // Common::DIE, Common::DEATH, *_DIE, FAINT, CART, … — avoid matching "DIESEL" etc.
        return ContainsToken(name, "DIE")
            || ContainsToken(name, "DEATH")
            || ContainsToken(name, "FAINT")
            || ContainsToken(name, "CART")
            || ContainsToken(name, "DEAD");
    }

    private static bool ContainsToken(ReadOnlySpan<char> name, string token)
    {
        for (var i = 0; i <= name.Length - token.Length; i++)
        {
            if (!name.Slice(i, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase))
                continue;
            var beforeOk = i == 0 || !char.IsLetterOrDigit(name[i - 1]);
            var after = i + token.Length;
            var afterOk = after >= name.Length || !char.IsLetterOrDigit(name[after]);
            if (beforeOk && afterOk)
                return true;
        }

        return false;
    }
}
