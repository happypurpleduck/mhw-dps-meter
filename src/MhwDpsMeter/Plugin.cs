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
    private PartySnapshot? _snapshot;
    private bool _inQuest;
    private int _questId;
    private string _questName = "";
    private DateTimeOffset _questStartedAt;
    private float _pollAccumulator;
    private string? _pendingResult;

    public PluginData Initialize()
    {
        return new PluginData
        {
            ImGuiWrappedInTreeNode = true
        };
    }

    public void OnLoad()
    {
        var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;
        var moduleBase = PartyDamageReader.GetModuleHandle(null);
        if (moduleBase == 0)
        {
            Log.Error("MhwDpsMeter: could not resolve MonsterHunterWorld.exe base address.");
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

        _map = AddressMap.TryLoad(pluginDir, version);
        if (_map is null)
        {
            Log.Error($"MhwDpsMeter: no address map for file version {version}. Overlay disabled.");
            return;
        }

        Log.Info($"MhwDpsMeter: loaded {_map.SourceFile} (version {version}).");
        _reader = new PartyDamageReader(_map, moduleBase);
        _logs = new FightLogStore(pluginDir);
    }

    public void OnUpdate(float deltaTime)
    {
        if (Input.IsPressed(Key.F10))
            _overlay.Visible = !_overlay.Visible;

        if (!_inQuest || _reader is null)
            return;

        _pollAccumulator += deltaTime;
        if (_pollAccumulator < PollIntervalSeconds)
            return;

        _pollAccumulator = 0;
        if (!_reader.TryRead(out var snapshot))
            return;

        _snapshot = WithRates(snapshot, (float)_questTimer.Elapsed.TotalSeconds);
        _logs?.UpdateSamples((float)_questTimer.Elapsed.TotalSeconds, snapshot.SlotDamage);
    }

    public void OnImGuiRender()
    {
        if (_logs is null)
        {
            ImGuiNET.ImGui.TextDisabled("Address map not loaded.");
            return;
        }

        _overlay.DrawSettings(_logs);
    }

    public void OnImGuiFreeRender()
    {
        _overlay.Draw(_snapshot, (float)_questTimer.Elapsed.TotalSeconds, _inQuest);
    }

    public void OnQuestEnter(int questId) => BeginHunt(questId, "in-progress");

    public void OnQuestComplete(int questId) => EndHunt(questId, "complete");

    public void OnQuestFail(int questId) => EndHunt(questId, "fail");

    public void OnQuestAbandon(int questId) => EndHunt(questId, "abandon");

    public void OnQuestReturn(int questId) => EndHunt(questId, "return");

    public void OnQuestLeave(int questId) => EndHunt(questId, _pendingResult ?? "leave");

    private void BeginHunt(int questId, string _)
    {
        if (questId <= 0)
            return;

        var questName = Quest.CurrentQuestName;
        if (questName.Contains("Expedition", StringComparison.OrdinalIgnoreCase)
            || questName.Contains("Guiding Lands", StringComparison.OrdinalIgnoreCase))
        {
            _inQuest = false;
            _snapshot = null;
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
        _logs?.BeginHunt();
    }

    private void EndHunt(int questId, string result)
    {
        if (!_inQuest)
            return;

        _pendingResult = result;
        _inQuest = false;
        _questTimer.Stop();

        if (_reader is not null && _reader.TryRead(out var snapshot))
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
