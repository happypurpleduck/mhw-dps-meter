using System.Text.Json;

namespace MhwDpsMeter;

/// <summary>Display names for monster part ids (HunterPie MonsterData-derived table).</summary>
internal static class MonsterParts
{
    private static readonly JsonDocument Table = JsonDocument.Parse(
        typeof(MonsterParts).Assembly.GetManifestResourceStream("MhwDpsMeter.MonsterParts.json")!);

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
}
