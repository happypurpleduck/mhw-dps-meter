namespace MhwDpsMeter;

/// <summary>A pure projection of the actual F9 button identity and visible
/// labels. UI IDs track cached entity identity plus the poller generation,
/// never the row index, even when samples reorder mid-click.</summary>
internal readonly record struct MonsterGaugeOverlayMenuChoice(
    MonsterGaugeState Sample, string ControlId, string Label, bool Selected)
{
    // Dear ImGui hashes the full label after ## but only the suffix after
    // ###. Keep the full widget identity stable when visible text changes.
    // F9 calls SmallButton(ButtonLabel) inside PushID(ControlId).
    public string ButtonLabel => Selected ? "Selected###pick" : "Show###pick";
}

internal readonly record struct MonsterGaugeOverlayMenu(
    MonsterGaugeOverlayMenuChoice[] Choices, int MoreCount);

internal static class MonsterGaugeOverlayMenuChoices
{
    public static MonsterGaugeOverlayMenu Build(
        IReadOnlyList<MonsterGaugeState> current, long generation,
        MonsterGaugeOverlaySelection selection)
    {
        // The menu is display-only. Duplicate cached instance/name records
        // describe the same selectable identity and must not reuse ImGui IDs.
        var unique = current
            .DistinctBy(g => (g.Instance, g.Name))
            .ToArray();
        var shown = unique.Take(MonsterGaugeOverlaySelection.MaxMenuChoices).ToArray();
        var choices = new MonsterGaugeOverlayMenuChoice[shown.Length];
        // Segment each Unicode label once rather than once per collision check.
        // The F9 selector is bounded to eight visible choices.
        var displayNames = shown.Select(g => MonsterGaugeOverlayLabels.Display(g.Name))
            .ToArray();

        for (var i = 0; i < shown.Length; i++)
        {
            var gauge = shown[i];
            var displayName = displayNames[i];
            // Collisions are ranked by complete (instance, full name)
            // identity; this includes two different names at one pointer.
            // Compare preformatted labels, avoiding repeated grapheme scans
            // and a sorted matching-choices allocation for each row.
            var collisionCount = 0;
            var ordinal = 1;
            for (var j = 0; j < shown.Length; j++)
            {
                if (!string.Equals(displayNames[j], displayName, StringComparison.Ordinal))
                    continue;

                collisionCount++;
                var instanceOrder = shown[j].Instance.ToInt64()
                    .CompareTo(gauge.Instance.ToInt64());
                if (instanceOrder < 0
                    || (instanceOrder == 0
                        && string.CompareOrdinal(shown[j].Name, gauge.Name) < 0))
                    ordinal++;
            }
            var label = collisionCount > 1
                ? $"{displayName} (sample #{ordinal})"
                : displayName;

            // Names can contain separators and change independently of a
            // pointer. Use a length prefix and a full pointer/generation key.
            // This ID is never saved, transmitted or displayed to the player.
            var identity = $"gauge:{generation}:{gauge.Instance.ToInt64():X16}:{gauge.Name.Length}:{gauge.Name}";
            choices[i] = new MonsterGaugeOverlayMenuChoice(
                gauge, identity, label, selection.Matches(gauge));
        }

        return new MonsterGaugeOverlayMenu(choices, unique.Length - shown.Length);
    }
}
