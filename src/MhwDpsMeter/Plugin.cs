using System.Diagnostics;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.IO;

namespace MhwDpsMeter;

public sealed class Plugin : IPlugin
{
    public string Name => "DPS Meter";
    public string Author => "mhw-dps-meter";

    private const float PollIntervalSeconds = 0.1f;

    private readonly Overlay _overlay = new();
    private readonly Stopwatch _questTimer = new();

    private AddressMap? _map;
    private PartyDamageReader? _reader;
    private FightLogStore? _logs;
    private readonly MonsterHpTracker _monsterHp = new();
    private PartySnapshot? _snapshot;
    private bool _inQuest;
    private int _questId;
    private string _questName = "";
    private DateTimeOffset _questStartedAt;
    private float _pollAccumulator;
    private nint _moduleBase;
    private string _status = "not loaded";
    private string? _pendingResult;
    private uint _questState;
    private int _stageId;

    public PluginData Initialize()
    {
        return new PluginData
        {
            ImGuiWrappedInTreeNode = true
        };
    }

    public void OnLoad()
    {
        var gameDir = ResolveGameDirectory();
        var pluginDir = ResolvePluginDirectory(gameDir);

        _moduleBase = 0;
        try
        {
            _moduleBase = Process.GetCurrentProcess().MainModule?.BaseAddress ?? 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: MainModule base failed ({ex.Message}).");
        }

        if (_moduleBase == 0)
            _moduleBase = PartyDamageReader.GetModuleHandle("MonsterHunterWorld.exe");

        if (_moduleBase == 0)
        {
            _status = "could not resolve MonsterHunterWorld.exe base address";
            Log.Error($"MhwDpsMeter: {_status}.");
            return;
        }

        var version = 421631;
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (info.FilePrivatePart > 0)
                    version = info.FilePrivatePart;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not read exe version ({ex.Message}), using 421631.");
        }

        _map = AddressMap.TryLoad(
            [
                pluginDir,
                Path.Combine(gameDir, "nativePC", "plugins", "CSharp", "MhwDpsMeter"),
                Path.Combine(gameDir, "nativePC", "plugins", "CSharp"),
                gameDir
            ],
            version);

        if (_map is null)
        {
            _status = $"no address map for file version {version}";
            Log.Error($"MhwDpsMeter: {_status} (pluginDir={pluginDir}).");
            return;
        }

