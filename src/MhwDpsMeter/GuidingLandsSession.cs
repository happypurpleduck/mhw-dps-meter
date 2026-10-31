namespace MhwDpsMeter;

internal enum GuidingLandsTransition
{
    None,
    Begin,
    Continue,
    Leave
}

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

    // At session termination the current stage is often already Seliana or
    // another destination. Never write the destination into an expedition
    // record: stage 109 is the source session and the log acceptance gate.
    public static int LogStageId(bool localOnlyExpedition, int currentStage) =>
        localOnlyExpedition ? StageId : currentStage;

    public static bool CanStart(int stage, nint mainPlayer) =>
        IsStage(stage) && mainPlayer != 0;

    /// <summary>Stage-only transition policy. A long expedition has no
    /// conventional quest completion state; monster/party transitions and
    /// brief missing player pointers must not create duplicate sessions.</summary>
    public static GuidingLandsTransition Decide(
        int stage, nint mainPlayer, bool alreadyActive)
    {
        if (IsStage(stage))
            return alreadyActive ? GuidingLandsTransition.Continue
                : CanStart(stage, mainPlayer) ? GuidingLandsTransition.Begin
                : GuidingLandsTransition.None;

        return alreadyActive ? GuidingLandsTransition.Leave
            : GuidingLandsTransition.None;
    }

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
        // Preserve a guest's actual roster slot for hit/event/carts and
        // slot-indexed samples. Zero remains only the no-roster fallback.
        return PartySnapshot.LocalOnly(localHookDamage, name!, mainPlayer,
            identity?.Slot ?? 0);
    }
}
