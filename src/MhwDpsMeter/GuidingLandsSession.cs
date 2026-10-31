namespace MhwDpsMeter;

/// <summary>Read-only expedition policy. Stage 109 is The Guiding Lands;
/// its open-ended hunt lacks quest-award damage totals. Only local hook
/// readings may be attributed to a hunter, in solo or multiplayer.</summary>
internal static class GuidingLandsSession
{
    public const int StageId = 109;
    public const string DamageScope = "local-only";

    // Ordinary quest logs still require a positive quest, players and damage.
    // The stage-109 exception allows observed local damage or valid gauges,
    // even if the hook is absent; a completely empty session is never saved.
    public static bool ShouldPersistLog(
        int questId, int stageId, bool localOnlyExpedition,
        int playerCount, int totalDamage, int gaugeSampleCount)
    {
        if (localOnlyExpedition && IsStage(stageId))
            return (playerCount > 0 && totalDamage > 0) || gaugeSampleCount > 0;
        if (localOnlyExpedition || questId <= 0)
            return false;
        return playerCount > 0 && totalDamage > 0;
    }
    public const string HudProvenance =
        "Guiding Lands: local hook damage only; teammate totals unavailable";

    // Percent in a local-only PartySnapshot describes a single known
    // hunter's fraction of *observed* damage, not the multiplayer party.
    // Never expose that internal 100% as a synchronized party share.
    public static string DisplayShare(bool guidingLands, float percent) =>
        guidingLands ? "n/a" : $"{percent:0.0}%";

    public static bool IsStage(int stage) => stage == StageId;

    public static bool CanStart(int stage, nint mainPlayer) =>
        IsStage(stage) && mainPlayer != 0;

    /// <summary>Never reuse quest-award data, remote players' zeros or
    /// whole-monster HP loss as individual hunter damage in this area.
    /// A missing hook or local entity means no reliable local snapshot.</summary>
    public static PartySnapshot? LocalSnapshot(
        PartySnapshot? roster, nint mainPlayer, string? saveName,
        int localHookDamage, bool hookAvailable)
    {
        if (!hookAvailable || mainPlayer == 0)
            return null;

        var identity = roster?.Members.FirstOrDefault(member =>
            member.IsLocal && member.Instance == mainPlayer);
        var name = identity?.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(saveName) ? "You" : saveName;

        // Present only the local player's observed hook damage, not fake
        // remote 0-DPS rows or potentially stale quest award totals.
        return PartySnapshot.LocalOnly(localHookDamage, name!, mainPlayer);
    }
}
