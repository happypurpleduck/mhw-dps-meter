using System.Diagnostics;
using SharpPluginLoader.Core.Entities;

namespace MhwDpsMeter;

internal enum TimeTrialState
{
    Idle,
    /// <summary>Armed: the clock starts on the first hit the hook counts.</summary>
    Armed,
    Running,
    /// <summary>Deadline passed; numbers are frozen until re-armed or cleared.</summary>
    Finished
}

/// <summary>One move's share of a finished trial.</summary>
internal sealed record TimeTrialMove(string Name, int Damage, int Hits, int Crits);

/// <summary>
/// Training-area time trial: fixed window of damage, timed from the first hit after
/// arming. Hits are stamped by the hook, so the window is measured on hit timestamps
/// and a hit that lands after the deadline is not counted even if the poll that
/// drains it runs late.
/// </summary>
internal sealed class TimeTrial
{
    public const int MinDurationSeconds = 5;
    public const int MaxDurationSeconds = 600;
    public const int DefaultDurationSeconds = 60;
    public static readonly int[] PresetDurations = [30, 60, 90, 120, 180];

    private readonly HuntRecorder _recorder;
    private long _startTimestamp;
    private long _cutoffTimestamp;

    public TimeTrial(Func<int, int, string?> resolveActionName)
    {
        _recorder = new HuntRecorder(resolveActionName);
    }

    public TimeTrialState State { get; private set; }
    public int DurationSeconds { get; private set; } = DefaultDurationSeconds;
    public int Damage { get; private set; }
    public int Hits { get; private set; }
    public int Crits { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public string? Weapon => _recorder.LocalWeapon;

    /// <summary>Set by the plugin after the trial finishes, from the saved history.</summary>
    public int? PreviousBest { get; set; }
    public bool IsPersonalBest { get; set; }
    public string? SavedFileName { get; set; }

    public bool Active => State != TimeTrialState.Idle;

    /// <summary>Seconds of the window used so far; the full duration once finished.</summary>
    public float Elapsed => State switch
    {
        TimeTrialState.Running => Math.Min((float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds, DurationSeconds),
        TimeTrialState.Finished => DurationSeconds,
        _ => 0f
    };

    public float Remaining => Math.Max(0f, DurationSeconds - Elapsed);

    /// <summary>Damage over the time used so far (running), or over the full window (finished). Floored at 1 s so the first hit does not read as thousands of DPS.</summary>
    public float Dps => State is TimeTrialState.Running or TimeTrialState.Finished ? Damage / Math.Max(Elapsed, 1f) : 0f;

    public void Arm(int durationSeconds)
    {
        Clear();
        DurationSeconds = Math.Clamp(durationSeconds, MinDurationSeconds, MaxDurationSeconds);
        State = TimeTrialState.Armed;
    }

    public void Cancel() => Clear();

    /// <summary>Feeds the hits drained since the last poll. Returns true on the poll that finishes the trial.</summary>
    public bool Update(IReadOnlyList<HitRecord> hits, WeaponType? weapon)
    {
        if (State is TimeTrialState.Idle or TimeTrialState.Finished)
            return false;

        var now = Stopwatch.GetTimestamp();
        foreach (var hit in hits)
        {
            if (State == TimeTrialState.Armed)
            {
                _startTimestamp = hit.Timestamp;
                _cutoffTimestamp = _startTimestamp + (long)(DurationSeconds * (double)Stopwatch.Frequency);
                StartedAt = DateTimeOffset.Now - Stopwatch.GetElapsedTime(hit.Timestamp, now);
                State = TimeTrialState.Running;
                _recorder.ObserveWeapon(0f, weapon, 0);
            }

            if (hit.Timestamp > _cutoffTimestamp)
                continue;

            Damage += hit.Damage;
            Hits++;
            if (hit.Crit)
                Crits++;
            _recorder.AddHitsRelativeTo(_startTimestamp, [hit], 0);
        }

        if (State == TimeTrialState.Running)
        {
            _recorder.ObserveWeapon(Elapsed, weapon, 0);
            if (now >= _cutoffTimestamp)
            {
                State = TimeTrialState.Finished;
                return true;
            }
        }

        return false;
    }

    /// <summary>Damage per move, biggest first. Uses the action name; falls back to the raw action ids.</summary>
    public IReadOnlyList<TimeTrialMove> TopMoves(int count)
    {
        return _recorder.Hits()
            .GroupBy(hit => hit.Action ?? $"action {hit.ActionSet}/{hit.ActionId}")
            .Select(group => new TimeTrialMove(
                group.Key,
                group.Sum(hit => hit.Damage),
                group.Count(),
                group.Count(hit => hit.Crit)))
            .OrderByDescending(move => move.Damage)
            .Take(count)
            .ToArray();
    }

    /// <summary>"WP_02::RANBU" reads as "RANBU" in the overlay; the full name stays in the log.</summary>
    public static string ShortMoveName(string name)
    {
        var separator = name.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? name[(separator + 2)..] : name;
    }

    /// <summary>Same file format as a quest log so the web viewer needs no special case.</summary>
    public FightLog BuildLog(string hunterName, int stageId, string stageName, int gameBuild)
    {
        var hits = _recorder.Hits();
        var samples = new List<FightLogSample> { new() { T = 0 } };
        var cumulative = 0;
        var next = 0;
        for (var t = FightLogStore.SampleIntervalSeconds; t <= DurationSeconds + 0.001f; t += FightLogStore.SampleIntervalSeconds)
            samples.Add(SampleAt(t));
        if (samples[^1].T < DurationSeconds - 0.001f)
            samples.Add(SampleAt(DurationSeconds));

        FightLogSample SampleAt(float t)
        {
            while (next < hits.Length && hits[next].T <= t)
                cumulative += hits[next++].Damage;
            return new FightLogSample { T = t, Damage = [cumulative, 0, 0, 0] };
        }

        return new FightLog
        {
            SchemaVersion = FightLog.CurrentSchemaVersion,
            PluginVersion = Plugin.BuildStamp,
            GameBuild = gameBuild,
            Kind = FightLog.KindTrial,
            QuestId = 0,
            QuestName = $"Time trial {DurationSeconds}s",
            Result = "trial",
            StageId = stageId,
            Stage = stageName,
            StartedAt = StartedAt.ToUniversalTime(),
            EndedAt = StartedAt.ToUniversalTime().AddSeconds(DurationSeconds),
            DurationSeconds = DurationSeconds,
            TimerSource = "trial",
            HitCoverage = "local",
            Players =
            [
                new FightLogPlayer
                {
                    Slot = 0,
                    Name = hunterName,
                    IsLocal = true,
                    Weapon = Weapon,
                    Damage = Damage,
                    Dps = Damage / (float)DurationSeconds,
                    Percent = 100f
                }
            ],
            Samples = samples.ToArray(),
            Hits = hits,
            Events = _recorder.Events()
        };
    }

    private void Clear()
    {
        State = TimeTrialState.Idle;
        Damage = 0;
        Hits = 0;
        Crits = 0;
        PreviousBest = null;
        IsPersonalBest = false;
        SavedFileName = null;
        _startTimestamp = 0;
        _cutoffTimestamp = 0;
        _recorder.Reset();
    }
}
