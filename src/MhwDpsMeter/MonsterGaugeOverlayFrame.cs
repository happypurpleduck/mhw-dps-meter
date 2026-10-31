namespace MhwDpsMeter;

/// <summary>Pure in-quest overlay content projection. Independently cached
/// monster gauges remain eligible when party acquisition is unavailable,
/// without bypassing the experimental capture/build/quest/freshness gate.</summary>
internal readonly record struct MonsterGaugeOverlayFrame(
    bool HasPartyRows, MonsterGaugeOverlayRow[] GaugeRows, string? GaugeHint)
{
    public static MonsterGaugeOverlayFrame Build(
        PartySnapshot? party, IReadOnlyList<MonsterGaugeState>? visibleGauges,
        MonsterGaugeOverlayAvailability availability, bool showDetails,
        MonsterGaugeOverlayCategories categories)
    {
        var hasPartyRows = party is { Members.Length: > 0 };
        if (availability.MayRenderGauges && visibleGauges is { Count: > 0 })
            return new(hasPartyRows,
                MonsterGaugeOverlayRows.Build(visibleGauges, showDetails, categories),
                null);

        var hint = availability.ShowStaleHudHint
            ? "Experimental gauges: cached sample stale (local, unverified)"
            : availability.ShowEmptyHudHint
                ? "Experimental gauges: no current samples (local, unverified)"
                : null;
        return new(hasPartyRows, [], hint);
    }
}
