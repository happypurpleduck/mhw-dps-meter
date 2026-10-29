namespace MhwDpsMeter;

/// <summary>
/// A sampled local memory observation, NOT proof of authoritative host state.
/// World guests can have stale part/ailment meters; this reader never infers
/// attacker identity or part damage from a change in these values.
/// </summary>
internal sealed record MonsterGaugeState(
    nint Instance,
    string Name,
    float? Stamina,
    float? MaxStamina,
    MonsterPartGauge[] Parts,
    MonsterAilmentGauge[] Ailments);

internal readonly record struct MonsterPartGauge(
    int Part, string Name, float Remaining, float Max,
    int CompletedFlinches, int? NextBreakThreshold);

internal readonly record struct MonsterAilmentGauge(
    int Id, string Name, float Buildup, float MaxBuildup,
    int Procs, float Duration, float MaxDuration);

/// <summary>
/// Experimental read-only MHW 421810 snapshot reader based on HunterPie World
/// memory layouts. Caller MUST gate on confirmed build; offsets require live
/// confirmation before becoming enabled by default. All pointer reads are
/// protected against teardown and all walks are bounded.
/// See docs/monster-gauges.md.
/// </summary>
internal static class MonsterGaugeReader
{
    private const int StaminaOffset = 0x1C0F0;
    private const int PartControllerOffset = 0x1D058;
    private const int NormalPartBase = 0x40;
    private const int NormalPartStride = 0x1F8;
    private const int AilmentsOffset = 0x1BC40;
    private const int AilmentValueOffset = 0x148;
    private const int MaxAilments = 32;
    private const int MaxNormalParts = 16;

    private static readonly string[] StatusNames =
    [
        "Unknown 0", "Poison", "Paralysis", "Sleep", "Blast",
        "Mount", "Exhaust", "KO / Stun", "Tranquilize",
        "Flash 9", "Flash 10", "Knockdown", "Dung",
        "Unknown 13", "Shock trap", "Pitfall trap",
        "Vine trap", "Unknown 17", "Elderseal",
        "Smoking", "Unknown 20", "Felyne trap",
        "Claw", "Claw flinch", "Banishing ball"
    ];

    public static MonsterGaugeState[] Read(IReadOnlyList<MonsterState> monsters)
    {
        var result = new List<MonsterGaugeState>(monsters.Count);
        foreach (var monster in monsters)
        {
            if (!SafeMemory.LooksLikeUserPointer(monster.Instance))
                continue;

            var stamina = ReadStamina(monster.Instance);
            result.Add(new MonsterGaugeState(
                monster.Instance,
                monster.Name,
                stamina.Current,
                stamina.Max,
                ReadParts(monster.Instance, (int)monster.Type),
                ReadAilments(monster.Instance)));
        }
        return result.ToArray();
    }

    private static (float? Current, float? Max) ReadStamina(nint monster)
    {
        if (SafeMemory.TryReadProtected<float>(monster + StaminaOffset, out var current)
            && SafeMemory.TryReadProtected<float>(monster + StaminaOffset + 4, out var max)
            && ValidGauge(current, max))
            return (current, max);

        return (null, null);
    }

    private static MonsterPartGauge[] ReadParts(nint monster, int type)
    {
        if (!SafeMemory.TryReadProtected<nint>(monster + PartControllerOffset, out var controller)
            || !SafeMemory.LooksLikeUserPointer(controller))
            return [];

        var result = new List<MonsterPartGauge>();
        var parts = MonsterParts.NormalDefinitions(type);
        for (var i = 0; i < Math.Min(MaxNormalParts, parts.Count); i++)
        {
            var part = parts[i];
            var address = controller + NormalPartBase + i * NormalPartStride;
            if (!SafeMemory.TryReadProtected<float>(address + 0x0C, out var max)
                || !SafeMemory.TryReadProtected<float>(address + 0x10, out var remaining)
                || !SafeMemory.TryReadProtected<int>(address + 0x18, out var count)
                || !ValidGauge(remaining, max)
                || count is < 0 or > 100)
                continue;

            result.Add(new MonsterPartGauge(
                part.Id, part.Name, remaining, max, count,
                MonsterParts.NextBreakThreshold(type, part.Id, count)));
        }
        return result.ToArray();
    }

    private static MonsterAilmentGauge[] ReadAilments(nint monster)
    {
        var result = new List<MonsterAilmentGauge>();
        for (var i = 0; i < MaxAilments; i++)
        {
            if (!SafeMemory.TryReadProtected<nint>(monster + AilmentsOffset + i * 8, out var ptr)
                || !SafeMemory.LooksLikeUserPointer(ptr))
                break;

            var address = ptr + AilmentValueOffset;
            if (!SafeMemory.TryReadProtected<nint>(address, out var owner) || owner != monster)
                break;
            if (!SafeMemory.TryReadProtected<int>(address + 0x10, out var id)
                || id < 0 || id >= StatusNames.Length
                || !SafeMemory.TryReadProtected<float>(address + 0x30, out var buildup)
                || !SafeMemory.TryReadProtected<float>(address + 0x40, out var maximum)
                || !SafeMemory.TryReadProtected<float>(address + 0x70, out var duration)
                || !SafeMemory.TryReadProtected<float>(address + 0x14, out var maxDuration)
                || !SafeMemory.TryReadProtected<int>(address + 0x78, out var procs))
                continue;

            if (!ValidGauge(buildup, maximum)
                || !ValidDuration(duration, maxDuration)
                || procs is < 0 or > 1000)
                continue;

            result.Add(new MonsterAilmentGauge(
                id, StatusNames[id], buildup, maximum,
                procs, duration, maxDuration));
        }
        return result.ToArray();
    }

    private static bool ValidGauge(float value, float max) =>
        float.IsFinite(value) && float.IsFinite(max)
        && max > 0 && max <= 10_000_000
        && value >= 0 && value <= max + 0.01f;

    private static bool ValidDuration(float value, float max) =>
        float.IsFinite(value) && float.IsFinite(max)
        && max >= 0 && max <= 100_000
        && value >= 0 && value <= Math.Max(1f, max * 1.25f);
}
