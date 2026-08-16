using System.Runtime.InteropServices;
using SharpPluginLoader.Core.Memory;

namespace MhwDpsMeter;

internal static class SafeMemory
{
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const ulong UserSpaceMin = 0x10000;
    private const ulong UserSpaceMax = 0x00007FFFFFFFFFFFul;

    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        if (!IsReadable(address, (uint)Marshal.SizeOf<T>()))
            return false;

        value = MemoryUtil.Read<T>(address);
        return true;
    }

    public static bool TryReadBytes(nint address, int count, out byte[] bytes)
    {
        bytes = [];
        if (count <= 0 || !IsReadable(address, (uint)count))
            return false;

        bytes = MemoryUtil.ReadBytes(address, count);
        return true;
    }

    public static nint Follow(nint address, int[] offsets)
    {
        return Follow(address, offsets, out _);
    }

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

    public static bool IsReadable(nint address, uint size)
    {
        if (!LooksLikeUserPointer(address) || size == 0)
            return false;

        var length = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        if (VirtualQuery(address, out var info, length) == 0)
            return true;

        if (info.State != 0 && info.State != MemCommit)
            return false;

        if ((info.Protect & PageNoAccess) != 0 || (info.Protect & PageGuard) != 0)
            return false;

        // Wine often reports RegionSize 0 for heap pages. The protect/state check is enough.
        if (info.RegionSize == 0)
            return true;

        var start = (nuint)address;
        var end = start + size;
        var regionStart = (nuint)info.BaseAddress;
        var regionEnd = regionStart + (nuint)info.RegionSize;
        return regionEnd <= regionStart || (start >= regionStart && end <= regionEnd);
    }

    public static bool LooksLikeUserPointer(nint address)
    {
        var value = (ulong)address;
        return value >= UserSpaceMin && value <= UserSpaceMax;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
