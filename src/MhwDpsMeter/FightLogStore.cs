using System.Text.Json;
using SharpPluginLoader.Core;

namespace MhwDpsMeter;

/// <summary>Quest facts known to the plugin at hunt end; the store adds players, monsters, hits, and events.</summary>
internal sealed record FightLogHeader(
    int QuestId,
    string QuestName,
    string Result,
    int StageId,
    string StageName,
    DateTimeOffset StartedAt,
    float DurationSeconds,
    string TimerSource,
    int GameBuild,
    FightLogRewards? Rewards = null,
    bool LocalOnlyExpedition = false);

/// <summary>
/// Writes one JSON file per hunt to <c>logs/</c>, keeps <c>logs/index.json</c> as a
/// lightweight listing for external viewers, and holds the recent history shown in
/// the F9 panel. Every write failure disables further writes for this session so a
/// read-only install never spams the console.
/// </summary>
internal sealed class FightLogStore
{
    public const int MaxSamples = 900;
    public const int HistoryLimit = 20;
    public const float SampleIntervalSeconds = 2f;
    public const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _logsDirectory;
    private readonly List<FightLogSample> _samples = [];
    private float _nextDamageSampleT = SampleIntervalSeconds;
    private float _damageSampleInterval = SampleIntervalSeconds;
    private int _damageSamplesCompactions;
    private readonly List<FightLog> _history = [];
    private List<FightLogIndexEntry> _index = [];
    private bool _logsAvailable = true;

    public IReadOnlyList<FightLog> History => _history;
    public int? ExpandedHistoryIndex { get; set; }
    public string LogsDirectory => _logsDirectory;

    /// <summary>Every saved hunt and trial, newest first (mirrors index.json).</summary>
    public IReadOnlyList<FightLogIndexEntry> Index => _index;

    public FightLogStore(string pluginDirectory)
    {
        _logsDirectory = Path.Combine(pluginDirectory, "logs");
        TryEnsureLogDirectory();
        LoadHistory();
        _index = ReadIndex();
        // Missing, or written by 0.4.0 before totalDamage/kind existed (a logged hunt never has 0 damage).
        // Genuine gauge-only Guiding Lands logs may have zero hit damage.
        // Do not rebuild index.json on every startup for those entries.
        if (_index.Count == 0 || _index.Any(entry =>
            entry.TotalDamage == 0 && entry.QuestId > 0))
            BuildIndexFromFiles();
    }

    /// <summary>Highest-damage saved trial for this weapon and duration, if any.</summary>
    public FightLogIndexEntry? BestTrial(string? weapon, int durationSeconds) =>
        Trials()
            .Where(entry => Math.Abs(entry.DurationSeconds - durationSeconds) < 0.5f
                            && string.Equals(entry.Weapon, weapon, StringComparison.Ordinal))
            .MaxBy(entry => entry.TotalDamage);

    public IEnumerable<FightLogIndexEntry> Trials() =>
        _index.Where(entry => entry.Kind == FightLog.KindTrial);

    public void BeginHunt()
    {
        _samples.Clear();
        _damageSampleInterval = SampleIntervalSeconds;
        _nextDamageSampleT = SampleIntervalSeconds;
        _damageSamplesCompactions = 0;
        ExpandedHistoryIndex = null;
        _samples.Add(new FightLogSample { T = 0 });
    }

    /// <summary>
    /// Bounded, full-session damage history. At capacity keep every second
    /// observation and double the future interval (2s -> 4s -> 8s ...),
    /// preserving t=0 and the newest session tail. SOS clock jumps can still
    /// catch up without truncating the rest of a Guiding Lands expedition.
    /// </summary>
    public void UpdateSamples(float elapsedSeconds, int[] slotDamage)
    {
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            return;

        while (elapsedSeconds + 0.05f >= _nextDamageSampleT)
        {
            if (_samples.Count >= MaxSamples)
                CompactDamageSamples();

            _samples.Add(new FightLogSample
            {
                T = _nextDamageSampleT,
                Damage = (int[])slotDamage.Clone()
            });
            _nextDamageSampleT += _damageSampleInterval;
        }
    }

    private void CompactDamageSamples()
    {
        // The current observations are aligned to the previous interval.
        // Keep 0,2,4,... indices; future points align with the doubled
        // interval, making gaps explicit in sample.T for both viewers.
        var write = 0;
        for (var read = 0; read < _samples.Count; read += 2)
            _samples[write++] = _samples[read];
        _samples.RemoveRange(write, _samples.Count - write);
        _damageSampleInterval *= 2f;
        _damageSamplesCompactions++;
    }

