using System.Runtime.InteropServices;
using SharpPluginLoader.Core.Memory;

namespace MhwDpsMeter;

internal static class SafeMemory
{
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint MaxLenientRead = 64;
    private const ulong UserSpaceMin = 0x10000;
    private const ulong UserSpaceMax = 0x00007FFFFFFFFFFFul;

    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        if (!IsReadable(address, (uint)Marshal.SizeOf<T>()))
            return false;

        try
        {
            value = MemoryUtil.Read<T>(address);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadBytes(nint address, int count, out byte[] bytes)
    {
        bytes = [];
        if (count <= 0 || !IsReadable(address, (uint)count))
            return false;

        try
        {
            bytes = MemoryUtil.ReadBytes(address, count);
            return true;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    public static nint Follow(nint address, int[] offsets) => Follow(address, offsets, out _);

    public static nint Follow(nint address, int[] offsets, out string error)
    {
        var current = address;
        for (var i = 0; i < offsets.Length; i++)
        {
            if (!LooksLikeUserPointer(current))
            {
                error = $"hop {i}/{offsets.Length} bad current 0x{current:X}";
                return 0;
            }

            if (!TryRead<nint>(current, out var next))
            {
                error = $"hop {i}/{offsets.Length} unreadable 0x{current:X}";
                return 0;
            }

            if (next == 0)
            {
                error = i == 0
                    ? $"session not allocated at 0x{current:X}"
                    : $"hop {i}/{offsets.Length} null at 0x{current:X}";
                return 0;
            }

            current = next + offsets[i];
        }

        error = "";
        return current;
    }

    public static nint FollowFromObject(nint obj, int[] offsets, out string error)
    {
        if (offsets.Length == 0)
        {
            error = "";
            return obj;
        }

        var current = obj + offsets[0];
        for (var i = 1; i < offsets.Length; i++)
        {
            if (!LooksLikeUserPointer(current))
            {
                error = $"obj hop {i}/{offsets.Length} bad current 0x{current:X}";
                return 0;
            }

            if (!TryRead<nint>(current, out var next) || next == 0)
            {
                error = $"obj hop {i}/{offsets.Length} null at 0x{current:X}";
                return 0;
            }

            current = next + offsets[i];
        }

        error = "";
        return current;
    }

    public static bool IsReadable(nint address, uint size) => QueryReadable(address, size, requireQuery: false);

    // Far reads (session names at +0x532ED). VirtualQuery must succeed so we
    // do not touch unmapped pages. Wine heap still reports RegionSize 0.
    public static bool IsMapped(nint address, uint size) => QueryReadable(address, size, requireQuery: true);

    private static bool QueryReadable(nint address, uint size, bool requireQuery)
    {
        if (!LooksLikeUserPointer(address) || size == 0)
            return false;

        var length = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        if (VirtualQuery(address, out var info, length) == 0)
            return !requireQuery && size <= MaxLenientRead;

        if (info.State != 0 && info.State != MemCommit)
            return false;

        if ((info.Protect & PageNoAccess) != 0 || (info.Protect & PageGuard) != 0)
            return false;

        if (info.RegionSize == 0)
            return size <= MaxLenientRead;

        var start = (ulong)address;
        var end = start + size;
        var regionStart = info.BaseAddress;
        var regionEnd = regionStart + info.RegionSize;
        if (regionEnd <= regionStart)
            return size <= MaxLenientRead;

        return start >= regionStart && end <= regionEnd;
    }

    public static bool LooksLikeUserPointer(nint address)
    {
        var value = (ulong)address;
        return value >= UserSpaceMin && value <= UserSpaceMax;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    // Matches MEMORY_BASIC_INFORMATION64 (48 bytes). A short struct lets
    // VirtualQuery smash the stack under Wine and abort the poll.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }
}
