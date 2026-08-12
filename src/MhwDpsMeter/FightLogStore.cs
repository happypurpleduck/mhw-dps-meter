using System.Text.Json;
using SharpPluginLoader.Core;

namespace MhwDpsMeter;

internal sealed class FightLogStore
{
    public const int MaxSamples = 900;
    public const int HistoryLimit = 20;
    public const float SampleIntervalSeconds = 2f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _logsDirectory;
    private readonly List<FightLogSample> _samples = [];
    private readonly List<FightLog> _history = [];
    private bool _logsAvailable = true;

    public IReadOnlyList<FightLog> History => _history;
    public int? ExpandedHistoryIndex { get; set; }

    public FightLogStore(string pluginDirectory)
    {
        _logsDirectory = Path.Combine(pluginDirectory, "logs");
        TryEnsureLogDirectory();
        LoadHistory();
    }

    public void BeginHunt()
    {
        _samples.Clear();
        ExpandedHistoryIndex = null;
        _samples.Add(new FightLogSample { T = 0, Damage = new int[4] });
    }

    public void UpdateSamples(float elapsedSeconds, int[] slotDamage)
    {
        var nextT = _samples.Count * SampleIntervalSeconds;
        if (elapsedSeconds + 0.05f < nextT || _samples.Count >= MaxSamples)
            return;

        _samples.Add(new FightLogSample
        {
            T = nextT,
            Damage = (int[])slotDamage.Clone()
        });
    }

    public FightLog? EndHunt(
        int questId,
        string questName,
        string result,
        DateTimeOffset startedAt,
        float durationSeconds,
        IReadOnlyList<PartyMemberSnapshot> members)
    {
        if (!_logsAvailable)
            return null;

        if (questId <= 0 || IsUnsupportedQuest(questName))
            return null;

        if (members.Count == 0)
            return null;

        var total = members.Sum(member => member.Damage);
        if (total <= 0)
            return null;

        var safeDuration = Math.Max(durationSeconds, 0.001f);
        var log = new FightLog
        {
            QuestId = questId,
            QuestName = string.IsNullOrWhiteSpace(questName) ? $"Quest {questId}" : questName,
            Result = result,
            StartedAt = startedAt,
            DurationSeconds = durationSeconds,
            Players = members
                .OrderByDescending(member => member.Damage)
                .Select(member => new FightLogPlayer
                {
                    Name = member.Name,
                    Damage = member.Damage,
                    Dps = member.Damage / safeDuration,
                    Percent = total > 0 ? 100f * member.Damage / total : 0f
                })
                .ToArray(),
            Samples = _samples.ToArray()
        };

        _samples.Clear();

        if (!TryWrite(log))
            return null;

        _history.Insert(0, log);
        while (_history.Count > HistoryLimit)
            _history.RemoveAt(_history.Count - 1);

        return log;
    }

    private bool TryWrite(FightLog log)
    {
        if (!TryEnsureLogDirectory())
            return false;

        var stamp = log.StartedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
        var safeResult = Sanitize(log.Result);
        var fileName = $"{stamp}_{log.QuestId}_{safeResult}.json";
        var path = Path.Combine(_logsDirectory, fileName);

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(log, JsonOptions));
            log.FileName = fileName;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not write fight log ({ex.Message}).");
            _logsAvailable = false;
            return false;
        }
    }

    private void LoadHistory()
    {
        if (!Directory.Exists(_logsDirectory))
            return;

        try
        {
            var files = Directory.GetFiles(_logsDirectory, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(HistoryLimit);

            foreach (var file in files)
            {
                try
                {
                    var log = JsonSerializer.Deserialize<FightLog>(File.ReadAllText(file), JsonOptions);
                    if (log is null)
                        continue;
                    log.FileName = Path.GetFileName(file);
                    _history.Add(log);
                }
                catch (Exception ex)
                {
                    Log.Warn($"MhwDpsMeter: skipped unreadable log {Path.GetFileName(file)} ({ex.Message}).");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not read fight logs ({ex.Message}).");
        }
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

    private static bool IsUnsupportedQuest(string questName)
    {
        return questName.Contains("Expedition", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("Guiding Lands", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars);
    }
}