    public FightLog? EndHunt(FightLogHeader header, IReadOnlyList<PartyMemberSnapshot> members, HuntRecorder recorder)
    {
        var samples = _samples.ToArray();
        _samples.Clear();

        if (!_logsAvailable)
            return null;

        var total = members.Sum(member => member.Damage);
        var gaugeSamples = recorder.GaugeSamples();
        if (!GuidingLandsSession.ShouldPersistLog(
            header.QuestId, header.StageId, header.LocalOnlyExpedition,
            members.Count, total, gaugeSamples.Length))
            return null;

        var safeDuration = Math.Max(header.DurationSeconds, 0.001f);
        var log = new FightLog
        {
            SchemaVersion = FightLog.CurrentSchemaVersion,
            PluginVersion = Plugin.BuildStamp,
            GameBuild = header.GameBuild,
            DamageScope = header.LocalOnlyExpedition
                ? GuidingLandsSession.DamageScope : null,
            QuestId = header.QuestId,
            QuestName = string.IsNullOrWhiteSpace(header.QuestName) ? $"Quest {header.QuestId}" : header.QuestName,
            Result = header.Result,
            StageId = header.StageId,
            Stage = header.StageName,
            StartedAt = header.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            DurationSeconds = header.DurationSeconds,
            TimerSource = header.TimerSource,
            HitCoverage = recorder.HitCoverage,
            Rewards = header.Rewards,
            Players = members
                .OrderByDescending(member => member.Damage)
                .ThenBy(member => member.Slot)
                .Select(member => new FightLogPlayer
                {
                    Slot = member.Slot,
                    Name = member.Name,
                    IsLocal = member.IsLocal,
                    Weapon = recorder.WeaponOf(member.Slot) ?? (member.IsLocal ? recorder.LocalWeapon : null),
                    Damage = member.Damage,
                    Dps = member.Damage / safeDuration,
                    // A zero-total, gauge-only expedition may still have
                    // a local roster row. Never serialize NaN/Infinity.
                    Percent = total > 0 ? 100f * member.Damage / total : 0f,
                    Carts = recorder.CartsOf(member.Slot)
                })
                .ToArray(),
            Monsters = recorder.Monsters(),
            Samples = samples,
            DamageSamplesCompactions = _damageSamplesCompactions,
            Hits = recorder.Hits(),
            GaugeSamples = gaugeSamples,
            GaugeSamplesDropped = recorder.GaugeSamplesDropped,
            GaugeClockDiscontinuities = recorder.GaugeClockDiscontinuities,
            Events = recorder.Events()
        };

        return Save(log) ? log : null;
    }

    /// <summary>Writes a finished log, adds it to the F9 history and to index.json.</summary>
    public bool Save(FightLog log)
    {
        if (!_logsAvailable || !TryWrite(log))
            return false;

        _history.Insert(0, log);
        while (_history.Count > HistoryLimit)
            _history.RemoveAt(_history.Count - 1);

        UpdateIndex(log);
        return true;
    }

    private bool TryWrite(FightLog log)
    {
        if (!TryEnsureLogDirectory())
            return false;

        var stamp = log.StartedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
        var middle = log.Kind == FightLog.KindTrial ? $"trial{log.DurationSeconds:0}s"
            : log.DamageScope == GuidingLandsSession.DamageScope
                ? "guiding-lands" : log.QuestId.ToString();
        var fileName = $"{stamp}_{middle}_{Sanitize(log.Result)}.json";
        var path = Path.Combine(_logsDirectory, fileName);

        try
        {
            WriteAtomic(path, JsonSerializer.Serialize(log, JsonOptions));
            log.FileName = fileName;
            Log.Info($"MhwDpsMeter: wrote fight log {fileName} ({log.Hits.Length} hits, {log.Events.Length} events).");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not write fight log ({ex.Message}).");
            _logsAvailable = false;
            return false;
        }
    }

    /// <summary>Prepends the new hunt to index.json; a viewer reading mid-write sees the old or new file, never a torn one.</summary>
    private void UpdateIndex(FightLog log)
    {
        _index.RemoveAll(entry => entry.File == log.FileName);
        _index.Insert(0, FightLogIndexEntry.From(log));
        WriteIndex();
    }

    /// <summary>First run after upgrading (or a lost index): rebuild from every existing log file.</summary>
    private void BuildIndexFromFiles()
    {
        if (!_logsAvailable || !Directory.Exists(_logsDirectory))
            return;

        try
        {
            var entries = new List<FightLogIndexEntry>();
            foreach (var file in LogFiles())
            {
                var log = TryReadLog(file);
                if (log is not null)
                    entries.Add(FightLogIndexEntry.From(log));
            }

            if (entries.Count == 0)
                return;

            entries.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            _index = entries;
            WriteIndex();
            Log.Info($"MhwDpsMeter: built {IndexFileName} from {entries.Count} existing fight logs.");
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not build {IndexFileName} ({ex.Message}).");
        }
    }

    private void WriteIndex()
    {
        try
        {
            WriteAtomic(IndexPath, JsonSerializer.Serialize(_index, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not write {IndexFileName} ({ex.Message}).");
        }
    }

    private List<FightLogIndexEntry> ReadIndex()
    {
        if (!File.Exists(IndexPath))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<FightLogIndexEntry>>(File.ReadAllText(IndexPath), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: {IndexFileName} unreadable, rebuilding ({ex.Message}).");
            return [];
        }
    }

    private void LoadHistory()
    {
        if (!Directory.Exists(_logsDirectory))
            return;

        try
        {
            foreach (var file in LogFiles().OrderByDescending(File.GetLastWriteTimeUtc).Take(HistoryLimit))
            {
                var log = TryReadLog(file);
                if (log is not null)
                    _history.Add(log);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not read fight logs ({ex.Message}).");
        }
    }

    private static FightLog? TryReadLog(string path)
    {
        try
        {
            var log = JsonSerializer.Deserialize<FightLog>(File.ReadAllText(path), JsonOptions);
            if (log is null)
                return null;
            log.FileName = Path.GetFileName(path);
            return log;
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: skipped unreadable log {Path.GetFileName(path)} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Per-hunt log files only: excludes index.json and the live-debug files.</summary>
    private IEnumerable<string> LogFiles() =>
        Directory.GetFiles(_logsDirectory, "*_*_*.json")
            .Where(path => !Path.GetFileName(path).StartsWith("live-debug", StringComparison.OrdinalIgnoreCase));

    private string IndexPath => Path.Combine(_logsDirectory, IndexFileName);

    private static void WriteAtomic(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    private bool TryEnsureLogDirectory()
    {
        if (!_logsAvailable)
            return false;

        try
        {
            Directory.CreateDirectory(_logsDirectory);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not create log directory ({ex.Message}).");
            _logsAvailable = false;
            return false;
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars);
    }
}
