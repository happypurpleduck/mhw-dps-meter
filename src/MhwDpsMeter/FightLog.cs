using System.Text.Json.Serialization;

namespace MhwDpsMeter;

internal sealed class FightLogPlayer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("dps")]
    public float Dps { get; set; }

    [JsonPropertyName("percent")]
    public float Percent { get; set; }
}

internal sealed class FightLogSample
{
    [JsonPropertyName("t")]
    public float T { get; set; }

    [JsonPropertyName("damage")]
    public int[] Damage { get; set; } = new int[4];
}

internal sealed class FightLog
{
    [JsonPropertyName("questId")]
    public int QuestId { get; set; }

    [JsonPropertyName("questName")]
    public string QuestName { get; set; } = "";

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("durationSeconds")]
    public float DurationSeconds { get; set; }

    [JsonPropertyName("players")]
    public FightLogPlayer[] Players { get; set; } = [];

    [JsonPropertyName("samples")]
    public FightLogSample[] Samples { get; set; } = [];

    [JsonIgnore]
    public string FileName { get; set; } = "";
}
