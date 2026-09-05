namespace MhwDpsMeter;

internal sealed class PartyMemberSnapshot
{
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public int Damage { get; init; }
    public bool IsLocal { get; init; }
    public nint Instance { get; init; }
    public float Dps { get; init; }
    public float Percent { get; init; }

    public PartyMemberSnapshot With(
        string? name = null,
        int? damage = null,
        bool? isLocal = null,
        nint? instance = null,
        float? dps = null,
        float? percent = null) => new()
    {
        Slot = Slot,
        Name = name ?? Name,
        Damage = damage ?? Damage,
        IsLocal = isLocal ?? IsLocal,
        Instance = instance ?? Instance,
        Dps = dps ?? Dps,
        Percent = percent ?? Percent
    };
}

internal sealed class PartySnapshot
{
    public PartyMemberSnapshot[] Members { get; init; } = [];
    public int TotalDamage { get; init; }
    public int[] SlotDamage { get; init; } = new int[PartyDamageReader.PartySlots];
    public bool HasAwardTable { get; init; }

    /// <summary>Slot of the local hunter, or -1 if none is marked.</summary>
    public int LocalSlot => Members.FirstOrDefault(member => member.IsLocal)?.Slot ?? -1;

    /// <summary>Builds a snapshot, deriving the per-slot array and total from the members.</summary>
    public static PartySnapshot From(IEnumerable<PartyMemberSnapshot> members, bool hasAwardTable)
    {
        var array = members as PartyMemberSnapshot[] ?? members.ToArray();
        var slotDamage = new int[PartyDamageReader.PartySlots];
        var total = 0;
        foreach (var member in array)
        {
            if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                slotDamage[member.Slot] = member.Damage;
            total += member.Damage;
        }

        return new PartySnapshot
        {
            Members = array,
            TotalDamage = total,
            SlotDamage = slotDamage,
            HasAwardTable = hasAwardTable
        };
    }

    /// <summary>Single local hunter in slot 0 (solo, training, or roster unreadable).</summary>
    public static PartySnapshot LocalOnly(int damage, string name, nint instance)
    {
        damage = Math.Max(0, damage);
        return From(
        [
            new PartyMemberSnapshot
            {
                Slot = 0,
                Name = string.IsNullOrEmpty(name) ? "You" : name,
                Damage = damage,
                IsLocal = true,
                Instance = instance
            }
        ], hasAwardTable: false);
    }

    /// <summary>Copy with DPS and party share filled in for the given hunt duration.</summary>
    public PartySnapshot WithRates(float elapsedSeconds)
    {
        var duration = Math.Max(elapsedSeconds, 1f);
        var total = Math.Max(TotalDamage, 0);
        return new PartySnapshot
        {
            Members = Members.Select(member => member.With(
                dps: member.Damage / duration,
                percent: total > 0 ? 100f * member.Damage / total : 0f)).ToArray(),
            TotalDamage = TotalDamage,
            SlotDamage = SlotDamage,
            HasAwardTable = HasAwardTable
        };
    }
}
