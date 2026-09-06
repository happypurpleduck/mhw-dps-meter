using System.Text.Json;

namespace MhwDpsMeter;

/// <summary>Display labels only; numeric IDs and weapon identity remain unchanged.</summary>
internal static class GameNames
{
    private static readonly JsonDocument Names = JsonDocument.Parse(
        typeof(GameNames).Assembly.GetManifestResourceStream("MhwDpsMeter.GameNames.json")!);

    public static string Stage(int id) => Lookup("stages", id) ?? $"Stage {id}";
    public static string Monster(int id) => Lookup("monsters", id) ?? $"Monster {id}";

    // Called from quest-end bookkeeping; a table shape problem must degrade to a fallback label.
    private static string? Lookup(string group, int id)
    {
        try
        {
            return Names.RootElement.TryGetProperty(group, out var table)
                   && table.TryGetProperty(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry)
                   && entry.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsPlaceholder([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] string? name) => string.IsNullOrWhiteSpace(name)
        || name.Trim().Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
        || name.Trim().Equals("Unavaliable", StringComparison.OrdinalIgnoreCase);

    // A failed current-quest read must not skip the independent ID lookup.
    public static string Quest(int id, Func<string?> current, Func<int, string?> byId)
    {
        try { var name = current(); if (!IsPlaceholder(name)) return name!; } catch { }
        try { var name = byId(id); if (!IsPlaceholder(name)) return name!; } catch { }
        return $"Quest {id}";
    }
}
