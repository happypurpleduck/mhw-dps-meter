using System.Text.Json;
using System.Text.Json.Serialization;
using SharpPluginLoader.Core;

namespace MhwDpsMeter;

/// <summary>User preferences persisted next to the plugin as <c>settings.json</c>.</summary>
internal sealed class PluginSettings
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [JsonIgnore]
    private string _path = "";

    [JsonPropertyName("overlayVisible")]
    public bool OverlayVisible { get; set; } = true;

    [JsonPropertyName("overlayOpacity")]
    public float OverlayOpacity { get; set; } = 0.92f;

    [JsonPropertyName("trialDurationSeconds")]
    public int TrialDurationSeconds { get; set; } = TimeTrial.DefaultDurationSeconds;

    public static PluginSettings Load(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, FileName);
        PluginSettings settings = new();
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(path), JsonOptions) ?? new PluginSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: {FileName} unreadable, using defaults ({ex.Message}).");
        }

        settings._path = path;
        settings.TrialDurationSeconds = Math.Clamp(settings.TrialDurationSeconds, TimeTrial.MinDurationSeconds, TimeTrial.MaxDurationSeconds);
        settings.OverlayOpacity = Math.Clamp(settings.OverlayOpacity, 0.25f, 1f);
        return settings;
    }

    public void Save()
    {
        if (_path.Length == 0)
            return;

        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"MhwDpsMeter: could not save {FileName} ({ex.Message}).");
        }
    }
}
