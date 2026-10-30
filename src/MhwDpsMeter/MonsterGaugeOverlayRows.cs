namespace MhwDpsMeter;

/// <summary>A bounded, presentation-only view over already cached experimental
/// observations. No memory reader, timer, recorder or source-authority inference.</summary>
internal readonly record struct MonsterGaugeOverlayRow(string Text, bool Heading = false);

/// <summary>Pure rendering preferences; independent of native gauge capture.</summary>
internal readonly record struct MonsterGaugeOverlayCategories(bool Stamina, bool Ailments, bool Parts)
{
    public static MonsterGaugeOverlayCategories All => new(true, true, true);
}

internal static class MonsterGaugeOverlayRows
{
    public const int MaxVisibleMonsters = 2;
    public const int MaxDetailAilments = 3;
    public const int MaxDetailParts = 2;

    public static MonsterGaugeOverlayRow[] Build(
        IReadOnlyList<MonsterGaugeState> gauges, bool showDetails) =>
        Build(gauges, showDetails, MonsterGaugeOverlayCategories.All);

    public static MonsterGaugeOverlayRow[] Build(
        IReadOnlyList<MonsterGaugeState> gauges, bool showDetails,
        MonsterGaugeOverlayCategories categories)
    {
        var rows = new List<MonsterGaugeOverlayRow>();
        foreach (var gauge in gauges.Take(MaxVisibleMonsters))
        {
            // Do not claim that unselected categories are zero or absent.
            // Compact rows contain only requested cached fields.
            var summary = new List<string> { MonsterGaugeOverlayLabels.Display(gauge.Name) };
            if (categories.Stamina)
            {
                var stamina = gauge.Stamina is float current && gauge.MaxStamina is float max
                    && float.IsFinite(current) && float.IsFinite(max) && max > 0f
                    ? $"{current:0}/{max:0}" : "unavailable";
                summary.Add($"stamina {stamina}");
            }
            if (categories.Ailments)
                summary.Add($"{gauge.Ailments.Length} sampled status");
            if (categories.Parts)
                summary.Add($"{gauge.Parts.Length} sampled part meters");
            if (!categories.Stamina && !categories.Ailments && !categories.Parts)
                summary.Add("no sampled gauge categories selected");
            rows.Add(new MonsterGaugeOverlayRow(string.Join(" | ", summary), Heading: true));

            if (!showDetails)
                continue;

            if (categories.Ailments)
            {
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
                        $"  {MonsterGaugeOverlayLabels.Display(ailment.Name)}: buildup {ailment.Buildup:0}/{ailment.MaxBuildup:0}, sampled procs {ailment.Procs}"));
                }
            }

            if (categories.Parts)
            {
                foreach (var part in gauge.Parts
                .Where(p => p.Max > 0f && float.IsFinite(p.Remaining)
                    && float.IsFinite(p.Max) && p.NextBreakThreshold is not null)
                .OrderBy(p => p.Remaining / p.Max)
                .ThenBy(p => p.Part)
                .Take(MaxDetailParts))
                {
                    rows.Add(new MonsterGaugeOverlayRow(
                        $"  {MonsterGaugeOverlayLabels.Display(part.Name)}: flinch {part.Remaining:0}/{part.Max:0} remaining, sampled cycle {part.CompletedFlinches}/{part.NextBreakThreshold}"));
                }
            }
        }

        if (gauges.Count > MaxVisibleMonsters)
            rows.Add(new MonsterGaugeOverlayRow(
                $"  +{gauges.Count - MaxVisibleMonsters} more sampled monster(s) not shown"));

        return rows.ToArray();
    }
}
