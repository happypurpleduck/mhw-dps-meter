namespace MhwDpsMeter;

/// <summary>Conservative native-pointer generation detection shared by the
/// HP fallback, recorder and gauge cache. Iceborne party changes can rescale
/// both max HP and current HP without creating a new monster. Runtime
/// verification of ambiguous pointer reuse is still required (#17).</summary>
internal static class MonsterGeneration
{
    public static bool Changed(MonsterState prior, MonsterState current) =>
        prior.Instance != current.Instance
        || prior.Type != current.Type
        || prior.Variant != current.Variant
        || !string.Equals(prior.Name, current.Name, StringComparison.Ordinal)
        // Compare health as a fraction of current maximum, not absolute HP.
        // Dynamic player-count scaling must not look like a respawn.
        || (prior.MaxHealth > 0 && current.MaxHealth > 0
            && current.Health / current.MaxHealth > 0.5f
            && current.Health / current.MaxHealth
                > prior.Health / prior.MaxHealth + 0.25f);

    /// <summary>Same observed monster identity, but a changed HP scale.
    /// Damage on the very transition tick cannot be separated reliably from
    /// rescaling with client-side HP snapshots alone.</summary>
    public static bool Rescaled(MonsterState prior, MonsterState current) =>
        prior.Instance == current.Instance
        && prior.Type == current.Type
        && prior.Variant == current.Variant
        && string.Equals(prior.Name, current.Name, StringComparison.Ordinal)
        && Math.Abs(prior.MaxHealth - current.MaxHealth) > 0.01f
        && !Changed(prior, current);
}
