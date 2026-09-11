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
    private readonly PartyActionTracker _party;
    private readonly HunterActionNames _actionNames;
    private readonly TimeTrial _trial;
    /// <summary>Award-table damage per slot at the previous poll; deltas feed teammate move attribution.</summary>
    private int[]? _prevSlotDamage;
    private PluginSettings _settings = new();

    private AddressMap? _map;
    private PartyDamageReader? _reader;
    private CartTracker? _carts;
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
    private long _moduleSize = FallbackModuleSize;
    private nint _hunterVtable;
    // MtObject vtables live in the exe image; used to reject garbage entity pointers.
    private const long FallbackModuleSize = 0x1000_0000;
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
    // Expedition / Guiding Lands quest that was rejected; decided once, not every frame.
    private int _unsupportedQuestId;
    private readonly Dictionary<string, DateTime> _lastCallbackError = [];
    private bool _buildConfirmed;
    private float _buildRetryAccumulator;
    private int _buildRetries;
    // Training area (stage 504). Not a quest: no quest timer, no award table, and the
    // pole/wagon are not large monsters. Damage comes from the deal-damage hook only,
    // the DPS clock starts at the first hit, and F7 resets without leaving the area.
    private bool _training;

    public Plugin()
    {
        _actionNames = new HunterActionNames(IsHunterEntity);
        _recorder = new HuntRecorder(ResolveActionName);
        _party = new PartyActionTracker(ResolveEntityActionName, EntityLooksAlive, IsHunterEntity);
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

        _moduleSize = ReadModuleSize(_moduleBase);

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
        _carts = new CartTracker(_map, _moduleBase);
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

    /// <summary>
    /// SPL calls plugin callbacks with no try/catch of its own, so an exception escaping one
    /// unwinds into the game's frame loop and takes the whole process down. Log it (at most
    /// once per 10 s per callback) and carry on instead.
    /// </summary>
    private void Guarded(string callback, System.Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _status = $"{callback} failed ({ex.GetType().Name})";
            var now = DateTime.UtcNow;
            if (_lastCallbackError.TryGetValue(callback, out var last) && (now - last).TotalSeconds < 10)
                return;
            _lastCallbackError[callback] = now;
            Log.Error($"MhwDpsMeter: {callback} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void OnUpdate(float deltaTime) => Guarded("OnUpdate", () => UpdateCore(deltaTime));

    private void UpdateCore(float deltaTime)
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
            AttributeTeammateDamage(snapshot);
            ObserveCarts(snapshot);
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
    /// Teammates' per-move damage: credit each slot's award-table increase since the last poll
    /// to the action that hunter's entity is performing. Only while the award table drives the
    /// numbers; the solo fallbacks (hook, monster HP) have no per-slot meaning for others.
    /// </summary>
    private void AttributeTeammateDamage(PartySnapshot snapshot)
    {
        _party.SetLocal(LocalPlayerInstance(), snapshot.LocalSlot);
        if (_party.ObserveParty(snapshot.Members))
            _prevSlotDamage = null;
        // Weapon discovery (especially the one-teammate case) does not require an
        // award table. Arena quests may never allocate one; only damage attribution does.
        var deltas = new int[PartyDamageReader.PartySlots];
        if (snapshot.HasAwardTable && _prevSlotDamage is not null)
        {
            for (var slot = 0; slot < deltas.Length; slot++)
                deltas[slot] = snapshot.SlotDamage[slot] - _prevSlotDamage[slot];
        }

        var elapsed = _elapsedSeconds;
        var estimated = _party.Attribute(
            elapsed,
            deltas,
            snapshot.Members,
            _recorder.SingleLiveMonsterId(),
            (slot, how) => _recorder.AddEvent(elapsed, "slotmatch", slot, how));
        _recorder.AddEstimatedHits(estimated);
        _prevSlotDamage = snapshot.HasAwardTable ? (int[])snapshot.SlotDamage.Clone() : null;
        _recorder.ObserveSlotWeapons(_elapsedSeconds, _party.SlotWeapons());
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
        _trial.PreviousBest = _logs?.BestTrial(GameNames.Weapon(ReadLocalWeapon()), _trial.DurationSeconds)?.TotalDamage;
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
        var log = _trial.BuildLog(name, _stageId, GameNames.Stage(_stageId), _gameBuild);
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
                _carts = new CartTracker(_map, _moduleBase);
                Log.Info($"MhwDpsMeter: switched to {Path.GetFileName(_map.SourceFile)}.");
            }
        }

        ApplyMap(build);
    }

    public void OnImGuiRender() => Guarded("OnImGuiRender", RenderSettingsCore);

    private void RenderSettingsCore()
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
            $"Part capture: {_hits.PartDiagnostics}\n" +
            $"Recorder: {_recorder.HitCount} hits ({_recorder.HitCoverage}), weapon {_recorder.LocalWeapon ?? "-"}\n" +
            $"Hunter entities: {_party.Diagnostics()}\n" +
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
            PartCapture = _hits.PartDiagnostics,
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

    public void OnImGuiFreeRender() => Guarded("OnImGuiFreeRender", RenderOverlayCore);

    private void RenderOverlayCore()
    {
        _overlay.Draw(
            _snapshot,
            _elapsedSeconds,
            _inQuest || _showResults || _training,
            _training ? TrainingHint : null,
            _training ? _trial : null,
            _inQuest ? _carts : null);
    }

    // ---- SPL quest callbacks -------------------------------------------------------

    public void OnQuestEnter(int questId) => Guarded("OnQuestEnter", () =>
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
    });

    public void OnQuestComplete(int questId) => Guarded("OnQuestComplete", () => EndHunt("complete"));

    public void OnQuestFail(int questId) => Guarded("OnQuestFail", () => EndHunt("fail"));

    public void OnQuestAbandon(int questId) => Guarded("OnQuestAbandon", () => EndHunt("abandon"));

    public void OnQuestReturn(int questId) => Guarded("OnQuestReturn", () => EndHunt("return"));

    public void OnQuestLeave(int questId) => Guarded("OnQuestLeave", () =>
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
    });

    // ---- SPL monster / player callbacks (fight-log timeline) --------------------------

    public void OnMonsterEnrage(Monster monster) => RecordMonsterEvent("enrage", monster);

    public void OnMonsterUnenrage(Monster monster) => RecordMonsterEvent("unenrage", monster);

    public void OnMonsterDeath(Monster monster) => RecordMonsterEvent("death", monster);

    public bool OnMonsterFlinch(Monster monster, ref int actionId)
    {
        RecordMonsterEvent("flinch", monster, actionId.ToString());
        return true;
    }

    /// <summary>Every hunter's actions (the game runs teammates' action controllers locally too).</summary>
    public void OnEntityAction(Entity entity, ref ActionInfo action)
    {
        if (!_inQuest)
            return;

        try
        {
            _party.OnAction(entity, action);
            NoteDeathAction(entity.Instance, action.ActionSet, action.ActionId);
        }
        catch
        {
            // never let bookkeeping break a game callback
        }
    }

    /// <summary>Remember the local hunter's current action so each hooked hit can be tagged with the move.</summary>
    public void OnPlayerAction(Player player, ref ActionInfo action)
    {
        if (!(_inQuest || _training) || !_hits.Hooked)
            return;

        try
        {
            if (player.Instance == LocalPlayerInstance())
            {
                _hits.SetCurrentAction(action.ActionSet, action.ActionId);
                if (_inQuest)
                    NoteDeathAction(player.Instance, action.ActionSet, action.ActionId);
            }
        }
        catch
        {
            // player wrapper torn down mid-callback
        }
    }

    private void NoteDeathAction(nint instance, int actionSet, int actionId)
    {
        if (_carts is null || instance == 0)
            return;

        // OnEntityAction includes non-hunter owners. Establish a party slot before
        // touching action data; the name reader also validates the hunter's vtable.
        var slot = _party.SlotOf(instance);
        if (slot < 0 && instance == LocalPlayerInstance())
            slot = _snapshot?.LocalSlot ?? -1;
        if (slot < 0)
            return;

        var name = ResolveEntityActionName(instance, actionSet, actionId);
        if (!CartTracker.LooksLikeDeathAction(name))
            return;

        var hunterName = _snapshot?.Members.FirstOrDefault(m => m.Slot == slot)?.Name;
        _carts.NoteDeathAction(slot, hunterName);
    }

    /// <summary>Quest death-counter increments → <c>cart</c> timeline events (and per-player totals).</summary>
    private void ObserveCarts(PartySnapshot snapshot)
    {
        if (_carts is null)
            return;

        var local = snapshot.Members.FirstOrDefault(m => m.IsLocal);
        var carts = _carts.Poll(snapshot.LocalSlot, local?.Name);
        foreach (var cart in carts)
        {
            var name = cart.Name
                ?? (cart.Slot is int slot
                    ? snapshot.Members.FirstOrDefault(m => m.Slot == slot)?.Name
                    : null);
            _recorder.AddCart(_elapsedSeconds, cart.Slot, name);
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
    private string? ResolveActionName(int actionSet, int actionId) =>
        _actionNames.Read(LocalPlayerInstance(), actionSet, actionId);

    /// <summary>Action name for any hunter entity, read from that entity's own action list.</summary>
    private string? ResolveEntityActionName(nint instance, int actionSet, int actionId) =>
        _actionNames.Read(instance, actionSet, actionId);

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

    /// <summary>SizeOfImage from the PE optional header; generous fallback if unreadable.</summary>
    private static long ReadModuleSize(nint moduleBase)
    {
        if (moduleBase == 0
            || !SafeMemory.TryRead<int>(moduleBase + 0x3C, out var lfanew) || lfanew <= 0 || lfanew > 0x1000
            || !SafeMemory.TryRead<uint>(moduleBase + lfanew + 0x50, out var sizeOfImage) || sizeOfImage == 0)
            return FallbackModuleSize;
        return sizeOfImage;
    }

    private bool ModuleContains(nint address) =>
        _moduleBase != 0 && address >= _moduleBase && address < _moduleBase + _moduleSize;

    /// <summary>
    /// Reads an object's vtable pointer through validated memory and checks it points into
    /// the game image. Freed or garbage pointers fail here instead of faulting later.
    /// </summary>
    private bool TryReadVtable(nint instance, out nint vtable) =>
        SafeMemory.TryReadProtected(instance, out vtable) && ModuleContains(vtable);

    /// <summary>
    /// Hunter entity pointers are remembered from past callbacks; a teammate who left may
    /// have been freed. Only touch ones whose vtable pointer still reads sanely.
    /// </summary>
    private bool EntityLooksAlive(nint instance) => TryReadVtable(instance, out _);

    /// <summary>
    /// Every hunter is a uPlayer, so it shares the local player's vtable. Comparing vtables
    /// answers "is this a hunter?" with plain reads, never a call into the object, which is
    /// what crashed the game on 2026-09-06 (Entity.Is -> GetDti on a non-entity owner).
    /// </summary>
    private bool IsHunterEntity(nint instance)
    {
        if (!TryReadVtable(instance, out var vtable))
            return false;

        if (_hunterVtable == 0)
        {
            var local = LocalPlayerInstance();
            if (local == 0 || !TryReadVtable(local, out var localVtable))
                return false;
            _hunterVtable = localVtable;
        }

        return vtable == _hunterVtable;
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
        {
            _finishedQuestId = 0;
            _unsupportedQuestId = 0;
        }

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

        // Expeditions read as InQuest for their whole duration, so SyncQuestState lands here
        // every frame; without this the game's quest-name function ran 60+ times a second.
        if (questId == _unsupportedQuestId)
            return;

        var questName = GameNames.Quest(questId, () => Quest.CurrentQuestName, Quest.GetQuestName);

        if (IsUnsupportedHunt((Stage)_stageId, questName))
        {
            _unsupportedQuestId = questId;
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
        _party.Reset();
        _carts?.Reset();
        _prevSlotDamage = null;
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
                {
                    _snapshot = snapshot.WithRates(_elapsedSeconds);
                    _recorder.ObserveParty(_elapsedSeconds, snapshot.Members);
                    _recorder.ObserveWeapon(_elapsedSeconds, ReadLocalWeapon(), snapshot.LocalSlot);
                }
                _recorder.ObserveMonsters(_elapsedSeconds, _monsterHp.LastTracked);
                _recorder.AddHits(_elapsedSeconds, _hits.DrainHits(), _snapshot?.LocalSlot ?? 0);
                if (snapshot is not null)
                {
                    AttributeTeammateDamage(snapshot);
                    ObserveCarts(snapshot);
                }
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
                GameNames.Stage(_stageId),
                _questStartedAt,
                _elapsedSeconds,
                _timerSource,
                _gameBuild,
                ReadRewards()),
            members,
            _recorder);

        _showResults = members.Length > 0;
        if (!_showResults)
            _snapshot = null;
    }

    /// <summary>Base quest rewards the game exposes; item drops need a hook that does not exist yet.</summary>
    private static FightLogRewards? ReadRewards()
    {
        try
        {
            return new FightLogRewards
            {
                Zenny = (int)Quest.CurrentQuestRewardMoney,
                HunterRankPoints = (int)Quest.CurrentQuestRewardHrp,
                Stars = Quest.CurrentQuestStarcount
            };
        }
        catch
        {
            return null;
        }
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
