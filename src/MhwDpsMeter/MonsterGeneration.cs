namespace MhwDpsMeter;

/// <summary>Conservative transient-pointer generation detection shared by the
/// recorder and gauge cache. Positive identification of reused native pointers
/// still needs real game tests (issue #17).</summary>
internal static class MonsterGeneration
{
    public static bool Changed(MonsterState prior, MonsterState current) =>
        prior.Instance != current.Instance
        || prior.Type != current.Type
        || prior.Variant != current.Variant
        || !string.Equals(prior.Name, current.Name, StringComparison.Ordinal)
        || Math.Abs(prior.MaxHealth - current.MaxHealth) > 0.01f
        || (prior.MaxHealth > 0
            && current.Health > 0.5f * current.MaxHealth
            && current.Health > prior.Health + 0.25f * prior.MaxHealth);
}
