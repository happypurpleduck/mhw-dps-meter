using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SharpPluginLoader.Core.Memory;

namespace MhwDpsMeter;

/// <summary>
/// Resolves the game build number (e.g. 421810), which is what HunterPie keys its
/// address maps by. The exe's version resource is 1.0.0.0, so it cannot be used.
/// The build is stored as a standalone ASCII string ("421810\0") in .rdata and is
/// formatted into the window title "MONSTER HUNTER: WORLD(%s)" at runtime.
/// </summary>
internal static partial class GameBuild
{
    public const int Fallback = 421810;

    [GeneratedRegex(@"MONSTER HUNTER: WORLD\((\d{5,7})\)")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"\x00(4\d{5})\x00")]
    private static partial Regex RdataRegex();

    /// <summary>
    /// Detect at plugin load. <paramref name="confirmed"/> is false when the value is
    /// only a guess (no map should be trusted blindly, and no hook installed).
    /// </summary>
    public static int Detect(nint moduleBase, out string source, out bool confirmed)
    {
        var fromTitle = TryFromWindowTitle();
        if (fromTitle > 0)
        {
            source = "window title";
            confirmed = true;
            return fromTitle;
        }

        var fromRdata = TryFromRdata(moduleBase);
        if (fromRdata > 0)
        {
            source = ".rdata build string";
            confirmed = true;
            return fromRdata;
        }

        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (info.FilePrivatePart > 1000)
                {
                    source = "exe FilePrivatePart";
                    confirmed = true;
                    return info.FilePrivatePart;
                }
            }
        }
        catch
        {
            // fall through
        }

        source = "unknown, assumed";
        confirmed = false;
        return Fallback;
    }

    /// <summary>Read "MONSTER HUNTER: WORLD(421810)" from this process's top-level window.</summary>
    public static int TryFromWindowTitle()
    {
        var pid = (uint)Environment.ProcessId;
        var found = 0;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != pid)
                    return true;

                var length = GetWindowTextLengthW(hwnd);
                if (length <= 0 || length > 256)
                    return true;

                var buffer = new StringBuilder(length + 1);
                if (GetWindowTextW(hwnd, buffer, buffer.Capacity) <= 0)
                    return true;

                var match = TitleRegex().Match(buffer.ToString());
                if (match.Success && int.TryParse(match.Groups[1].Value, out var build))
                {
                    found = build;
                    return false;
                }

                return true;
            }, 0);
        }
        catch
        {
            // user32 unavailable / window not created yet
        }

        return found;
    }

    /// <summary>Scan the mapped .rdata section for a unique standalone "4xxxxx" string.</summary>
    public static int TryFromRdata(nint moduleBase)
    {
        try
        {
            if (moduleBase == 0 || !SafeMemory.TryRead<ushort>(moduleBase, out var mz) || mz != 0x5A4D)
                return 0;
            if (!SafeMemory.TryRead<int>(moduleBase + 0x3C, out var lfanew) || lfanew <= 0 || lfanew > 0x1000)
                return 0;

            var nt = moduleBase + lfanew;
            if (!SafeMemory.TryRead<uint>(nt, out var sig) || sig != 0x00004550)
                return 0;
            if (!SafeMemory.TryRead<ushort>(nt + 6, out var sectionCount)
                || !SafeMemory.TryRead<ushort>(nt + 20, out var optionalSize))
                return 0;

            var section = nt + 24 + optionalSize;
            var candidates = new HashSet<int>();
            for (var i = 0; i < sectionCount && i < 32; i++, section += 40)
            {
                if (!SafeMemory.TryReadBytes(section, 8, out var nameBytes))
                    continue;
                var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                if (name != ".rdata")
                    continue;

                if (!SafeMemory.TryRead<uint>(section + 8, out var virtualSize)
                    || !SafeMemory.TryRead<uint>(section + 12, out var virtualAddress))
                    continue;

                ScanRange(moduleBase + (nint)virtualAddress, virtualSize, candidates);
            }

            return candidates.Count == 1 ? candidates.First() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void ScanRange(nint start, uint size, HashSet<int> candidates)
    {
        const int chunk = 1 << 20;
        const int overlap = 16;
        var offset = 0L;
        while (offset < size)
        {
            var length = (int)Math.Min(chunk, size - offset);
            byte[] bytes;
            try
            {
                bytes = MemoryUtil.ReadBytes(start + (nint)offset, length);
            }
            catch
            {
                offset += chunk - overlap;
                continue;
            }

            // Latin-1 keeps one char per byte so regex offsets line up.
            var text = Encoding.Latin1.GetString(bytes);
            foreach (Match match in RdataRegex().Matches(text))
            {
                if (int.TryParse(match.Groups[1].Value, out var build))
                    candidates.Add(build);
            }

            if (length < chunk)
                break;
            offset += chunk - overlap;
        }
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);
}
