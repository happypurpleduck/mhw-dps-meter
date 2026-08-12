using System.Runtime.InteropServices;
using SharpPluginLoader.Core.Memory;

namespace MhwDpsMeter;

internal static class SafeMemory
{
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

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
        var current = address;
        foreach (var offset in offsets)
        {
            if (!TryRead<nint>(current, out var next) || next == 0)
                return 0;

            current = next + offset;
        }

        return current;
    }

    public static bool IsReadable(nint address, uint size)
    {
        if (address == 0 || size == 0)
            return false;

        if (VirtualQuery(address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            return false;

        if (info.State != MemCommit)
            return false;

        if ((info.Protect & PageNoAccess) != 0 || (info.Protect & PageGuard) != 0)
            return false;

        var start = (nuint)address;
        var end = start + size;
        var regionStart = (nuint)info.BaseAddress;
        var regionEnd = regionStart + (nuint)info.RegionSize;
        return start >= regionStart && end <= regionEnd;
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
