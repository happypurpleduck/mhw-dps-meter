using System.Diagnostics;
using System.Reflection;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.Entities;
using SharpPluginLoader.Core.IO;

namespace MhwDpsMeter;

public sealed class Plugin : IPlugin
{
    public string Name => "DPS Meter";
    public string Author => "mhw-dps-meter";

    /// <summary>"0.3.0+2026-09-03 18:50Z": version plus build time, so a stale DLL is obvious.</summary>
    public static readonly string BuildStamp =
        typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Plugin).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private const float PollIntervalSeconds = 0.1f;

    private readonly Overlay _overlay = new();
    private readonly Stopwatch _questTimer = new();

    private AddressMap? _map;
    private PartyDamageReader? _reader;
    private FightLogStore? _logs;
    private LiveDebugStore? _liveDebug;
    private readonly MonsterHpTracker _monsterHp = new();
    private readonly DamageTracker _hits = new();
    private PartySnapshot? _snapshot;
    private bool _inQuest;
    private bool _showResults;
    private int _questId;
    private string _questName = "";
    private DateTimeOffset _questStartedAt;
    private float _pollAccumulator;
    private float _elapsedSeconds;
    private nint _moduleBase;
    private string _status = "not loaded";
    private string? _pendingResult;
    private uint _questState;
    private int _stageId;
    private string _pluginDir = "";
    private int _lastFallbackDamage;
    private int _gameBuild;
    private bool _mapMatchesBuild;
    // Quest that just ended. SPL's OnQuestComplete fires while Quest.QuestState still
    // reads InQuest for a few frames, which used to restart the hunt (timer reset to
    // ~0 => absurd DPS on the results screen, plus a duplicate millisecond fight log).
    private int _finishedQuestId;
    private DateTimeOffset _finishedAt;
    private bool _buildConfirmed;
    private float _buildRetryAccumulator;
    private int _buildRetries;
    // Training area (stage 504). Not a quest: no quest timer, no award table, and the
    // pole/wagon are not large monsters. Damage comes from the deal-damage hook only,
    // the DPS clock starts at the first hit, and F7 resets without leaving the area.
    private bool _training;

    public PluginData Initialize()
    {
        return new PluginData
        {
            ImGuiWrappedInTreeNode = true
        };
    }

    public void OnLoad()
    {
        Log.Info($"MhwDpsMeter: plugin {BuildStamp} loading.");
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

        // The exe's version resource is 1.0.0.0 on current builds, so use the build
        // number Capcom bakes into the window title ("MONSTER HUNTER: WORLD(421810)").
        var build = GameBuild.Detect(_moduleBase, out var buildSource, out _buildConfirmed);
        _gameBuild = build;
        Log.Info($"MhwDpsMeter: game build {build} ({buildSource}).");

        _map = AddressMap.TryLoad(
            [
                pluginDir,
                Path.Combine(gameDir, "nativePC", "plugins", "CSharp", "MhwDpsMeter"),
                Path.Combine(gameDir, "nativePC", "plugins", "CSharp"),
                gameDir
            ],
            build);

        if (_map is null)
        {
            _status = $"no address map for game build {build}";
            Log.Error($"MhwDpsMeter: {_status} (pluginDir={pluginDir}).");
            return;
        }

        Log.Info($"MhwDpsMeter: loaded {_map.SourceFile} (build {build}, base 0x{_moduleBase:X}).");
        _pluginDir = pluginDir;
        _reader = new PartyDamageReader(_map, _moduleBase);
        _logs = new FightLogStore(pluginDir);
        _liveDebug = new LiveDebugStore(pluginDir);

        _mapMatchesBuild = _map.SourceFile.Contains($".{build}.", StringComparison.Ordinal);
        if (_mapMatchesBuild && _buildConfirmed)
        {
            _hits.Install(_moduleBase, _map);
        }
        else if (!_mapMatchesBuild)
        {
            Log.Warn($"MhwDpsMeter: map {Path.GetFileName(_map.SourceFile)} is for another build; hit hook disabled, party read may fail.");
        }

        _status = _mapMatchesBuild
            ? $"loaded map {Path.GetFileName(_map.SourceFile)}"
            : $"WARNING: build {build} has no map, using {Path.GetFileName(_map.SourceFile)}";
        Log.Info($"MhwDpsMeter: live debug -> {_liveDebug.LatestPath}");
    }

    public void OnUnload() => _hits.Uninstall();

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

        if (Input.IsPressed(Key.F6))
            DumpDiagnostics();

        if (Input.IsPressed(Key.F7))
            ResetTraining();

        RetryBuildDetection(deltaTime);
        SyncQuestState();

        if (!(_inQuest || _training) || _reader is null)
            return;

        _pollAccumulator += deltaTime;
        if (_pollAccumulator < PollIntervalSeconds)
            return;

        _pollAccumulator = 0;
        if (_training)
        {
            PollTraining();
            return;
        }

        try
        {
            var fallbackDamage = _monsterHp.Poll();
            _hits.UpdateMonsters(_monsterHp.LiveInstances);
            _lastFallbackDamage = fallbackDamage;
            if (!_reader.TryRead(out var snapshot, fallbackDamage) || snapshot.Members.Length == 0)
            {
                if (_snapshot is { Members.Length: > 0 })
                    return;
                snapshot = LocalSnapshot(Math.Max(0, fallbackDamage));
            }

            _hits.UpdateParty(snapshot.Members);
            snapshot = MergeLiveDamage(snapshot, fallbackDamage);
            _elapsedSeconds = HuntElapsedSeconds();
            _snapshot = WithRates(snapshot, _elapsedSeconds);
            _status = string.IsNullOrEmpty(_reader.LastError)
                ? $"{snapshot.Members.Length} hunters ({_reader.DamageSource})"
                : $"{snapshot.Members.Length} hunters ({_reader.LastError})";
            _logs?.UpdateSamples(_elapsedSeconds, snapshot.SlotDamage);
            WriteLiveDebug(force: false);
        }
        catch (Exception ex)
        {
            _status = $"read failed ({ex.GetType().Name})";
            Log.Warn($"MhwDpsMeter: {_status}: {ex.Message}");
        }
    }

    private void PollTraining()
    {
        try
        {
            _monsterHp.Poll();
            var damage = _hits.LocalDamage;
            PartySnapshot snapshot;
            if (!_reader!.TryRead(out snapshot, 0) || snapshot.Members.Length == 0)
                snapshot = LocalSnapshot(0);

            // Solo in the training area: keep the reader's name/slot, take damage from the hook.
            var members = snapshot.Members
                .Select(member => member.With(damage: member.IsLocal ? damage : 0))
                .ToArray();
            var slotDamage = new int[PartyDamageReader.PartySlots];
            foreach (var member in members)
            {
                if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                    slotDamage[member.Slot] = member.Damage;
            }

            _hits.UpdateParty(members);
            _elapsedSeconds = (float)_hits.SinceFirstHit.TotalSeconds;
            _snapshot = WithRates(new PartySnapshot
            {
                Members = members,
                TotalDamage = damage,
                SlotDamage = slotDamage,
                HasAwardTable = false
            }, _elapsedSeconds);
            _reader.DamageSource = "training hits";
            _status = _hits.Hooked
                ? $"training area: {damage} dmg over {_elapsedSeconds:0}s ({_hits.Hits} hits)"
                : "training area: hit hook is off, no damage can be counted";
            WriteLiveDebug(force: false);
        }
        catch (Exception ex)
        {
            _status = $"training read failed ({ex.GetType().Name})";
            Log.Warn($"MhwDpsMeter: {_status}: {ex.Message}");
        }
    }

    private void BeginTraining()
    {
        if (_training)
            return;

        _training = true;
        _inQuest = false;
        _showResults = false;
        _pollAccumulator = 0;
        _monsterHp.Reset();
        _hits.AcceptAllTargets = true;
        ResetTraining();
        Log.Info("MhwDpsMeter: entered training area; counting hooked hits, F7 resets.");
    }

    private void EndTraining()
    {
        if (!_training)
            return;

        _training = false;
        _hits.AcceptAllTargets = false;
        _hits.Reset();
        _snapshot = null;
        _elapsedSeconds = 0;
        _status = "left training area";
    }

    /// <summary>F7 or the F9 button: zero the training damage and restart the DPS clock at the next hit.</summary>
    private void ResetTraining()
    {
        if (!_training)
            return;

        _hits.Reset();
        _elapsedSeconds = 0;
        _snapshot = WithRates(LocalSnapshot(0), 0);
        _status = "training area: reset, waiting for first hit";
    }

    /// <summary>
    /// Manual probe (F6 or the F9 button): re-read the party even outside a quest so
    /// the roster can be verified from the hub, then write live-debug.json.
    /// </summary>
    private void DumpDiagnostics()
    {
        try
        {
            if (_reader is not null && !_inQuest && !_training)
                _reader.TryRead(out _, 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: diagnostics probe failed ({ex.Message}).");
        }

        WriteLiveDebug(force: true);
    }

    /// <summary>
    /// If load-time detection only guessed the build, keep trying the window title
    /// (the window is created well after plugins load). Once confirmed, swap the map
    /// if a better one exists and enable the hit hook.
    /// </summary>
    private void RetryBuildDetection(float deltaTime)
    {
        if (_buildConfirmed || _map is null || _buildRetries >= 120)
            return;

        _buildRetryAccumulator += deltaTime;
        if (_buildRetryAccumulator < 2f)
            return;

        _buildRetryAccumulator = 0;
        _buildRetries++;

        var build = GameBuild.TryFromWindowTitle();
        if (build <= 0)
            return;

        _buildConfirmed = true;
        _gameBuild = build;
        Log.Info($"MhwDpsMeter: game build {build} confirmed from window title.");

        if (!_map.SourceFile.Contains($".{build}.", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(_map.SourceFile) ?? _pluginDir;
            var replacement = AddressMap.TryLoad([dir, _pluginDir], build);
            if (replacement is not null && replacement.SourceFile.Contains($".{build}.", StringComparison.Ordinal))
            {
                _map = replacement;
                _reader = new PartyDamageReader(_map, _moduleBase);
                Log.Info($"MhwDpsMeter: switched to {Path.GetFileName(_map.SourceFile)}.");
            }
        }

        _mapMatchesBuild = _map.SourceFile.Contains($".{build}.", StringComparison.Ordinal);
        _status = _mapMatchesBuild
            ? $"loaded map {Path.GetFileName(_map.SourceFile)}"
            : $"WARNING: build {build} has no map, using {Path.GetFileName(_map.SourceFile)}";
        if (_mapMatchesBuild)
            _hits.Install(_moduleBase, _map);
    }

    public void OnImGuiRender()
    {
        var slotLines = "";
        if (_reader?.LastSlots is { Length: > 0 } slots)
        {
            foreach (var slot in slots)
            {
                slotLines +=
                    $"  [{slot.Slot}] shown={slot.Shown} '{slot.ShownName}'{(slot.IsLocal ? "*" : "")} dmg={slot.RawDamage} " +
                    $"party='{slot.PartyName}' ptr={(slot.PtrOk ? slot.Ptr : "no")} hex={slot.PartyNameHex} why={slot.Why}\n";
            }
        }

        var diagnostics =
            $"Plugin build: {BuildStamp}\n" +
            $"Status: {_status}\n" +
            $"Game build: {_gameBuild}  map: {Path.GetFileName(_map?.SourceFile ?? "none")}{(_mapMatchesBuild ? "" : "  (MISMATCH)")}\n" +
            $"Quest: {_inQuest} id {_questId} state {DescribeQuestState(_questState)}\n" +
            $"Stage: {(Stage)_stageId} ({_stageId}){(_training ? "  training mode" : "")}\n" +
            $"Damage source: {_reader?.DamageSource ?? "n/a"}\n" +
            $"Hit hook: {_hits.Status} calls={_hits.Calls} counted={_hits.Hits} ignored={_hits.Ignored} local={_hits.LocalDamage} last {_hits.LastHit}\n" +
            $"Monsters: {_monsterHp.LastMonsters}\n" +
            $"Party size: {_reader?.LastPartySize ?? 0}\n" +
            $"Local name: {_reader?.LastLocalName ?? "-"}\n" +
            $"Award layout: {_reader?.LastLayout ?? "n/a"} packed={_reader?.LastHasPackedTable}\n" +
            $"Damage base: {_reader?.LastDamageBase ?? "0"} ({_reader?.LastDamageError})\n" +
            $"Party array: {_reader?.LastPartyArray ?? "0"} ({_reader?.LastPartyArrayError})\n" +
            $"Slot damage: {FormatSlots(_reader?.LastRawDamage)}\n" +
            $"Hook slots: {FormatSlots(_hits.Snapshot())}\n" +
            $"HP fallback: {_lastFallbackDamage}\n" +
            $"Slots:\n{slotLines}" +
            $"Live debug: {_liveDebug?.LatestPath ?? "n/a"}\n" +
            $"Module: 0x{_moduleBase:X}\n" +
            "F6 = force debug dump, F7 = reset training damage, F9 = this menu, F10 = toggle overlay";
        _overlay.DrawSettings(_logs, diagnostics, DumpDiagnostics, _training ? ResetTraining : null);
    }

    private void WriteLiveDebug(bool force)
    {
        if (_liveDebug is null || _reader is null)
            return;

        var hooked = _hits.Snapshot();
        var overlayDamage = new int[PartyDamageReader.PartySlots];
        if (_snapshot is not null)
        {
            foreach (var member in _snapshot.Members)
            {
                if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                    overlayDamage[member.Slot] = member.Damage;
            }
        }

        var snapshot = new LiveDebugSnapshot
        {
            At = DateTimeOffset.Now.ToString("O"),
            InQuest = _inQuest,
            QuestId = _questId,
            QuestName = _questName,
            Elapsed = _elapsedSeconds,
            Status = _status,
            GameBuild = _gameBuild,
            MapFile = Path.GetFileName(_map?.SourceFile ?? ""),
            HookStatus = _hits.Status,
            HookHits = _hits.Hits,
            HookCalls = _hits.Calls,
            HookIgnored = _hits.Ignored,
            HookLastHit = _hits.LastHit,
            Monsters = _monsterHp.LastMonsters,
            DamageSource = _reader.DamageSource,
            LastError = _reader.LastError,
            Layout = _reader.LastLayout,
            PartySize = _reader.LastPartySize,
            LocalName = _reader.LastLocalName,
            LocalInstance = _reader.LastLocalInstance,
            PartyArray = _reader.LastPartyArray,
            PartyArrayError = _reader.LastPartyArrayError,
            DamageBase = _reader.LastDamageBase,
            DamageError = _reader.LastDamageError,
            HasPackedTable = _reader.LastHasPackedTable,
            FallbackLocalDamage = _lastFallbackDamage,
            HookTotal = hooked.Sum(),
            HookSlots = hooked,
            OverlayMemberCount = _snapshot?.Members.Length ?? 0,
            OverlayNames = _snapshot is null
                ? ""
                : string.Join(" | ", _snapshot.Members.Select(m => $"{m.Slot}:{m.Name}{(m.IsLocal ? "*" : "")}")),
            OverlayDamage = overlayDamage,
            Slots = _reader.LastSlots
        };
        _liveDebug.Write(snapshot, force);
    }

    private static string FormatSlots(int[]? slots)
    {
        if (slots is not { Length: > 0 })
            return "-";
        return string.Join(" / ", slots);
    }

    public void OnImGuiFreeRender()
    {
        _overlay.Draw(
            _snapshot,
            _elapsedSeconds,
            _inQuest || _showResults || _training,
            _training ? "Training  |  F7 resets  |  DPS since first hit" : null);
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

    public void OnQuestLeave(int questId)
    {
        if (!_inQuest)
            return;

        var state = _questState;
        try
        {
            state = Quest.QuestState;
        }
        catch
        {
            // use last known state
        }

        EndHunt(questId, _pendingResult ?? ResultFromQuestState(state));
    }

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

        if (questId <= 0 || questState is (uint)QuestState.None or (uint)QuestState.Ready)
            _finishedQuestId = 0;

        if ((Stage)_stageId == Stage.TrainingCamp && !_inQuest)
        {
            if (_showResults)
            {
                _showResults = false;
                _snapshot = null;
            }

            BeginTraining();
            return;
        }

        if (_training)
            EndTraining();

        var hunting = questId > 0 && questState == (uint)QuestState.InQuest && IsHuntingStage((Stage)_stageId);

        if (hunting)
        {
            _showResults = false;
            BeginHunt(questId);
            return;
        }

        if (_inQuest)
        {
            var terminal = questState is
                (uint)QuestState.Success or
                (uint)QuestState.Completed or
                (uint)QuestState.Failed or
                (uint)QuestState.Abandon or
                (uint)QuestState.Quit;
            if (!terminal && IsHuntingStage((Stage)_stageId) && questId > 0)
                return;

            EndHunt(_questId, ResultFromQuestState(questState));
            return;
        }

        if (_showResults && !IsHuntingStage((Stage)_stageId))
        {
            _showResults = false;
            _snapshot = null;
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

        // Same quest ending a moment ago: this is the post-quest state flicker, not a new hunt.
        if (questId == _finishedQuestId && (DateTimeOffset.UtcNow - _finishedAt) < TimeSpan.FromMinutes(3))
            return;

        string questName;
        try
        {
            questName = Quest.CurrentQuestName;
            if (string.IsNullOrWhiteSpace(questName))
                questName = Quest.GetQuestName(questId);
        }
        catch
        {
            questName = "";
        }

        if (IsUnsupportedHunt((Stage)_stageId, questName))
        {
            _inQuest = false;
            _showResults = false;
            _snapshot = null;
            _status = "expedition/guiding lands (no quest-award damage)";
            return;
        }

        _inQuest = true;
        _showResults = false;
        _questId = questId;
        _questName = string.IsNullOrWhiteSpace(questName) ? $"Quest {questId}" : questName;
        _questStartedAt = DateTimeOffset.UtcNow;
        _pendingResult = null;
        _elapsedSeconds = 0;
        _snapshot = new PartySnapshot
        {
            Members =
            [
                new PartyMemberSnapshot
                {
                    Slot = 0,
                    Name = "You",
                    Damage = 0,
                    IsLocal = true
                }
            ]
        };
        _pollAccumulator = 0;
        _questTimer.Restart();
        _monsterHp.Reset();
        _hits.Reset();
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
        Stage.InfinityOfNothingHandler or Stage.GuidingLands => false,
        _ => (uint)stage != 0
    };

    private void EndHunt(int questId, string result)
    {
        if (!_inQuest)
            return;

        _pendingResult = result;
        _inQuest = false;
        _finishedQuestId = _questId;
        _finishedAt = DateTimeOffset.UtcNow;
        _questTimer.Stop();
        _elapsedSeconds = Math.Max(_elapsedSeconds, HuntElapsedSeconds());

        try
        {
            if (_reader is not null)
            {
                var fallbackDamage = _monsterHp.Poll();
                _hits.UpdateMonsters(_monsterHp.LiveInstances);
                if (_reader.TryRead(out var snapshot, fallbackDamage) && snapshot.Members.Length > 0)
                {
                    _hits.UpdateParty(snapshot.Members);
                    snapshot = MergeLiveDamage(snapshot, fallbackDamage);
                    _snapshot = WithRates(snapshot, _elapsedSeconds);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: final read failed ({ex.Message}).");
        }

        var members = _snapshot?.Members ?? [];
        _logs?.EndHunt(
            questId == 0 ? _questId : questId,
            _questName,
            result,
            _questStartedAt,
            _elapsedSeconds,
            members);

        _pendingResult = null;
        _showResults = members.Length > 0;
        if (!_showResults)
            _snapshot = null;
    }

    private float HuntElapsedSeconds()
    {
        try
        {
            var time = Quest.QuestEndTimer.Time;
            if (time is > 0.25f and < 60_000f)
                return time;
        }
        catch
        {
            // fall through to local clock
        }

        return (float)_questTimer.Elapsed.TotalSeconds;
    }

    private static bool IsUnsupportedHunt(Stage stage, string questName)
    {
        if (stage is Stage.GuidingLands)
            return true;

        return questName.Contains("Expedition", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("Guiding Lands", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("Expédition", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("Expedición", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("Spedizione", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("Expedição", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("探検", StringComparison.Ordinal)
               || questName.Contains("探索", StringComparison.Ordinal)
               || questName.Contains("導きの地", StringComparison.Ordinal)
               || questName.Contains("탐험", StringComparison.Ordinal)
               || questName.Contains("안내하는 땅", StringComparison.Ordinal)
               || questName.Contains("Экспедиция", StringComparison.OrdinalIgnoreCase)
               || questName.Contains("探险", StringComparison.Ordinal)
               || questName.Contains("调查地点", StringComparison.Ordinal);
    }

    private static PartySnapshot WithRates(PartySnapshot snapshot, float elapsedSeconds)
    {
        var duration = Math.Max(elapsedSeconds, 1f);
        var total = Math.Max(snapshot.TotalDamage, 0);
        var members = snapshot.Members.Select(member => new PartyMemberSnapshot
        {
            Slot = member.Slot,
            Name = member.Name,
            Damage = member.Damage,
            IsLocal = member.IsLocal,
            Instance = member.Instance,
            Dps = member.Damage / duration,
            Percent = total > 0 ? 100f * member.Damage / total : 0f
        }).ToArray();

        return new PartySnapshot
        {
            Members = members,
            TotalDamage = snapshot.TotalDamage,
            SlotDamage = snapshot.SlotDamage,
            HasAwardTable = snapshot.HasAwardTable
        };
    }

    private PartySnapshot MergeLiveDamage(PartySnapshot snapshot, int fallbackLocalDamage)
    {
        // Award table only wins once it has real totals. Otherwise keep live fallbacks.
        if (snapshot.HasAwardTable && snapshot.TotalDamage > 0)
            return MergeZeroSlots(snapshot, _hits.Snapshot());

        var hooked = _hits.Snapshot();
        var members = snapshot.Members.Select(member =>
        {
            var damage = member.Damage;
            if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                damage = Math.Max(damage, hooked[member.Slot]);
            if (member.IsLocal && snapshot.Members.Length == 1)
                damage = Math.Max(damage, Math.Max(0, fallbackLocalDamage));

            return member.With(damage: damage);
        }).ToArray();

        var hookTotal = hooked.Sum();
        var tableTotal = snapshot.Members.Sum(member => member.Damage);
        if (tableTotal == 0 && members.Length > 0)
            _reader!.DamageSource = hookTotal > 0
                ? "live hits"
                : snapshot.Members.Length == 1 ? "monster HP" : "party list";

        var slotDamage = new int[PartyDamageReader.PartySlots];
        var total = 0;
        foreach (var member in members)
        {
            if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                slotDamage[member.Slot] = member.Damage;
            total += member.Damage;
        }

        return new PartySnapshot
        {
            Members = members,
            TotalDamage = total,
            SlotDamage = slotDamage,
            HasAwardTable = false
        };
    }

    private static PartySnapshot MergeZeroSlots(PartySnapshot snapshot, int[] hooked)
    {
        var members = snapshot.Members.Select(member =>
        {
            if (member.Damage > 0 || member.Slot is < 0 or >= PartyDamageReader.PartySlots)
                return member;
            var hookedDamage = hooked[member.Slot];
            return hookedDamage > 0 ? member.With(damage: hookedDamage) : member;
        }).ToArray();

        var slotDamage = new int[PartyDamageReader.PartySlots];
        var total = 0;
        foreach (var member in members)
        {
            if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                slotDamage[member.Slot] = member.Damage;
            total += member.Damage;
        }

        return new PartySnapshot
        {
            Members = members,
            TotalDamage = total,
            SlotDamage = slotDamage,
            HasAwardTable = true
        };
    }

    private static PartySnapshot LocalSnapshot(int damage)
    {
        nint instance = 0;
        try
        {
            instance = Player.MainPlayer?.Instance ?? 0;
        }
        catch
        {
            // menus
        }

        return new PartySnapshot
        {
            Members =
            [
                new PartyMemberSnapshot
                {
                    Slot = 0,
                    Name = "You",
                    Damage = damage,
                    IsLocal = true,
                    Instance = instance
                }
            ],
            TotalDamage = damage,
            SlotDamage = [damage, 0, 0, 0],
            HasAwardTable = false
        };
    }
}
