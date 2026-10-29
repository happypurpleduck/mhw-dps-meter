namespace MhwDpsMeter;

/// <summary>
/// Shared quest-poll integration seam: observe monsters and optional gauges
/// BEFORE callers check whether the party table is readable. This preserves
/// independent monster history when a transient party snapshot is unavailable.
/// </summary>
internal static class HuntGaugeObservation
{
    public static PartySnapshot? CaptureBeforePartyGuard(
        PartySnapshot? partySnapshot, float huntElapsed, double monotonicSeconds,
        bool enabled, bool monsterEnumerationComplete,
        IReadOnlyList<MonsterState> monsters, MonsterGaugePoller poller,
        HuntRecorder recorder,
        Func<IReadOnlyList<MonsterState>, MonsterGaugeState[]> reader)
    {
        if (monsterEnumerationComplete)
            recorder.ObserveMonsters(huntElapsed, monsters);

        var sampled = poller.Tick(monotonicSeconds, enabled, monsterEnumerationComplete,
            monsters, reader);
        if (sampled is { Length: > 0 })
            recorder.ObserveGauges(huntElapsed, monotonicSeconds, sampled);

        return partySnapshot;
    }
}
