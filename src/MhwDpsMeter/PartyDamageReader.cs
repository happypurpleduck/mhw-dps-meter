using System.Text;
using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

/// <summary>
/// Party damage reader following HunterPie's MHWPlayer.GetParty layout:
///   partySize   = Deref&lt;int&gt;(SESSION_OFFSET, SESSION_PARTY_OFFSETS)      (0 = solo, no session party)
///   partyArray  = Read(PARTY_ADDRESS, PARTY_OFFSETS)                        4 structs of 0x58 bytes
///   member      = Read&lt;ptr&gt;(partyArray + slot*0x58); name (UTF-8, 32 bytes) at member+0x49
///   damageBase  = Read(DAMAGE_ADDRESS, DAMAGE_OFFSETS); damage[slot] = Read&lt;int&gt;(damageBase + slot*0x2A0)
/// Local player = party member whose name equals the save-file name.
/// </summary>
internal sealed class PartyDamageReader
{
    public const int PartySlots = 4;
    public const int DamageStride = 0x2A0;
    public const int NameOffset = 0x49;
    public const int NameBytes = 32;
    public const int PartyMemberStride = 0x58;
    private const nint SaveSlotStride = 0x26CC00;

    private readonly AddressMap _map;
    private readonly nint _moduleBase;

    public PartyDamageReader(AddressMap map, nint moduleBase)
    {
        _map = map;
        _moduleBase = moduleBase;
    }

    public string LastError { get; private set; } = "";
    public string DamageSource { get; private set; } = "";
    public int LastPartySize { get; private set; }
    public string LastLayout { get; private set; } = "none";
    public int[] LastRawDamage { get; private set; } = new int[PartySlots];
    public string LastLocalName { get; private set; } = "";
    public string LastLocalInstance { get; private set; } = "0";
    public string LastPartyArray { get; private set; } = "0";
    public string LastPartyArrayError { get; private set; } = "";
    public string LastDamageBase { get; private set; } = "0";
    public string LastDamageError { get; private set; } = "";
    public bool LastHasPackedTable { get; private set; }
    public LiveDebugSlot[] LastSlots { get; private set; } = [];

    public bool TryRead(out PartySnapshot snapshot, int fallbackLocalDamage = 0)
    {
        try
        {
            return TryReadCore(out snapshot, fallbackLocalDamage);
        }
        catch (Exception ex)
        {
            snapshot = PartySnapshot.LocalOnly(fallbackLocalDamage, ReadSaveNameSafe(), LocalPlayerInstance());
            LastError = $"read aborted ({ex.GetType().Name})";
            DamageSource = fallbackLocalDamage > 0 ? "monster HP" : "none";
            return true;
        }
    }