        Log.Info($"MhwDpsMeter: loaded {_map.SourceFile} (version {version}, base 0x{_moduleBase:X}).");
        _reader = new PartyDamageReader(_map, _moduleBase);
        _logs = new FightLogStore(pluginDir);
        _status = $"loaded map {Path.GetFileName(_map.SourceFile)}";
    }

    private static string ResolveGameDirectory()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
        }
        catch
        {
            // fall through
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolvePluginDirectory(string gameDir)
    {
        var fromAssembly = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        var candidates = new[]
        {
            Path.Combine(gameDir, "nativePC", "plugins", "CSharp", "MhwDpsMeter"),
            fromAssembly ?? "",
            Path.Combine(gameDir, "nativePC", "plugins", "CSharp")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;
        }

        var created = Path.Combine(gameDir, "nativePC", "plugins", "CSharp", "MhwDpsMeter");
        Directory.CreateDirectory(created);
        return created;
    }

    public void OnUpdate(float deltaTime)
    {
        if (Input.IsPressed(Key.F10))
            _overlay.Visible = !_overlay.Visible;

        SyncQuestState();

        if (!_inQuest || _reader is null)
            return;

        _pollAccumulator += deltaTime;
        if (_pollAccumulator < PollIntervalSeconds)
            return;

        _pollAccumulator = 0;
        var fallbackDamage = _monsterHp.Poll();
        if (!_reader.TryRead(out var snapshot, fallbackDamage))
        {
            _status = _reader.LastError;
            _snapshot = null;
            return;
        }

        _snapshot = WithRates(snapshot, (float)_questTimer.Elapsed.TotalSeconds);
        _status = string.IsNullOrEmpty(_reader.LastError)
            ? $"{snapshot.Members.Length} hunters ({_reader.DamageSource})"
            : $"{snapshot.Members.Length} hunters ({_reader.LastError})";
        _logs?.UpdateSamples((float)_questTimer.Elapsed.TotalSeconds, snapshot.SlotDamage);
    }

    public void OnImGuiRender()
    {
        var diagnostics =
            $"Status: {_status}\n" +
            $"Quest: {_inQuest} id {_questId} state {DescribeQuestState(_questState)}\n" +
            $"Stage: {(Stage)_stageId} ({_stageId})\n" +
            $"Damage source: {_reader?.DamageSource ?? "n/a"}\n" +
            $"Module: 0x{_moduleBase:X}\n" +
            "F9 = this menu, F10 = toggle overlay";
        _overlay.DrawSettings(_logs, diagnostics);
    }

    public void OnImGuiFreeRender()
    {
        _overlay.Draw(_snapshot, (float)_questTimer.Elapsed.TotalSeconds, _inQuest);
    }

    public void OnQuestEnter(int questId)
    {
        try
        {
            _stageId = (int)Area.CurrentStage;
        }
        catch
        {
            // keep last known stage
        }

        if (!IsHuntingStage((Stage)_stageId))
            return;

        BeginHunt(questId);
    }

    public void OnQuestComplete(int questId) => EndHunt(questId, "complete");

    public void OnQuestFail(int questId) => EndHunt(questId, "fail");

    public void OnQuestAbandon(int questId) => EndHunt(questId, "abandon");

    public void OnQuestReturn(int questId) => EndHunt(questId, "return");

    public void OnQuestLeave(int questId) => EndHunt(questId, _pendingResult ?? "leave");

    private void SyncQuestState()
    {
        int questId;
        uint questState;
        try
        {
            questId = Quest.CurrentQuestId;
            questState = Quest.QuestState;
            _stageId = (int)Area.CurrentStage;
        }
        catch
        {
            return;
        }

        _questState = questState;
        var hunting = questId > 0 && questState == (uint)QuestState.InQuest && IsHuntingStage((Stage)_stageId);

        if (hunting)
        {
            BeginHunt(questId);
            return;
        }

        if (_inQuest)
        {
            EndHunt(_questId, ResultFromQuestState(questState));
            return;
        }

        if (_reader is null)
            return;

        if (questId > 0 && questState == (uint)QuestState.Ready)
            _status = $"quest {questId} accepted — depart to start the overlay";
        else if (questId > 0)
            _status = $"quest {questId} ({DescribeQuestState(questState)})";
        else
            _status = "not in a hunt";
    }

    private void BeginHunt(int questId)
    {
        if (questId <= 0)
            return;

        if (_inQuest && _questId == questId)
            return;

        var questName = Quest.CurrentQuestName;
        if (questName.Contains("Expedition", StringComparison.OrdinalIgnoreCase)
            || questName.Contains("Guiding Lands", StringComparison.OrdinalIgnoreCase))
        {
            _inQuest = false;
            _snapshot = null;
            _status = "expedition/guiding lands (no quest-award damage)";
            return;
        }

        _inQuest = true;
        _questId = questId;
        _questName = string.IsNullOrWhiteSpace(questName) ? Quest.GetQuestName(questId) : questName;
        _questStartedAt = DateTimeOffset.UtcNow;
        _pendingResult = null;
        _snapshot = null;
        _pollAccumulator = 0;
        _questTimer.Restart();
        _monsterHp.Reset();
        _logs?.BeginHunt();
        _status = $"in quest {questId}";
    }

    private enum QuestState : uint
    {
        None = 0,
        Ready = 1,
        InQuest = 2,
        Success = 3,
        Completed = 4,
        Failed = 5,
        Abandon = 6,
        Quit = 7
    }

    private static string ResultFromQuestState(uint state) => (QuestState)state switch
    {
        QuestState.Success or QuestState.Completed => "complete",
        QuestState.Failed => "fail",
        QuestState.Abandon => "abandon",
        QuestState.Quit => "leave",
        _ => "leave"
    };

    private static string DescribeQuestState(uint state) => (QuestState)state switch
    {
        QuestState.None => "none",
        QuestState.Ready => "ready (hub)",
        QuestState.InQuest => "in quest",
        QuestState.Success => "success",
        QuestState.Completed => "completed",
        QuestState.Failed => "failed",
        QuestState.Abandon => "abandon",
        QuestState.Quit => "quit",
        _ => state.ToString()
    };

    private static bool IsHuntingStage(Stage stage) => stage switch
    {
        Stage.Astera or Stage.AsteraHub or Stage.ResearchBase or
        Stage.Seliana or Stage.SelianaHub or Stage.LivingQuarters or
        Stage.PrivateQuarters or Stage.PrivateSuite or Stage.TrainingCamp or
        Stage.ChamberOfFive or Stage.SelianaRoom or Stage.CharacterCreation or
        Stage.InfinityOfNothingHandler => false,
        _ => (uint)stage != 0
    };

    private void EndHunt(int questId, string result)
    {
        if (!_inQuest)
            return;

        _pendingResult = result;
        _inQuest = false;
        _questTimer.Stop();

        if (_reader is not null && _reader.TryRead(out var snapshot, _monsterHp.Poll()))
            _snapshot = WithRates(snapshot, (float)_questTimer.Elapsed.TotalSeconds);

        var members = _snapshot?.Members ?? [];
        _logs?.EndHunt(
            questId == 0 ? _questId : questId,
            _questName,
            result,
            _questStartedAt,
            (float)_questTimer.Elapsed.TotalSeconds,
            members);

        _pendingResult = null;
        _snapshot = null;
    }

    private static PartySnapshot WithRates(PartySnapshot snapshot, float elapsedSeconds)
    {
        var duration = Math.Max(elapsedSeconds, 0.001f);
        var total = Math.Max(snapshot.TotalDamage, 0);
        var members = snapshot.Members.Select(member => new PartyMemberSnapshot
        {
            Slot = member.Slot,
            Name = member.Name,
            Damage = member.Damage,
            IsLocal = member.IsLocal,
            Dps = member.Damage / duration,
            Percent = total > 0 ? 100f * member.Damage / total : 0f
        }).ToArray();

        return new PartySnapshot
        {
            Members = members,
            TotalDamage = snapshot.TotalDamage,
            SlotDamage = snapshot.SlotDamage
        };
    }
}
