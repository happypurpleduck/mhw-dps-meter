using System.Text.Json;
using System.Text.Json.Serialization;
using SharpPluginLoader.Core;

namespace MhwDpsMeter;

internal sealed class LiveDebugSlot
{
    public int Slot { get; init; }
    public string Ptr { get; init; } = "0";
    public bool PtrOk { get; init; }
    public string PartyName { get; init; } = "";
    public string PartyNameHex { get; init; } = "";
    public string SessionName { get; init; } = "";
    public int RawDamage { get; init; }
    public bool Shown { get; init; }
    public string ShownName { get; init; } = "";
    public bool IsLocal { get; init; }
    public string Why { get; init; } = "";
    public long MemberPtr { get; init; }
    /// <summary>First 0x80 bytes of the party-member struct, to find the weapon / entity offsets by comparing hunts.</summary>
    public string StructHex { get; init; } = "";
}

internal sealed class LiveDebugSnapshot
{
    public string At { get; init; } = "";
    public string PluginBuild { get; init; } = Plugin.BuildStamp;
    public bool InQuest { get; init; }
    public int QuestId { get; init; }
    public string QuestName { get; init; } = "";
    public float Elapsed { get; init; }
    public string Status { get; init; } = "";
    public int GameBuild { get; init; }
    public string MapFile { get; init; } = "";
    public string HookStatus { get; init; } = "";
    public int HookHits { get; init; }
    public int HookCalls { get; init; }
    public int HookIgnored { get; init; }
    public string PartCapture { get; init; } = "";
    public string HookLastHit { get; init; } = "";
    public string Monsters { get; init; } = "";
    public string DamageSource { get; init; } = "";
    public string LastError { get; init; } = "";
    public string Layout { get; init; } = "";
    public int PartySize { get; init; }
    public string LocalName { get; init; } = "";
    public string LocalInstance { get; init; } = "0";
    public string PartyArray { get; init; } = "0";
    public string PartyArrayError { get; init; } = "";
    public string DamageBase { get; init; } = "0";
    public string DamageError { get; init; } = "";
    public bool HasPackedTable { get; init; }
    public int FallbackLocalDamage { get; init; }
    public int HookTotal { get; init; }
    public int[] HookSlots { get; init; } = new int[PartyDamageReader.PartySlots];
    public int OverlayMemberCount { get; init; }
    public string OverlayNames { get; init; } = "";
    public int[] OverlayDamage { get; init; } = new int[PartyDamageReader.PartySlots];
    public LiveDebugSlot[] Slots { get; init; } = [];
}

internal sealed class LiveDebugStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>live-debug.log is appended up to 10 times a second during a fight; roll it over past this size.</summary>
    private const long MaxLogBytes = 8 * 1024 * 1024;

    private readonly string _directory;
    private readonly string _latestPath;
    private readonly string _logPath;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private string _lastFingerprint = "";

    public string LatestPath => _latestPath;
    public string LogPath => _logPath;
    public LiveDebugSnapshot? Last { get; private set; }

    public LiveDebugStore(string pluginDirectory)
    {
        _directory = Path.Combine(pluginDirectory, "logs");
        _latestPath = Path.Combine(_directory, "live-debug.json");
        _logPath = Path.Combine(_directory, "live-debug.log");
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: live-debug dir failed ({ex.Message}).");
        }
    }

    /// <summary>Keeps one previous generation (live-debug.1.log) so a long session cannot fill the disk.</summary>
    private void RotateLogIfLarge()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length < MaxLogBytes)
            return;

        File.Move(_logPath, Path.Combine(_directory, "live-debug.1.log"), overwrite: true);
    }

    public void Write(LiveDebugSnapshot snapshot, bool force = false)
    {
        Last = snapshot;
        var now = DateTime.UtcNow;
        var fingerprint =
            $"{snapshot.PartySize}|{snapshot.DamageSource}|{snapshot.Layout}|" +
            string.Join(",", snapshot.Slots.Select(s => $"{s.Shown}:{s.ShownName}:{s.RawDamage}")) +
            $"|{snapshot.OverlayMemberCount}|{snapshot.HookTotal}|{snapshot.HookCalls}|{snapshot.FallbackLocalDamage}|{snapshot.Monsters}";

        var due = force || (now - _lastWriteUtc).TotalSeconds >= 1.0;
        var changed = !string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal);
        if (!due && !changed)
            return;

        _lastWriteUtc = now;
        _lastFingerprint = fingerprint;

        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tmp = _latestPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _latestPath, overwrite: true);

            var line =
                $"{snapshot.At} q={snapshot.QuestId} in={snapshot.InQuest} src={snapshot.DamageSource} " +
                $"party={snapshot.PartySize} layout={snapshot.Layout} " +
                $"raw=[{string.Join(",", snapshot.Slots.Select(s => s.RawDamage))}] " +
                $"shown=[{string.Join(",", snapshot.Slots.Where(s => s.Shown).Select(s => $"{s.Slot}:{s.ShownName}:{s.RawDamage}"))}] " +
                $"hook={snapshot.HookTotal} calls={snapshot.HookCalls} parts=[{snapshot.PartCapture}] hp={snapshot.FallbackLocalDamage} mon={snapshot.Monsters} err={snapshot.LastError}";
            RotateLogIfLarge();
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: live-debug write failed ({ex.Message}).");
        }
    }
}
