using System.Globalization;
using System.Reflection;

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

    public bool TryGetAddress(string key, out nint value) => _addresses.TryGetValue(key, out value);

    public bool TryGetOffsets(string key, out int[] value) => _offsets.TryGetValue(key, out value!);

    public static AddressMap? TryLoad(IEnumerable<string> searchDirectories, int filePrivatePart)
    {
        var dirs = searchDirectories
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var dir in dirs)
        {
            foreach (var folder in new[] { Path.Combine(dir, "Addresses"), dir })
            {
                var exact = Path.Combine(folder, $"MonsterHunterWorld.{filePrivatePart}.map");
                if (File.Exists(exact))
                    return ParseFile(exact);
            }
        }

        foreach (var dir in dirs)
        {
            foreach (var folder in new[] { Path.Combine(dir, "Addresses"), dir })
            {
                if (!Directory.Exists(folder))
                    continue;

                var fallback = Directory.GetFiles(folder, "MonsterHunterWorld.*.map")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (fallback is not null)
                    return ParseFile(fallback);
            }
        }

        return TryLoadEmbedded(filePrivatePart);
    }

    public static AddressMap? TryLoadEmbedded(int filePrivatePart)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        var preferred = names.FirstOrDefault(name =>
            name.EndsWith($"MonsterHunterWorld.{filePrivatePart}.map", StringComparison.OrdinalIgnoreCase));
        var any = preferred ?? names.FirstOrDefault(name =>
            name.EndsWith(".map", StringComparison.OrdinalIgnoreCase));
        if (any is null)
            return null;

        using var stream = assembly.GetManifestResourceStream(any);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return ParseLines(reader.ReadToEnd().Split('\n'), $"embedded:{any}");
    }

    public static AddressMap ParseFile(string path) => ParseLines(File.ReadAllLines(path), path);

    private static AddressMap ParseLines(IEnumerable<string> lines, string source)
    {
        var map = new AddressMap(source);
        foreach (var raw in lines)
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
