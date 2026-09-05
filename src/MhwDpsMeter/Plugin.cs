using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpPluginLoader.Core;
using SharpPluginLoader.Core.Actions;
using SharpPluginLoader.Core.Entities;
using SharpPluginLoader.Core.IO;

namespace MhwDpsMeter;

public sealed class Plugin : IPlugin
{
    public string Name => "DPS Meter";
    public string Author => "mhw-dps-meter";

    /// <summary>"0.4.0+2026-09-05 18:50Z": version plus build time, so a stale DLL is obvious.</summary>
    public static readonly string BuildStamp =
        typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Plugin).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private const float PollIntervalSeconds = 0.1f;
    private const string TrainingHint = "Training  |  F7 resets  |  F8 time trial  |  DPS since first hit";

    private readonly Overlay _overlay = new();
    private readonly Stopwatch _questTimer = new();
    private readonly MonsterHpTracker _monsterHp = new();
    private readonly DamageTracker _hits = new();
    private readonly HuntRecorder _recorder;
    private readonly TimeTrial _trial;
    private PluginSettings _settings = new();

    private AddressMap? _map;
    private PartyDamageReader? _reader;
    private FightLogStore? _logs;
    private LiveDebugStore? _liveDebug;
    private PartySnapshot? _snapshot;
    private bool _inQuest;
    private bool _showResults;
    private int _questId;
    private string _questName = "";
    private DateTimeOffset _questStartedAt;
    private float _pollAccumulator;
    private float _elapsedSeconds;
    private string _timerSource = "local";
    private nint _moduleBase;
    private string _status = "not loaded";
    private string _damageSource = "n/a";
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

    public Plugin()
    {
        _recorder = new HuntRecorder(ResolveActionName);
        _trial = new TimeTrial(ResolveActionName);
    }

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

        _moduleBase = ResolveModuleBase();
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
        _settings = PluginSettings.Load(pluginDir);
        _overlay.Visible = _settings.OverlayVisible;
        _overlay.Opacity = _settings.OverlayOpacity;
        _reader = new PartyDamageReader(_map, _moduleBase);
        _logs = new FightLogStore(pluginDir);
        _liveDebug = new LiveDebugStore(pluginDir);

        ApplyMap(build);
        if (!_mapMatchesBuild)
            Log.Warn($"MhwDpsMeter: map {Path.GetFileName(_map.SourceFile)} is for another build; hit hook disabled, party read may fail.");
        Log.Info($"MhwDpsMeter: live debug -> {_liveDebug.LatestPath}");
    }

    public void OnUnload() => _hits.Uninstall();

    /// <summary>Sets the status line and installs the hit hook when the loaded map is for this exact build.</summary>
    private void ApplyMap(int build)
    {
        if (_map is null)
            return;

        _mapMatchesBuild = MapIsForBuild(_map, build);
        _status = _mapMatchesBuild
            ? $"loaded map {Path.GetFileName(_map.SourceFile)}"
            : $"WARNING: build {build} has no map, using {Path.GetFileName(_map.SourceFile)}";
        if (_mapMatchesBuild && _buildConfirmed)
            _hits.Install(_moduleBase, _map);
    }

    private static bool MapIsForBuild(AddressMap map, int build) =>
        map.SourceFile.Contains($".{build}.", StringComparison.Ordinal);

    private static nint ResolveModuleBase()
    {
        try
        {
            var fromProcess = Process.GetCurrentProcess().MainModule?.BaseAddress ?? 0;
            if (fromProcess != 0)
                return fromProcess;
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: MainModule base failed ({ex.Message}).");
        }

        return GetModuleHandle("MonsterHunterWorld.exe");
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
        {
            _overlay.Visible = !_overlay.Visible;
            _settings.OverlayVisible = _overlay.Visible;
            _settings.Save();
        }

        if (Input.IsPressed(Key.F6))
            DumpDiagnostics();

        if (Input.IsPressed(Key.F7))
            ResetTraining();

        if (Input.IsPressed(Key.F8))
            ToggleTrial();

        RetryBuildDetection(deltaTime);
        SyncQuestState();

        if (!(_inQuest || _training) || _reader is null)
            return;

        _pollAccumulator += deltaTime;
        if (_pollAccumulator < PollIntervalSeconds)
            return;

        _pollAccumulator = 0;
        if (_training)
            PollTraining();
        else
            PollHunt();
    }

    private void PollHunt()
    {
        try
        {
            var snapshot = ReadPartyWithFallbacks();
            if (snapshot is null)
                return;

            _elapsedSeconds = HuntElapsedSeconds();
            _snapshot = snapshot.WithRates(_elapsedSeconds);
            _status = string.IsNullOrEmpty(_reader!.LastError)
                ? $"{snapshot.Members.Length} hunters ({_damageSource})"
                : $"{snapshot.Members.Length} hunters ({_reader.LastError})";

            _recorder.ObserveMonsters(_elapsedSeconds, _monsterHp.LastTracked);
            _recorder.ObserveParty(_elapsedSeconds, snapshot.Members);
            _recorder.ObserveWeapon(_elapsedSeconds, ReadLocalWeapon(), snapshot.LocalSlot);
            _recorder.AddHits(_elapsedSeconds, _hits.DrainHits(), snapshot.LocalSlot);
            _logs?.UpdateSamples(_elapsedSeconds, snapshot.SlotDamage);
            WriteLiveDebug(force: false);
        }
        catch (Exception ex)
        {
            _status = $"read failed ({ex.GetType().Name})";
            Log.Warn($"MhwDpsMeter: {_status}: {ex.Message}");
        }
    }

    /// <summary>
    /// Polls monsters and the party table, then layers the live fallbacks (hooked hits,
    /// monster HP lost) over it. Returns null when nothing new could be read and the
    /// previous snapshot should stay on screen.
    /// </summary>
    private PartySnapshot? ReadPartyWithFallbacks()
    {
        var fallbackDamage = _monsterHp.Poll();
        _hits.UpdateMonsters(_monsterHp.LiveInstances);
        _lastFallbackDamage = fallbackDamage;

        if (!_reader!.TryRead(out var snapshot, fallbackDamage) || snapshot.Members.Length == 0)
        {
            if (_snapshot is { Members.Length: > 0 })
                return null;
            snapshot = PartySnapshot.LocalOnly(fallbackDamage, "You", LocalPlayerInstance());
        }

        _hits.UpdateParty(snapshot.Members);
        return MergeLiveDamage(snapshot, fallbackDamage);
    }

    private void PollTraining()
    {
        try
        {
            _monsterHp.Poll();
            if (_trial.Update(_hits.DrainHits(), ReadLocalWeapon()))
                FinishTrial();

            // While a trial is armed/running/finished the overlay shows the trial window only.
            var damage = _trial.Active ? _trial.Damage : _hits.LocalDamage;
            _elapsedSeconds = _trial.Active ? _trial.Elapsed : (float)_hits.SinceFirstHit.TotalSeconds;

            if (!_reader!.TryRead(out var snapshot, 0) || snapshot.Members.Length == 0)
                snapshot = PartySnapshot.LocalOnly(0, "You", LocalPlayerInstance());

            // Solo in the training area: keep the reader's name/slot, take damage from the hook.
            var members = snapshot.Members
                .Select(member => member.With(damage: member.IsLocal ? damage : 0))
                .ToArray();

            _hits.UpdateParty(members);
            _snapshot = PartySnapshot.From(members, hasAwardTable: false).WithRates(_elapsedSeconds);
            _damageSource = _trial.Active ? "time trial" : "training hits";
            _status = !_hits.Hooked
                ? "training area: hit hook is off, no damage can be counted"
                : _trial.State switch
                {
                    TimeTrialState.Armed => $"time trial {_trial.DurationSeconds}s armed, waiting for first hit",
                    TimeTrialState.Running => $"time trial: {damage} dmg, {_trial.Remaining:0.0}s left",
                    TimeTrialState.Finished => $"time trial done: {damage} dmg in {_trial.DurationSeconds}s ({_trial.Dps:0.0} DPS)",
                    _ => $"training area: {damage} dmg over {_elapsedSeconds:0}s ({_hits.Hits} hits)"
                };
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
        _hits.RecordHits = false;
        ResetTraining();
        Log.Info("MhwDpsMeter: entered training area; counting hooked hits, F7 resets.");
    }

    private void EndTraining()
    {
        if (!_training)
            return;

        _training = false;
        _trial.Cancel();
        _hits.RecordHits = false;
        _hits.AcceptAllTargets = false;
        _hits.Reset();
        _snapshot = null;
        _elapsedSeconds = 0;
        _status = "left training area";
    }

    /// <summary>F7 or the F9 button: zero the training damage, drop any trial, and restart the DPS clock at the next hit.</summary>
    private void ResetTraining()
    {
        if (!_training)
            return;

        _trial.Cancel();
        _hits.RecordHits = false;
        _hits.Reset();
        _elapsedSeconds = 0;
        _snapshot = PartySnapshot.LocalOnly(0, "You", LocalPlayerInstance()).WithRates(0);
        _status = "training area: reset, waiting for first hit";
    }

    /// <summary>F8 or the F9 button: arm a trial with the configured duration, or cancel the one in progress.</summary>
    private void ToggleTrial()
    {
        if (!_training)
        {
            _status = "time trial only works in the training area";
            return;
        }

        if (_trial.State is TimeTrialState.Armed or TimeTrialState.Running)
        {
            _trial.Cancel();
            _hits.RecordHits = false;
            _status = "time trial cancelled";
            return;
        }

        if (!_hits.Hooked)
        {
            _status = "time trial needs the hit hook (address map mismatch)";
            return;
        }

        _hits.RecordHits = true;
        _trial.Arm(_settings.TrialDurationSeconds);
        _trial.PreviousBest = _logs?.BestTrial(ReadLocalWeapon()?.ToString(), _trial.DurationSeconds)?.TotalDamage;
        _status = $"time trial {_trial.DurationSeconds}s armed, waiting for first hit";
        Log.Info($"MhwDpsMeter: time trial {_trial.DurationSeconds}s armed.");
    }

    /// <summary>Deadline reached: compare with the saved best for this weapon/duration and write the trial log.</summary>
    private void FinishTrial()
    {
        _hits.RecordHits = false;
        var best = _logs?.BestTrial(_trial.Weapon, _trial.DurationSeconds);
        _trial.PreviousBest = best?.TotalDamage;
        _trial.IsPersonalBest = best is null || _trial.Damage > best.TotalDamage;

        if (_trial.Damage <= 0 || _logs is null)
            return;

        var name = _reader?.LastLocalName is { Length: > 0 } local ? local : "You";
        var log = _trial.BuildLog(name, _stageId, ((Stage)_stageId).ToString(), _gameBuild);
        if (_logs.Save(log))
            _trial.SavedFileName = log.FileName;
        Log.Info($"MhwDpsMeter: time trial {_trial.DurationSeconds}s finished: {_trial.Damage} dmg, {_trial.Hits} hits{(_trial.IsPersonalBest ? ", personal best" : "")}.");
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

        if (!MapIsForBuild(_map, build))
        {
            var dir = Path.GetDirectoryName(_map.SourceFile) ?? _pluginDir;
            var replacement = AddressMap.TryLoad([dir, _pluginDir], build);
            if (replacement is not null && MapIsForBuild(replacement, build))
            {
                _map = replacement;
                _reader = new PartyDamageReader(_map, _moduleBase);
                Log.Info($"MhwDpsMeter: switched to {Path.GetFileName(_map.SourceFile)}.");
            }
        }

        ApplyMap(build);
    }

    public void OnImGuiRender()
    {
        _overlay.DrawSettings(
            _logs,
            BuildDiagnosticsText(),
            DumpDiagnostics,
            _training ? ResetTraining : null,
            _settings,
            _training ? _trial : null,
            _training ? ToggleTrial : null);
    }

    private string BuildDiagnosticsText()
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

        return
            $"Plugin build: {BuildStamp}\n" +
            $"Status: {_status}\n" +
            $"Game build: {_gameBuild}  map: {Path.GetFileName(_map?.SourceFile ?? "none")}{(_mapMatchesBuild ? "" : "  (MISMATCH)")}\n" +
            $"Quest: {_inQuest} id {_questId} state {DescribeQuestState(_questState)}  timer {_timerSource}\n" +
            $"Stage: {(Stage)_stageId} ({_stageId}){(_training ? "  training mode" : "")}\n" +
            $"Damage source: {_damageSource}\n" +
            $"Hit hook: {_hits.Status} calls={_hits.Calls} counted={_hits.Hits} ignored={_hits.Ignored} local={_hits.LocalDamage} last {_hits.LastHit}\n" +
            $"Recorder: {_recorder.HitCount} hits, weapon {_recorder.LocalWeapon ?? "-"}\n" +
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
            "F6 = force debug dump, F7 = reset training damage, F8 = time trial, F9 = this menu, F10 = toggle overlay";
    }

    private void WriteLiveDebug(bool force)
    {
        if (_liveDebug is null || _reader is null)
            return;

        var hooked = _hits.Snapshot();
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
            DamageSource = _damageSource,
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
            OverlayDamage = _snapshot?.SlotDamage ?? new int[PartyDamageReader.PartySlots],
            Slots = _reader.LastSlots
        };
        _liveDebug.Write(snapshot, force);
    }

    private static string FormatSlots(int[]? slots) =>
        slots is { Length: > 0 } ? string.Join(" / ", slots) : "-";

    public void OnImGuiFreeRender()
    {
        _overlay.Draw(
            _snapshot,
            _elapsedSeconds,
            _inQuest || _showResults || _training,
            _training ? TrainingHint : null,
            _training ? _trial : null);
    }

    // ---- SPL quest callbacks -------------------------------------------------------

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

    public void OnQuestComplete(int questId) => EndHunt("complete");

    public void OnQuestFail(int questId) => EndHunt("fail");

    public void OnQuestAbandon(int questId) => EndHunt("abandon");

    public void OnQuestReturn(int questId) => EndHunt("return");

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

        EndHunt(ResultFromQuestState(state));
    }

    // ---- SPL monster / player callbacks (fight-log timeline) --------------------------

    public void OnMonsterEnrage(Monster monster) => RecordMonsterEvent("enrage", monster);

    public void OnMonsterUnenrage(Monster monster) => RecordMonsterEvent("unenrage", monster);

    public void OnMonsterDeath(Monster monster) => RecordMonsterEvent("death", monster);

    public bool OnMonsterFlinch(Monster monster, ref int actionId)
    {
        RecordMonsterEvent("flinch", monster, actionId.ToString());
        return true;
    }

    /// <summary>Remember the local hunter's current action so each hooked hit can be tagged with the move.</summary>
    public void OnPlayerAction(Player player, ref ActionInfo action)
    {
        if (!_inQuest || !_hits.Hooked)
            return;

        try
        {
            if (player.Instance == LocalPlayerInstance())
                _hits.SetCurrentAction(action.ActionSet, action.ActionId);
        }
        catch
        {
            // player wrapper torn down mid-callback
        }
    }

    private void RecordMonsterEvent(string type, Monster monster, string? detail = null)
    {
        if (!_inQuest)
            return;

        try
        {
            _recorder.AddMonsterEvent(_elapsedSeconds, type, monster.Instance, detail);
        }
        catch
        {
            // never let bookkeeping break a game callback
        }
    }

    /// <summary>Looks up the internal action name ("Attack00" style) from the local hunter's action list.</summary>
    private static string? ResolveActionName(int actionSet, int actionId)
    {
        var player = Player.MainPlayer;
        if (player is null || actionId < 0)
            return null;

        var list = player.ActionController.GetActionList(actionSet);
        if (actionId >= list.Count)
            return null;

        return list[actionId]?.Name;
    }

    private static WeaponType? ReadLocalWeapon()
    {
        try
        {
            return Player.MainPlayer?.CurrentWeaponType;
        }
        catch
        {
            return null;
        }
    }

    private static nint LocalPlayerInstance()
    {
        try
        {
            return Player.MainPlayer?.Instance ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---- hunt state machine ----------------------------------------------------------

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

            EndHunt(ResultFromQuestState(questState));
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
            if (IsPlaceholderName(questName))
                questName = Quest.GetQuestName(questId);
            if (IsPlaceholderName(questName))
                questName = "";
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
        _elapsedSeconds = 0;
        _timerSource = "local";
        _snapshot = PartySnapshot.LocalOnly(0, "You", LocalPlayerInstance());
        _pollAccumulator = 0;
        _questTimer.Restart();
        _monsterHp.Reset();
        _hits.Reset();
        _hits.RecordHits = true;
        _recorder.Reset();
        _logs?.BeginHunt();
        _status = $"in quest {questId}";
    }

    private void EndHunt(string result)
    {
        if (!_inQuest)
            return;

        _inQuest = false;
        _finishedQuestId = _questId;
        _finishedAt = DateTimeOffset.UtcNow;
        _questTimer.Stop();
        _elapsedSeconds = Math.Max(_elapsedSeconds, HuntElapsedSeconds());

        try
        {
            if (_reader is not null)
            {
                var snapshot = ReadPartyWithFallbacks();
                if (snapshot is not null)
                    _snapshot = snapshot.WithRates(_elapsedSeconds);
                _recorder.ObserveMonsters(_elapsedSeconds, _monsterHp.LastTracked);
                _recorder.AddHits(_elapsedSeconds, _hits.DrainHits(), _snapshot?.LocalSlot ?? 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: final read failed ({ex.Message}).");
        }

        _hits.RecordHits = false;
        var members = _snapshot?.Members ?? [];
        _logs?.EndHunt(
            new FightLogHeader(
                _questId,
                _questName,
                result,
                _stageId,
                ((Stage)_stageId).ToString(),
                _questStartedAt,
                _elapsedSeconds,
                _timerSource,
                _gameBuild),
            members,
            _recorder);

        _showResults = members.Length > 0;
        if (!_showResults)
            _snapshot = null;
    }

    /// <summary>In-game quest timer when it is running (shared by every hunter), else the plugin's own clock.</summary>
    private float HuntElapsedSeconds()
    {
        try
        {
            var time = Quest.QuestEndTimer.Time;
            if (time is > 0.25f and < 60_000f)
            {
                _timerSource = "quest";
                return time;
            }
        }
        catch
        {
            // fall through to local clock
        }

        _timerSource = "local";
        return (float)_questTimer.Elapsed.TotalSeconds;
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

    /// <summary>The game returns "Unavailable" for arena/challenge quest ids it has no text for.</summary>
    private static bool IsPlaceholderName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Expeditions and the Guiding Lands never allocate the quest-award damage table.</summary>
    private static readonly string[] UnsupportedQuestNameMarkers =
    [
        "Expedition", "Guiding Lands", "Expédition", "Expedición", "Spedizione", "Expedição",
        "探検", "探索", "導きの地", "탐험", "안내하는 땅", "Экспедиция", "探险", "调查地点"
    ];

    private static bool IsUnsupportedHunt(Stage stage, string questName)
    {
        if (stage is Stage.GuidingLands)
            return true;

        return UnsupportedQuestNameMarkers.Any(marker =>
            questName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    // ---- damage merging --------------------------------------------------------------

    /// <summary>
    /// Award table wins once it has real totals (zero slots are still patched from the hook).
    /// Otherwise the live fallbacks fill in: hooked hits per slot, and monster HP lost when solo.
    /// Sets <see cref="_damageSource"/> to say which one the overlay is showing.
    /// </summary>
    private PartySnapshot MergeLiveDamage(PartySnapshot snapshot, int fallbackLocalDamage)
    {
        var hooked = _hits.Snapshot();
        if (snapshot.HasAwardTable && snapshot.TotalDamage > 0)
        {
            _damageSource = _reader!.DamageSource;
            return PartySnapshot.From(snapshot.Members.Select(member =>
            {
                if (member.Damage > 0 || member.Slot is < 0 or >= PartyDamageReader.PartySlots)
                    return member;
                var hookedDamage = hooked[member.Slot];
                return hookedDamage > 0 ? member.With(damage: hookedDamage) : member;
            }), hasAwardTable: true);
        }

        var solo = snapshot.Members.Length == 1;
        var members = snapshot.Members.Select(member =>
        {
            var damage = member.Damage;
            if (member.Slot is >= 0 and < PartyDamageReader.PartySlots)
                damage = Math.Max(damage, hooked[member.Slot]);
            if (member.IsLocal && solo)
                damage = Math.Max(damage, Math.Max(0, fallbackLocalDamage));
            return member.With(damage: damage);
        }).ToArray();

        var tableTotal = snapshot.Members.Sum(member => member.Damage);
        _damageSource = tableTotal > 0 || members.Length == 0
            ? _reader!.DamageSource
            : hooked.Sum() > 0 ? "live hits" : solo ? "monster HP" : "party list";

        return PartySnapshot.From(members, hasAwardTable: false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string moduleName);
}
