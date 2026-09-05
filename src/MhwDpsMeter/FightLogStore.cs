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
    int GameBuild);

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
        if (_index.Count == 0 || _index.Any(entry => entry.TotalDamage == 0))
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
        ExpandedHistoryIndex = null;
        _samples.Add(new FightLogSample { T = 0 });
    }

    /// <summary>
    /// Appends one sample per <see cref="SampleIntervalSeconds"/> of hunt time. Catches up
    /// with repeated samples when the clock jumps (SOS join-in-progress starts mid-quest).
    /// </summary>
    public void UpdateSamples(float elapsedSeconds, int[] slotDamage)
    {
        while (_samples.Count < MaxSamples)
        {
            var nextT = _samples.Count * SampleIntervalSeconds;
            if (elapsedSeconds + 0.05f < nextT)
                return;

            _samples.Add(new FightLogSample
            {
                T = nextT,
                Damage = (int[])slotDamage.Clone()
            });
        }
    }

    public FightLog? EndHunt(FightLogHeader header, IReadOnlyList<PartyMemberSnapshot> members, HuntRecorder recorder)
    {
        var samples = _samples.ToArray();
        _samples.Clear();

        if (!_logsAvailable || header.QuestId <= 0 || members.Count == 0)
            return null;

        var total = members.Sum(member => member.Damage);
        if (total <= 0)
            return null;

        var safeDuration = Math.Max(header.DurationSeconds, 0.001f);
        var log = new FightLog
        {
            SchemaVersion = FightLog.CurrentSchemaVersion,
            PluginVersion = Plugin.BuildStamp,
            GameBuild = header.GameBuild,
            QuestId = header.QuestId,
            QuestName = string.IsNullOrWhiteSpace(header.QuestName) ? $"Quest {header.QuestId}" : header.QuestName,
            Result = header.Result,
            StageId = header.StageId,
            Stage = header.StageName,
            StartedAt = header.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            DurationSeconds = header.DurationSeconds,
            TimerSource = header.TimerSource,
            HitCoverage = "local",
            Players = members
                .OrderByDescending(member => member.Damage)
                .ThenBy(member => member.Slot)
                .Select(member => new FightLogPlayer
                {
                    Slot = member.Slot,
                    Name = member.Name,
                    IsLocal = member.IsLocal,
                    Weapon = member.IsLocal ? recorder.LocalWeapon : null,
                    Damage = member.Damage,
                    Dps = member.Damage / safeDuration,
                    Percent = 100f * member.Damage / total
                })
                .ToArray(),
            Monsters = recorder.Monsters(),
            Samples = samples,
            Hits = recorder.Hits(),
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
        var middle = log.Kind == FightLog.KindTrial ? $"trial{log.DurationSeconds:0}s" : log.QuestId.ToString();
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
