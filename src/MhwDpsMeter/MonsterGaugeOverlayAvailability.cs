namespace MhwDpsMeter;

/// <summary>Only explains the current experimental capture/display gate.
/// These are local cached samples, never verified host-state or gameplay
/// events. No additional native reads or timing assumptions.</summary>
internal enum MonsterGaugeAvailabilityKind
{
    Disabled,
    UnsupportedBuild,
    OutsideQuest,
    NoCurrentSamples,
    StaleSamples,
    LocalUnverified
}

internal readonly record struct MonsterGaugeOverlayAvailability(
    bool CaptureEnabled, bool BuildReady, bool InQuest, int CachedMonsterCount,
    double? SampleAgeSeconds = null)
{
    // Allow missed nominal 1 s sample cycles without displaying indefinitely
    // stale observations after an update/render stall. #17 must assess this
    // conservative UI-only threshold under real game frame-time conditions.
    public const double MaxDisplayAgeSeconds = 2.5;

    public MonsterGaugeAvailabilityKind Kind =>
        !CaptureEnabled ? MonsterGaugeAvailabilityKind.Disabled :
        !BuildReady ? MonsterGaugeAvailabilityKind.UnsupportedBuild :
        !InQuest ? MonsterGaugeAvailabilityKind.OutsideQuest :
        CachedMonsterCount <= 0 ? MonsterGaugeAvailabilityKind.NoCurrentSamples :
        SampleAgeSeconds is not double age || !double.IsFinite(age)
            || age < 0 || age > MaxDisplayAgeSeconds
            ? MonsterGaugeAvailabilityKind.StaleSamples :
        MonsterGaugeAvailabilityKind.LocalUnverified;

    public string StatusText => Kind switch
    {
        MonsterGaugeAvailabilityKind.Disabled =>
            "Off: experimental native gauge capture disabled",
        MonsterGaugeAvailabilityKind.UnsupportedBuild =>
            "Unavailable: confirmed MHW 421810 and matching map required",
        MonsterGaugeAvailabilityKind.OutsideQuest =>
            "No active quest: no current in-quest gauge samples",
        MonsterGaugeAvailabilityKind.NoCurrentSamples =>
            "Enabled: no current monster-gauge samples (not a zero reading)",
        MonsterGaugeAvailabilityKind.StaleSamples =>
            "Stale: cached monster-gauge samples hidden until a new local observation",
        MonsterGaugeAvailabilityKind.LocalUnverified =>
            $"Local-unverified: {CachedMonsterCount} sampled monster(s), NOT verified host state",
        _ => "Gauge availability unknown"
    };

    // Only show an extra HUD hint when experimental capture is actually
    // enabled, supported and in a quest. Avoid clutter in default/off mode.
    public bool ShowEmptyHudHint => Kind == MonsterGaugeAvailabilityKind.NoCurrentSamples;
    public bool ShowStaleHudHint => Kind == MonsterGaugeAvailabilityKind.StaleSamples;
    public bool MayRenderGauges => Kind == MonsterGaugeAvailabilityKind.LocalUnverified;
}
