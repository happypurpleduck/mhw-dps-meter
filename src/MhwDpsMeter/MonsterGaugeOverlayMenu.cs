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

        for (var i = 0; i < shown.Length; i++)
        {
            var gauge = shown[i];
            var displayName = MonsterGaugeOverlayLabels.Display(gauge.Name);
            // Distinct (instance, full name) records may shorten to the
            // same visible label, including two names at the same pointer.
            // Rank complete identities, not just distinct addresses.
            var matchingChoices = shown
                .Where(other => string.Equals(
                    MonsterGaugeOverlayLabels.Display(other.Name),
                    displayName, StringComparison.Ordinal))
                .OrderBy(other => other.Instance.ToInt64())
                .ThenBy(other => other.Name, StringComparer.Ordinal)
                .ToArray();
            var ordinal = Array.FindIndex(matchingChoices, other =>
                other.Instance == gauge.Instance
                && string.Equals(other.Name, gauge.Name, StringComparison.Ordinal)) + 1;
            var label = matchingChoices.Length > 1
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
