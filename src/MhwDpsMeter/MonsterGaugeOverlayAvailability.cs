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
    LocalUnverified
}

internal readonly record struct MonsterGaugeOverlayAvailability(
    bool CaptureEnabled, bool BuildReady, bool InQuest, int CachedMonsterCount)
{
    public MonsterGaugeAvailabilityKind Kind =>
        !CaptureEnabled ? MonsterGaugeAvailabilityKind.Disabled :
        !BuildReady ? MonsterGaugeAvailabilityKind.UnsupportedBuild :
        !InQuest ? MonsterGaugeAvailabilityKind.OutsideQuest :
        CachedMonsterCount <= 0 ? MonsterGaugeAvailabilityKind.NoCurrentSamples :
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
        MonsterGaugeAvailabilityKind.LocalUnverified =>
            $"Local-unverified: {CachedMonsterCount} sampled monster(s), NOT verified host state",
        _ => "Gauge availability unknown"
    };

    // Only show an extra HUD hint when experimental capture is actually
    // enabled, supported and in a quest. Avoid clutter in default/off mode.
    public bool ShowEmptyHudHint => Kind == MonsterGaugeAvailabilityKind.NoCurrentSamples;
    public bool MayRenderGauges => Kind == MonsterGaugeAvailabilityKind.LocalUnverified;
}
