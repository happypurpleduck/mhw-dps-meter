using System.Text.Json.Serialization;

namespace MhwDpsMeter;

/// <summary>
/// On-disk fight log. Schema 2 is a superset of the original (schema-less) files:
/// every old top-level field keeps its name, so old logs still deserialize and a
/// web viewer needs a single parser. New fields default to empty when absent.
/// </summary>
internal sealed class FightLog
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("pluginVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginVersion { get; set; }

    [JsonPropertyName("gameBuild")]
    public int GameBuild { get; set; }

    [JsonPropertyName("questId")]
    public int QuestId { get; set; }

    [JsonPropertyName("questName")]
    public string QuestName { get; set; } = "";

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("stageId")]
    public int StageId { get; set; }

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("endedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndedAt { get; set; }

    [JsonPropertyName("durationSeconds")]
    public float DurationSeconds { get; set; }

    /// <summary>"quest" when the in-game quest timer was used, "local" for the plugin's own clock.</summary>
    [JsonPropertyName("timerSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimerSource { get; set; }

    /// <summary>
    /// "local": <see cref="Hits"/> only contain the local hunter's hits (the game never runs
    /// the deal-damage function for other hunters). Other players only have totals + samples.
    /// </summary>
    [JsonPropertyName("hitCoverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HitCoverage { get; set; }

    [JsonPropertyName("players")]
    public FightLogPlayer[] Players { get; set; } = [];

    [JsonPropertyName("monsters")]
    public FightLogMonster[] Monsters { get; set; } = [];

    /// <summary>Party damage every <see cref="FightLogStore.SampleIntervalSeconds"/>, indexed by slot.</summary>
    [JsonPropertyName("samples")]
    public FightLogSample[] Samples { get; set; } = [];

    [JsonPropertyName("hits")]
    public FightLogHit[] Hits { get; set; } = [];

    [JsonPropertyName("events")]
    public FightLogEvent[] Events { get; set; } = [];

    [JsonIgnore]
    public string FileName { get; set; } = "";
}

internal sealed class FightLogPlayer
{
    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("isLocal")]
    public bool IsLocal { get; set; }

    /// <summary>Weapon type name (SharpPluginLoader <c>WeaponType</c>); only known for the local hunter.</summary>
    [JsonPropertyName("weapon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Weapon { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("dps")]
    public float Dps { get; set; }

    [JsonPropertyName("percent")]
    public float Percent { get; set; }
}

internal sealed class FightLogMonster
{
    /// <summary>Stable key used by hits and events ("m1", "m2", ...), in order of first sighting.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("variant")]
    public int Variant { get; set; }

    [JsonPropertyName("maxHealth")]
    public float MaxHealth { get; set; }

    [JsonPropertyName("lastHealth")]
    public float LastHealth { get; set; }

    [JsonPropertyName("firstSeenT")]
    public float FirstSeenT { get; set; }

    [JsonPropertyName("diedT")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? DiedT { get; set; }
}

internal sealed class FightLogSample
{
    [JsonPropertyName("t")]
    public float T { get; set; }

    [JsonPropertyName("damage")]
    public int[] Damage { get; set; } = new int[PartyDamageReader.PartySlots];
}

/// <summary>One hit from the deal-damage hook. Local hunter only.</summary>
internal sealed class FightLogHit
{
    [JsonPropertyName("t")]
    public float T { get; set; }

    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    /// <summary><see cref="FightLogMonster.Id"/> of the target, or null for untracked targets.</summary>
    [JsonPropertyName("monster")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Monster { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("crit")]
    public bool Crit { get; set; }

    [JsonPropertyName("tenderized")]
    public bool Tenderized { get; set; }

    /// <summary>Attack parameter id the game passes to the damage function.</summary>
    [JsonPropertyName("attackId")]
    public int AttackId { get; set; }

    /// <summary>Action set / id the local hunter was performing when the hit landed.</summary>
    [JsonPropertyName("actionSet")]
    public int ActionSet { get; set; }

    [JsonPropertyName("actionId")]
    public int ActionId { get; set; }

    /// <summary>Internal action name from the game's action list, when resolvable.</summary>
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }
}

/// <summary>
/// Timeline event. Types: enrage, unenrage, death, flinch (detail = flinch action id),
/// weapon (detail = weapon type name), join, leave (detail = hunter name).
/// </summary>
internal sealed class FightLogEvent
{
    [JsonPropertyName("t")]
    public float T { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("slot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Slot { get; set; }

    [JsonPropertyName("monster")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Monster { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

/// <summary>One row of <c>logs/index.json</c>: enough to list hunts without opening each file.</summary>
internal sealed class FightLogIndexEntry
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

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
    public string[] Players { get; set; } = [];

    [JsonPropertyName("monsters")]
    public string[] Monsters { get; set; } = [];

    public static FightLogIndexEntry From(FightLog log) => new()
    {
        File = log.FileName,
        QuestId = log.QuestId,
        QuestName = log.QuestName,
        Result = log.Result,
        StartedAt = log.StartedAt,
        DurationSeconds = log.DurationSeconds,
        Players = log.Players.Select(player => player.Name).ToArray(),
        Monsters = log.Monsters.Select(monster => monster.Name).Distinct().ToArray()
    };
}
