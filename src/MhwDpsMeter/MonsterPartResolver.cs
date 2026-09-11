namespace MhwDpsMeter;

/// <summary>
/// Reads the collision part from the hit context passed to FUN_SHOW_DAMAGE_CONTEXT.
/// FUN_DEAL_DAMAGE only displays a number: part meters do not change inside it and
/// its position argument is a Vector3, not a part pointer. See docs/part-damage.md.
/// </summary>
internal static class MonsterPartResolver
{
    private const int MaxNormalParts = 16;
    private const int NormalPartsStride = 0x1F8;

    internal readonly record struct HitContext(nint Target, nint Position, int? Part, string Status)
    {
        // Both must match: unrelated/nested damage numbers cannot inherit a part.
        public int? Match(nint target, nint position) =>
            Target == target && Position == position ? Part : null;
    }

    public static HitContext Capture(nint controller, nint hitData)
    {
        nint target = 0;
        var position = hitData + 0xE0;
        HitContext Unknown(string reason) => new(target, position, null, reason);

        if (!SafeMemory.LooksLikeUserPointer(controller) || !SafeMemory.LooksLikeUserPointer(hitData)
            || !SafeMemory.TryRead<nint>(controller + 0x08, out target)
            || !SafeMemory.LooksLikeUserPointer(target))
            return Unknown("unreadable target");

        if (!SafeMemory.TryRead<nint>(hitData + 0x28, out var collision)
            || !SafeMemory.LooksLikeUserPointer(collision)
            || !SafeMemory.TryRead<int>(collision + 0x60, out var slot)
            || slot is < 0 or >= MaxNormalParts)
            return Unknown("unreadable collision part");

        // Mirrors the selector in the game's damage application (421810:
        // 0x1402C66DE..0x1402C671E), including the alternate part after a break.
        if (!SafeMemory.TryRead<int>(controller + slot * NormalPartsStride + 0x208, out var state))
            return Unknown("unreadable part state");
        if (state == 1 && (!SafeMemory.TryRead<int>(collision + 0x80, out slot)
            || slot is < 0 or >= MaxNormalParts))
            return Unknown("unreadable alternate part");

        if (!SafeMemory.TryRead<int>(target + 0x12280, out var monsterType))
            return Unknown("unreadable monster type");

        // HunterPie ids also include severable meters. The collision index is a
        // normal-array ordinal, so e.g. Anjanath slot 0 maps to Head (id 2).
        var part = MonsterParts.FromNormalSlot(monsterType, slot);
        return new(target, position, part,
            part is null ? $"unmapped type={monsterType} slot={slot}" : $"collision slot={slot} part={part}");
    }
}
