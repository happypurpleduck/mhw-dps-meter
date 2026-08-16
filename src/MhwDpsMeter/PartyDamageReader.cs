using System.Runtime.InteropServices;
using System.Text;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.Networking;

namespace MhwDpsMeter;

internal sealed class PartyMemberSnapshot
{
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public int Damage { get; init; }
    public bool IsLocal { get; init; }
    public float Dps { get; init; }
    public float Percent { get; init; }
}

internal sealed class PartySnapshot
{
    public PartyMemberSnapshot[] Members { get; init; } = [];
    public int TotalDamage { get; init; }
    public int[] SlotDamage { get; init; } = new int[4];
}

internal sealed class PartyDamageReader
{
    public const int PartySlots = 4;
    public const int DamageStride = 0x2A0;
    public const int NameOffset = 0x49;
    public const int NameBytes = 32;
    public const int PartyMemberStride = 0x58;
    private const nint SaveSlotStride = 0x26CC00;

    private static readonly string[] SessionSingletons =
    [
        "sMhNetwork",
        "sQuest"
    ];

    private readonly AddressMap _map;
    private readonly nint _moduleBase;

    public PartyDamageReader(AddressMap map, nint moduleBase)
    {
        _map = map;
        _moduleBase = moduleBase;
    }

    public string LastError { get; private set; } = "";
    public string DamageSource { get; private set; } = "";

    public bool TryRead(out PartySnapshot snapshot, int fallbackLocalDamage = 0)
    {
        snapshot = new PartySnapshot();
        LastError = "";
        DamageSource = "";

        var partyArray = SafeMemory.Follow(
            _moduleBase + _map.GetAddress("PARTY_ADDRESS"),
            _map.GetOffsets("PARTY_OFFSETS"),
            out var partyError);
        var damageBase = ResolveDamageBase(out var damageError);
        if (damageBase != 0 && !LooksLikeDamageTable(damageBase))
        {
            damageBase = 0;
            damageError = "damage table looked invalid";
        }

        var localName = ReadSaveName();
        var members = new List<PartyMemberSnapshot>(PartySlots);
        var slotDamage = new int[PartySlots];
        var total = 0;

        if (partyArray != 0)
        {
            for (var slot = 0; slot < PartySlots; slot++)
            {
                if (!SafeMemory.TryRead<nint>(partyArray + slot * PartyMemberStride, out var memberPtr) || memberPtr == 0)
                    continue;

                if (!TryReadName(memberPtr + NameOffset, out var name) || name.Length == 0)
                    continue;

                var damage = 0;
                if (damageBase != 0 && SafeMemory.TryRead<int>(damageBase + slot * DamageStride, out var value) && value > 0)
                    damage = value;

                slotDamage[slot] = damage;
                total += damage;
                members.Add(new PartyMemberSnapshot
                {
                    Slot = slot,
                    Name = name,
                    Damage = damage,
                    IsLocal = !string.IsNullOrEmpty(localName) &&
                              string.Equals(name, localName, StringComparison.Ordinal)
                });
            }
        }

        if (members.Count == 1)
        {
            members[0] = new PartyMemberSnapshot
            {
                Slot = members[0].Slot,
                Name = members[0].Name,
                Damage = members[0].Damage,
                IsLocal = true
            };
        }

        if (members.Count == 0)
        {
            if (string.IsNullOrEmpty(localName))
                localName = "You";

            var damage = 0;
            if (damageBase != 0 && SafeMemory.TryRead<int>(damageBase, out var tableDamage) && tableDamage > 0)
                damage = tableDamage;
            if (damage == 0)
                damage = Math.Max(0, fallbackLocalDamage);

            members.Add(new PartyMemberSnapshot
            {
                Slot = 0,
                Name = localName,
                Damage = damage,
                IsLocal = true
            });
            slotDamage[0] = damage;
            total = damage;
        }
        else if (damageBase == 0 && fallbackLocalDamage > 0)
        {
            var local = members.FindIndex(member => member.IsLocal);
            if (local < 0)
                local = 0;
            var updated = members[local];
            members[local] = new PartyMemberSnapshot
            {
                Slot = updated.Slot,
                Name = updated.Name,
                Damage = fallbackLocalDamage,
                IsLocal = true
            };
            slotDamage[updated.Slot] = fallbackLocalDamage;
            total = members.Sum(member => member.Damage);
        }

        snapshot = new PartySnapshot
        {
            Members = members.ToArray(),
            TotalDamage = total,
            SlotDamage = slotDamage
        };

        if (damageBase != 0)
        {
            DamageSource = "quest-award table";
        }
        else if (fallbackLocalDamage > 0)
        {
            DamageSource = "monster HP";
        }
        else
        {
            DamageSource = "none";
            LastError = string.IsNullOrEmpty(damageError)
                ? "damage session not allocated"
                : $"damage {damageError}";
            if (partyArray == 0 && !string.IsNullOrEmpty(partyError))
                LastError = $"party {partyError}; {LastError}";
        }

        return true;
    }

    private nint ResolveDamageBase(out string error)
    {
        var offsets = _map.GetOffsets("DAMAGE_OFFSETS");
        var fromStatic = SafeMemory.Follow(
            _moduleBase + _map.GetAddress("DAMAGE_ADDRESS"),
            offsets,
            out error);
        if (fromStatic != 0)
            return fromStatic;

        foreach (var name in SessionSingletons)
        {
            MtObject? singleton = name switch
            {
                "sMhNetwork" => TrySingleton(() => Network.SingletonInstance),
                "sQuest" => TrySingleton(() => Quest.SingletonInstance),
                _ => SingletonManager.GetSingleton(name)
            };
            if (singleton is null || singleton.Instance == 0)
                continue;

            var fromObject = SafeMemory.FollowFromObject(singleton.Instance, offsets, out _);
            if (fromObject == 0 || !LooksLikeDamageTable(fromObject))
                continue;

            error = "";
            return fromObject;
        }

        return 0;
    }

    private static bool LooksLikeDamageTable(nint damageBase)
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

    private static MtObject? TrySingleton(Func<MtObject> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
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

        return TryReadName(headerBase + (nint)(SaveSlotStride * slot) + 0x50, out var name) ? name : "";
    }

    private static bool TryReadName(nint address, out string name)
    {
        name = "";
        if (!SafeMemory.TryReadBytes(address, NameBytes, out var bytes))
            return false;

        var length = Array.IndexOf(bytes, (byte)0);
        if (length == 0)
            return false;
        if (length < 0)
            length = bytes.Length;

        name = Encoding.UTF8.GetString(bytes, 0, length).Trim('\0', ' ');
        return IsHunterName(name);
    }

    private static bool IsHunterName(string name)
    {
        if (name.Length is < 1 or > 32 || name.Contains('\uFFFD'))
            return false;
        if (name.StartsWith("m_", StringComparison.Ordinal) || name.StartsWith("u_", StringComparison.Ordinal))
            return false;

        var lettersOrDigits = 0;
        foreach (var c in name)
        {
            if (char.IsControl(c))
                return false;
            if (char.IsLetterOrDigit(c))
                lettersOrDigits++;
        }

        return lettersOrDigits > 0 && lettersOrDigits * 2 >= name.Length;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern nint GetModuleHandle(string moduleName);
}
