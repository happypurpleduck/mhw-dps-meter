namespace MhwDpsMeter;

/// <summary>
/// Shared quest-poll integration seam: observe monsters and optional gauges
/// BEFORE callers check whether the party table is readable. This preserves
/// independent monster history when a transient party snapshot is unavailable.
/// </summary>
internal static class HuntGaugeObservation
{
    public static PartySnapshot? CaptureBeforePartyGuard(
        Func<PartySnapshot?> readParty, float huntElapsed, double monotonicSeconds,
        string huntClockSource, bool enabled, bool monsterEnumerationComplete,
        IReadOnlyList<MonsterState> monsters, MonsterGaugePoller poller,
        HuntRecorder recorder,
        Func<IReadOnlyList<MonsterState>, MonsterGaugeState[]> reader,
        Action<Exception>? partyReadFailure = null)
    {
        PartySnapshot? snapshot;
        try
        {
            snapshot = readParty();
        }
        catch (Exception ex)
        {
            partyReadFailure?.Invoke(ex);
            snapshot = null;
        }

        // Even a throwing party-table read cannot skip the independent
        // monster observation. The monster enumeration must have succeeded.
        if (monsterEnumerationComplete)
            recorder.ObserveMonsters(huntElapsed, monsters);

        var sampled = poller.Tick(monotonicSeconds, enabled, monsterEnumerationComplete,
            monsters, reader);
        if (sampled is { Length: > 0 })
            recorder.ObserveGauges(huntElapsed, monotonicSeconds, huntClockSource, sampled);

        return snapshot;
    }
}
