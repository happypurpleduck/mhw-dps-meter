namespace MhwDpsMeter;

/// <summary>Structured F9 diagnostic sections around the owned gauge-status row.
/// The caller supplies these two sections, so unrelated multiline fields cannot
/// impersonate a gauge label or be rewritten by an in-frame capture toggle.</summary>
internal readonly record struct MonsterGaugeOverlayDiagnostics(
    string BeforeGauge, string AfterGauge)
{
    /// <summary>Compose the exact verbose diagnostics with the current F9
    /// capture availability, after the checkbox has been processed.</summary>
    public string Render(MonsterGaugeOverlayAvailability availability) =>
        BeforeGauge + "Monster gauges: " + availability.StatusText + "\n" + AfterGauge;
}