    private bool TryReadCore(out PartySnapshot snapshot, int fallbackLocalDamage)
    {
        LastError = "";
        DamageSource = "";
        LastLayout = "none";
        LastSlots = [];

        var partySize = ReadPartySize();
        LastPartySize = partySize;

        var localName = ReadSaveName();
        var localInstance = LocalPlayerInstance();
        LastLocalName = localName;
        LastLocalInstance = $"0x{localInstance:X}";

        // Damage table (quest award / results-screen totals, synced for every hunter).
        var damageBase = SafeMemory.Follow(
            _moduleBase + _map.GetAddress("DAMAGE_ADDRESS"),
            _map.GetOffsets("DAMAGE_OFFSETS"),
            out var damageError);
        LastDamageError = damageError;
        LastDamageBase = $"0x{damageBase:X}";
        var hasTable = damageBase != 0 && LooksLikePackedTable(damageBase);
        LastHasPackedTable = hasTable;
        LastLayout = hasTable ? "quest-award packed" : "none";

        var rawDamage = new int[PartySlots];
        if (hasTable)
        {
            for (var slot = 0; slot < PartySlots; slot++)
            {
                if (SafeMemory.TryRead<int>(damageBase + slot * DamageStride, out var value)
                    && value is >= 0 and <= 50_000_000)
                    rawDamage[slot] = value;
            }
        }

        LastRawDamage = (int[])rawDamage.Clone();

        // Party roster.
        var partyArray = SafeMemory.Follow(
            _moduleBase + _map.GetAddress("PARTY_ADDRESS"),
            _map.GetOffsets("PARTY_OFFSETS"),
            out var partyError);
        LastPartyArray = $"0x{partyArray:X}";
        LastPartyArrayError = partyError;

        var probes = ProbeSlots(partyArray, rawDamage);
        var members = new List<PartyMemberSnapshot>(PartySlots);

        if (partySize > 0)
        {
            for (var slot = 0; slot < PartySlots; slot++)
            {
                var probe = probes[slot];
                var occupied = probe.PartyName.Length > 0 || rawDamage[slot] > 0;
                if (!occupied)
                    continue;

                var name = probe.PartyName.Length > 0 ? probe.PartyName : $"Hunter {slot + 1}";
                var isLocal = probe.PartyName.Length > 0
                              && localName.Length > 0
                              && string.Equals(probe.PartyName, localName, StringComparison.Ordinal);
                members.Add(new PartyMemberSnapshot
                {
                    Slot = slot,
                    Name = name,
                    Damage = rawDamage[slot],
                    IsLocal = isLocal,
                    Instance = isLocal ? localInstance : (nint)probe.MemberPtr
                });
            }
        }

        if (members.Count == 0)
        {
            // Solo (no session party) or roster unreadable: show ourselves only.
            var damage = rawDamage[0] > 0 ? rawDamage[0] : Math.Max(0, fallbackLocalDamage);
            members.Add(new PartyMemberSnapshot
            {
                Slot = 0,
                Name = string.IsNullOrEmpty(localName) ? "You" : localName,
                Damage = damage,
                IsLocal = true,
                Instance = localInstance
            });
        }
        else if (!members.Any(m => m.IsLocal))
        {
            // Name mismatch (encoding, or save name unreadable): fall back to the
            // game's own main-player instance, else slot 0 when alone.
            var marked = false;
            for (var i = 0; i < members.Count; i++)
            {
                if (localInstance != 0 && members[i].Instance == localInstance)
                {
                    members[i] = members[i].With(isLocal: true);
                    marked = true;
                }
            }

            if (!marked && members.Count == 1)
                members[0] = members[0].With(isLocal: true);
        }

        var hasAwardValues = hasTable && rawDamage.Any(v => v > 0);
        snapshot = PartySnapshot.From(members, hasAwardValues);

        var shown = members.ToDictionary(m => m.Slot, m => m);
        for (var slot = 0; slot < PartySlots; slot++)
        {
            shown.TryGetValue(slot, out var member);
            var p = probes[slot];
            var why = new List<string>();
            if (p.PartyName.Length > 0) why.Add("party-name");
            if (partySize > 0 && slot < partySize) why.Add($"in-size<{partySize}");
            if (rawDamage[slot] > 0) why.Add("raw-damage");
            if (p.PtrOk) why.Add("ptr");
            probes[slot] = new LiveDebugSlot
            {
                Slot = slot,
                Ptr = p.Ptr,
                PtrOk = p.PtrOk,
                PartyName = p.PartyName,
                PartyNameHex = p.PartyNameHex,
                SessionName = "",
                RawDamage = rawDamage[slot],
                Shown = member is not null,
                ShownName = member?.Name ?? "",
                IsLocal = member?.IsLocal ?? false,
                Why = why.Count == 0 ? "empty" : string.Join("+", why),
                MemberPtr = p.MemberPtr,
                StructHex = p.StructHex
            };
        }

        LastSlots = probes;

        if (hasAwardValues)
        {
            DamageSource = "quest-award packed";
        }
        else if (members.Count > 1 || partySize > 0)
        {
            DamageSource = "party list";
            LastError = hasTable
                ? "party found, award damage still 0"
                : $"party found, damage table unreadable ({damageError})";
        }
        else if (fallbackLocalDamage > 0)
        {
            DamageSource = "monster HP";
            LastError = "solo (party size 0)";
        }
        else
        {
            DamageSource = "none";
            LastError = partySize == 0
                ? "solo (party size 0), no damage yet"
                : string.IsNullOrEmpty(damageError) ? "damage table not allocated" : $"damage {damageError}";
            if (partyArray == 0 && !string.IsNullOrEmpty(partyError))
                LastError = $"party {partyError}; {LastError}";
        }

        return true;
    }

