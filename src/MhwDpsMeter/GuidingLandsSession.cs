namespace MhwDpsMeter;

/// <summary>Read-only expedition policy. Stage 109 is The Guiding Lands;
/// its open-ended hunt lacks quest-award damage totals. Only local hook
/// readings may be attributed to a hunter, in solo or multiplayer.</summary>
internal static class GuidingLandsSession
{
    public const int StageId = 109;

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
