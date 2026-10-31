using System.Text.Json;

namespace MhwDpsMeter;

/// <summary>Display names for monster part ids (HunterPie MonsterData-derived table).</summary>
internal static class MonsterParts
{
    private static readonly JsonDocument Table = JsonDocument.Parse(
        typeof(MonsterParts).Assembly.GetManifestResourceStream("MhwDpsMeter.MonsterParts.json")!);

    // Entries preserve MonsterData.xml order; normal meters are laid out in that
    // order, while severable meters use a separate array and id namespace.
    private static readonly Dictionary<int, int[]> NormalPartIds = Table.RootElement
        .GetProperty("monsters").EnumerateObject().ToDictionary(
            monster => int.Parse(monster.Name, System.Globalization.CultureInfo.InvariantCulture),
            monster => monster.Value.EnumerateObject()
                .Where(part => !part.Value.GetProperty("severable").GetBoolean())
                .Select(part => int.Parse(part.Name, System.Globalization.CultureInfo.InvariantCulture)).ToArray());

    public static int? FromNormalSlot(int monsterType, int slot) =>
        NormalPartIds.TryGetValue(monsterType, out var ids) && slot >= 0 && slot < ids.Length
            ? ids[slot] : null;

    public static string? Name(int monsterType, int partId)
    {
        try
        {
            if (!Table.RootElement.TryGetProperty("monsters", out var monsters))
                return null;
            if (!monsters.TryGetProperty(monsterType.ToString(System.Globalization.CultureInfo.InvariantCulture), out var parts))
                return null;
            if (!parts.TryGetProperty(partId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry))
                return null;
            return entry.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string Display(int monsterType, int partId) =>
        Name(monsterType, partId) ?? $"Part {partId}";

    /// <summary>Metadata from the same canonical part ids as collision capture.</summary>
    internal readonly record struct Definition(
        int Id, string Name, bool Severable, int[] BreakThresholds, int[] TenderizeIds);

    private static readonly Dictionary<int, Definition[]> Definitions = Table.RootElement
        .GetProperty("monsters").EnumerateObject().ToDictionary(
            m => int.Parse(m.Name, System.Globalization.CultureInfo.InvariantCulture),
            m => m.Value.EnumerateObject().Select(p => new Definition(
                int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
                p.Value.GetProperty("name").GetString() ?? $"Part {p.Name}",
                p.Value.GetProperty("severable").GetBoolean(),
                ReadIds(p.Value, "breakThresholds"),
                ReadIds(p.Value, "tenderizeIds"))).ToArray());

    private static int[] ReadIds(JsonElement data, string name) =>
        data.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(x => x.GetInt32()).ToArray()
            : [];

    public static IReadOnlyList<Definition> NormalDefinitions(int monsterType) =>
        Definitions.TryGetValue(monsterType, out var parts)
            ? parts.Where(x => !x.Severable).ToArray()
            : [];

    public static int? NextBreakThreshold(int monsterType, int partId, int completedFlinches)
    {
        if (!Definitions.TryGetValue(monsterType, out var parts))
            return null;
        foreach (var part in parts)
        {
            if (part.Id != partId)
                continue;
            foreach (var threshold in part.BreakThresholds)
                if (threshold > completedFlinches)
                    return threshold;
            return null;
        }
        return null;
    }
}
