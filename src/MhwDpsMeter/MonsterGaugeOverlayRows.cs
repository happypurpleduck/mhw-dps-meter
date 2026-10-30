namespace MhwDpsMeter;

/// <summary>A bounded, presentation-only view over already cached experimental
/// observations. No memory reader, timer, recorder or source-authority inference.</summary>
internal readonly record struct MonsterGaugeOverlayRow(string Text, bool Heading = false);

internal static class MonsterGaugeOverlayRows
{
    public const int MaxVisibleMonsters = 2;
    public const int MaxDetailAilments = 3;
    public const int MaxDetailParts = 2;

    public static MonsterGaugeOverlayRow[] Build(
        IReadOnlyList<MonsterGaugeState> gauges, bool showDetails)
    {
        var rows = new List<MonsterGaugeOverlayRow>();
        foreach (var gauge in gauges.Take(MaxVisibleMonsters))
        {
            // An absent reading stays unavailable; it is not a zero meter.
            var stamina = gauge.Stamina is float current && gauge.MaxStamina is float max
                && float.IsFinite(current) && float.IsFinite(max) && max > 0f
                ? $"{current:0}/{max:0}" : "unavailable";
            rows.Add(new MonsterGaugeOverlayRow(
                $"{gauge.Name} | stamina {stamina} | {gauge.Ailments.Length} sampled status, {gauge.Parts.Length} sampled part meters",
                Heading: true));

            if (!showDetails)
                continue;

            foreach (var ailment in gauge.Ailments
                .Where(a => a.MaxBuildup > 0f && float.IsFinite(a.Buildup)
                    && float.IsFinite(a.MaxBuildup)
                    && (a.Buildup > 0f || a.Duration > 0f || a.Procs > 0))
                // A positive recorded proc count is independent evidence even
                // when buildup and duration have already returned to zero.
                // Reserve space for such counter-only rows before selecting
                // the highest active buildup percentages under the three-row cap.
                .OrderByDescending(a => a.Procs > 0 && a.Buildup == 0f && a.Duration == 0f)
                .ThenByDescending(a => a.Buildup / a.MaxBuildup)
                .ThenBy(a => a.Id)
                .Take(MaxDetailAilments))
            {
                rows.Add(new MonsterGaugeOverlayRow(
                    $"  {ailment.Name}: buildup {ailment.Buildup:0}/{ailment.MaxBuildup:0}, sampled procs {ailment.Procs}"));
            }

            foreach (var part in gauge.Parts
                .Where(p => p.Max > 0f && float.IsFinite(p.Remaining)
                    && float.IsFinite(p.Max) && p.NextBreakThreshold is not null)
                .OrderBy(p => p.Remaining / p.Max)
                .ThenBy(p => p.Part)
                .Take(MaxDetailParts))
            {
                rows.Add(new MonsterGaugeOverlayRow(
                    $"  {part.Name}: flinch {part.Remaining:0}/{part.Max:0} remaining, sampled cycle {part.CompletedFlinches}/{part.NextBreakThreshold}"));
            }
        }

        if (gauges.Count > MaxVisibleMonsters)
            rows.Add(new MonsterGaugeOverlayRow(
                $"  +{gauges.Count - MaxVisibleMonsters} more sampled monster(s) not shown"));

        return rows.ToArray();
    }
}
