using System.Text.Json;

namespace MhwDpsMeter;

/// <summary>Display labels only; numeric IDs and weapon identity remain unchanged.</summary>
internal static class GameNames
{
    private static readonly JsonDocument Names = JsonDocument.Parse(
        typeof(GameNames).Assembly.GetManifestResourceStream("MhwDpsMeter.GameNames.json")!);

    // Game IDs, independent of the loaded SPL enum: 0.0.7.2 swapped the bowgun names.
    // Corrected upstream in SPL 0.0.9 (Player.cs WeaponType).
    public static string? Weapon(SharpPluginLoader.Core.Entities.WeaponType? weapon) => weapon is null ? null : (int)weapon.Value switch
    {
        0 => "GreatSword", 1 => "SwordAndShield", 2 => "DualBlades", 3 => "LongSword",
        4 => "Hammer", 5 => "HuntingHorn", 6 => "Lance", 7 => "GunLance",
        8 => "SwitchAxe", 9 => "ChargeBlade", 10 => "InsectGlaive", 11 => "Bow",
        12 => "HeavyBowgun", 13 => "LightBowgun", _ => null
    };

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