    private static LiveDebugSlot[] ProbeSlots(nint partyArray, int[] rawDamage)
    {
        var probes = new LiveDebugSlot[PartySlots];
        for (var slot = 0; slot < PartySlots; slot++)
        {
            nint memberPtr = 0;
            var ptrOk = false;
            var name = "";
            var hex = "";
            var structHex = "";
            if (partyArray != 0
                && SafeMemory.TryRead<nint>(partyArray + slot * PartyMemberStride, out memberPtr)
                && SafeMemory.LooksLikeUserPointer(memberPtr))
            {
                ptrOk = true;
                if (SafeMemory.TryReadBytes(memberPtr + NameOffset, NameBytes, out var bytes))
                {
                    hex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
                    if (TryDecodeUtf8Name(bytes, out var decoded))
                        name = decoded;
                }

                if (SafeMemory.TryReadBytes(memberPtr, 0x80, out var raw))
                    structHex = Convert.ToHexString(raw);
            }

            probes[slot] = new LiveDebugSlot
            {
                Slot = slot,
                Ptr = $"0x{memberPtr:X}",
                PtrOk = ptrOk,
                PartyName = name,
                PartyNameHex = hex,
                RawDamage = rawDamage[slot],
                MemberPtr = ptrOk ? (long)memberPtr : 0,
                StructHex = structHex
            };
        }

        return probes;
    }

    private static bool LooksLikePackedTable(nint damageBase)
    {
        for (var slot = 0; slot < PartySlots; slot++)
        {
            if (!SafeMemory.TryRead<int>(damageBase + slot * DamageStride, out var value))
                return false;
            if (value is < 0 or > 50_000_000)
                return false;
        }

        return true;
    }

    private int ReadPartySize()
    {
        if (!_map.TryGetOffsets("SESSION_PARTY_OFFSETS", out var offsets))
            return 0;
        if (!_map.TryGetAddress("SESSION_OFFSET", out var session)
            && !_map.TryGetAddress("DAMAGE_ADDRESS", out session))
            return 0;

        var address = SafeMemory.Follow(_moduleBase + session, offsets, out _);
        if (address != 0 && SafeMemory.TryRead<int>(address, out var size) && size is >= 1 and <= PartySlots)
            return size;
        return 0;
    }

    private string ReadSaveNameSafe()
    {
        try
        {
            return ReadSaveName();
        }
        catch
        {
            return "";
        }
    }

    private string ReadSaveName()
    {
        if (!_map.TryGetAddress("LEVEL_OFFSET", out var level) ||
            !_map.TryGetOffsets("LevelOffsets", out var offsets))
            return "";

        var firstSave = SafeMemory.Follow(_moduleBase + level, offsets, out _);
        if (firstSave == 0)
            return "";

        var slot = 0u;
        if (SafeMemory.TryRead<uint>(firstSave + 0x44, out var rawSlot) && rawSlot <= 2)
            slot = rawSlot;

        if (!SafeMemory.TryRead<nint>(firstSave, out var headerBase) || headerBase == 0)
            return "";

        return TryReadUtf8Name(headerBase + (nint)(SaveSlotStride * slot) + 0x50, NameBytes, out var name)
            ? name
            : "";
    }

    private static bool TryReadUtf8Name(nint address, int byteCount, out string name)
    {
        name = "";
        if (!SafeMemory.TryReadBytes(address, byteCount, out var bytes))
            return false;
        return TryDecodeUtf8Name(bytes, out name);
    }

    private static bool TryDecodeUtf8Name(byte[] bytes, out string name)
    {
        name = "";
        var length = Array.IndexOf(bytes, (byte)0);
        if (length == 0)
            return false;
        if (length < 0)
            length = bytes.Length;

        var text = Encoding.UTF8.GetString(bytes, 0, length).Trim('\0', ' ', '�');
        if (!IsHunterName(text))
            return false;

        name = text;
        return true;
    }

    private static bool IsHunterName(string name)
    {
        if (name.Length is < 1 or > 32 || name.Contains('�'))
            return false;

        foreach (var c in name)
        {
            if (char.IsControl(c))
                return false;
        }

        return true;
    }

    private static nint LocalPlayerInstance()
    {
        try
        {
            return Player.MainPlayer?.Instance ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
