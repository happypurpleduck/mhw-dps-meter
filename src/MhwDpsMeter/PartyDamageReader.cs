using System.Runtime.InteropServices;
using System.Text;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.Entities;

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

    private readonly AddressMap _map;
    private readonly nint _moduleBase;

    public PartyDamageReader(AddressMap map, nint moduleBase)
    {
        _map = map;
        _moduleBase = moduleBase;
    }

    public bool TryRead(out PartySnapshot snapshot)
    {
        snapshot = new PartySnapshot();

        var partyArray = SafeMemory.Follow(
            _moduleBase + _map.GetAddress("PARTY_ADDRESS"),
            _map.GetOffsets("PARTY_OFFSETS"));
        var damageBase = SafeMemory.Follow(
            _moduleBase + _map.GetAddress("DAMAGE_ADDRESS"),
            _map.GetOffsets("DAMAGE_OFFSETS"));

        if (partyArray == 0 || damageBase == 0)
            return false;

        var localName = Player.MainPlayer?.Name ?? "";
        var members = new List<PartyMemberSnapshot>(PartySlots);
        var slotDamage = new int[PartySlots];
        var total = 0;

        for (var slot = 0; slot < PartySlots; slot++)
        {
            if (!SafeMemory.TryRead<nint>(partyArray + slot * PartyMemberStride, out var memberPtr) || memberPtr == 0)
                continue;

            if (!TryReadName(memberPtr + NameOffset, out var name) || name.Length == 0)
                continue;

            var damage = 0;
            if (SafeMemory.TryRead<int>(damageBase + slot * DamageStride, out var value) && value > 0)
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

        snapshot = new PartySnapshot
        {
            Members = members.ToArray(),
            TotalDamage = total,
            SlotDamage = slotDamage
        };
        return members.Count > 0;
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
        return name.Length > 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern nint GetModuleHandle(string? moduleName);
}
