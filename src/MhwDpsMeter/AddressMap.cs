using System.Globalization;

namespace MhwDpsMeter;

internal sealed class AddressMap
{
    private readonly Dictionary<string, nint> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public string SourceFile { get; }

    private AddressMap(string sourceFile)
    {
        SourceFile = sourceFile;
    }

    public nint GetAddress(string key) => _addresses[key];

    public int[] GetOffsets(string key) => _offsets[key];

    public static AddressMap? TryLoad(string pluginDirectory, int filePrivatePart)
    {
        var addressesDir = Path.Combine(pluginDirectory, "Addresses");
        var exact = Path.Combine(addressesDir, $"MonsterHunterWorld.{filePrivatePart}.map");
        if (File.Exists(exact))
            return Parse(exact);

        if (!Directory.Exists(addressesDir))
            return null;

        var fallback = Directory.GetFiles(addressesDir, "MonsterHunterWorld.*.map")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return fallback is null ? null : Parse(fallback);
    }

    public static AddressMap Parse(string path)
    {
        var map = new AddressMap(path);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment].Trim();

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                continue;

            if (parts[0].Equals("Address", StringComparison.OrdinalIgnoreCase))
            {
                map._addresses[parts[1]] = ParseHex(parts[2]);
            }
            else if (parts[0].Equals("Offset", StringComparison.OrdinalIgnoreCase))
            {
                map._offsets[parts[1]] = parts[2]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseHexInt)
                    .ToArray();
            }
        }

        return map;
    }

    private static nint ParseHex(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return (nint)ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static int ParseHexInt(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
