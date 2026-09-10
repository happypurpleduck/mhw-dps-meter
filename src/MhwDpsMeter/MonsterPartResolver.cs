namespace MhwDpsMeter;

/// <summary>
/// Identifies which monster part a local hit landed on by reading the part-health
/// array HunterPie uses (monster + 0x1D058). Part "health" is the flinch/break meter,
/// not monster HP, so we look for which slot changed rather than matching damage.
/// </summary>
internal static class MonsterPartResolver
{
    private const int MonsterPartsOffset = 0x1D058;
    private const int NormalPartsBase = 0x40;
    private const int NormalPartsStride = 0x1F8;
    private const int SeverablePartsBase = 0x1FC8;
    private const int SeverablePartsStride = 0x78;
    private const int MaxHealthOffset = 0x0C;
    private const int HealthOffset = 0x10;
    private const int CounterOffset = 0x18;
    private const int IndexOffset = 0x6C;
    private const int MaxNormalParts = 32;
    private const int MaxSeverableParts = 32;
    private const float MinHealthDelta = 0.5f;

    /// <summary>One readable part slot before or after a damage call.</summary>
    internal readonly record struct PartSample(int Index, float Health, int Counter, nint Address, int Stride);

    /// <summary>Snapshot every part with MaxHealth &gt; 0. Empty when the array is unreadable.</summary>
    public static List<PartSample> Snapshot(nint monster)
    {
        var parts = new List<PartSample>(16);
        if (monster == 0 || !SafeMemory.TryRead<nint>(monster + MonsterPartsOffset, out var partPtr) || partPtr == 0)
            return parts;

        ReadRange(parts, partPtr + NormalPartsBase, NormalPartsStride, MaxNormalParts);
        ReadRange(parts, partPtr + SeverablePartsBase, SeverablePartsStride, MaxSeverableParts);
        return parts;
    }

    /// <summary>
    /// Prefer the part whose flinch/break health dropped (or counter rose) across the
    /// damage call. Fall back to matching <paramref name="position"/> to a part struct,
    /// or treating a small integer position as a part index.
    /// </summary>
    public static int? Resolve(nint monster, nint position, IReadOnlyList<PartSample>? before)
    {
        var after = Snapshot(monster);
        var fromDiff = ResolveFromDiff(before, after);
        if (fromDiff is not null)
            return fromDiff;

        return MatchPosition(before ?? after, position);
    }

    /// <summary>Pure diff used by the hook and by regression tests.</summary>
    internal static int? ResolveFromDiff(IReadOnlyList<PartSample>? before, IReadOnlyList<PartSample>? after)
    {
        if (before is null || after is null || before.Count == 0 || after.Count == 0)
            return null;

        var afterByAddress = after.ToDictionary(part => part.Address);
        int? best = null;
        var bestHealthDrop = MinHealthDelta;
        int? counterHit = null;

        foreach (var prior in before)
        {
            if (!afterByAddress.TryGetValue(prior.Address, out var next))
                continue;

            var drop = prior.Health - next.Health;
            if (drop > bestHealthDrop)
            {
                bestHealthDrop = drop;
                best = next.Index;
            }
            else if (counterHit is null && next.Counter > prior.Counter)
            {
                counterHit = next.Index;
            }
        }

        return best ?? counterHit;
    }

    internal static int? MatchPosition(IReadOnlyList<PartSample> parts, nint position)
    {
        if (position == 0)
            return null;

        // Some call sites may pass a bare part index in this slot.
        var asIndex = (ulong)position;
        if (asIndex <= 64)
            return parts.Any(part => part.Index == (int)asIndex) ? (int)asIndex : null;

        if (!SafeMemory.LooksLikeUserPointer(position))
            return null;

        foreach (var part in parts)
        {
            if (position >= part.Address && position < part.Address + part.Stride)
                return part.Index;
        }

        return null;
    }

    private static void ReadRange(List<PartSample> parts, nint start, int stride, int limit)
    {
        for (var i = 0; i < limit; i++)
        {
            var address = start + i * stride;
            if (!SafeMemory.TryRead<float>(address + MaxHealthOffset, out var maxHealth) || maxHealth <= 0f)
                continue;
            if (!SafeMemory.TryRead<float>(address + HealthOffset, out var health))
                continue;
            if (!SafeMemory.TryRead<int>(address + CounterOffset, out var counter))
                counter = 0;
            if (!SafeMemory.TryRead<uint>(address + IndexOffset, out var index) || index > 64)
                continue;
            if (float.IsNaN(health) || float.IsNaN(maxHealth))
                continue;

            parts.Add(new PartSample((int)index, health, counter, address, stride));
        }
    }
}
