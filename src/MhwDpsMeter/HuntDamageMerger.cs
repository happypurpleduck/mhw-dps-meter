namespace MhwDpsMeter;

/// <summary>
/// Ordinary-quest damage projection. A working local hit hook measures zero
/// before attacks; unrelated monster HP loss must not replace that reading.
/// </summary>
internal static class HuntDamageMerger
{
    public static PartySnapshot Merge(
        PartySnapshot snapshot, int[] hooked, bool hookAvailable,
        int fallbackLocalDamage, string readerSource, out string damageSource)
    {
        if (snapshot.HasAwardTable && snapshot.TotalDamage > 0)
        {
            damageSource = readerSource;
            // A readable, positive award table is one source for the whole
            // party, including measured zero slots. Hook rows have different
            // credit semantics; mixing them into zero slots can inflate a
            // hunter's total and then make it fall when awards become positive.
            return snapshot;
        }

        var solo = snapshot.Members.Length == 1;
        var members = snapshot.Members.Select(member =>
        {
            if (member.IsLocal && hookAvailable)
            {
                // The reader may already have filled a missing/empty roster's
                // local row with HP loss. Replace that estimate as well.
                var localDamage = member.Slot is >= 0 and < PartyDamageReader.PartySlots
                    ? hooked[member.Slot] : 0;
                return member.With(damage: localDamage);
            }

            var damage = member.Damage;
            if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                damage = Math.Max(damage, hooked[member.Slot]);
            if (member.IsLocal && solo)
                damage = Math.Max(damage, Math.Max(0, fallbackLocalDamage));
            return member.With(damage: damage);
        }).ToArray();

        var tableTotal = snapshot.Members.Sum(member => member.Damage);
        damageSource = members.Length == 0
            ? readerSource
            : hookAvailable && members.Any(member => member.IsLocal) ? "live hits"
            : tableTotal > 0 ? readerSource
            : hooked.Sum() > 0 ? "live hits" : solo ? "monster HP" : "party list";

        return PartySnapshot.From(members, hasAwardTable: false);
    }
}
