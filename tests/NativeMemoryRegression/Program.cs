using System.Runtime.InteropServices;
using System.Text;
using MhwDpsMeter;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Run this test on Windows or through Proton's Windows .NET runtime.");
    return 2;
}

var checks = 0;
void Check(bool ok, string scenario)
{
    if (!ok) throw new Exception(scenario);
    checks++;
}

var pageSize = Environment.SystemPageSize;
var pages = Native.VirtualAlloc(0, (nuint)(pageSize * 2), 0x3000, 0x04);
var hunter = Marshal.AllocHGlobal(0x6300);
var actions = Marshal.AllocHGlobal(8);
var action = Marshal.AllocHGlobal(0x28);
Check(pages != 0, "allocate test pages");
try
{
    Marshal.WriteInt64(pages, 123456789);
    Check(SafeMemory.TryReadProtected<long>(pages, out var number) && number == 123456789, "read actual committed memory");
    Check(!SafeMemory.TryReadProtected<long>(0x3251, out number) && number == 0, "reject actual crash address");
    Check(Native.VirtualProtect(pages + pageSize, (nuint)pageSize, 0x01, out _), "protect second page");
    Check(!SafeMemory.TryReadProtected<long>(pages + pageSize, out number) && number == 0, "unreadable page returns failure");
    Check(!SafeMemory.TryReadProtected<long>(pages + pageSize - 4, out number) && number == 0, "partial typed read does not return data");
    Check(!SafeMemory.TryReadProtectedBytes(pages + pageSize - 4, new byte[8]), "copy across unreadable boundary fails");
    Check(!SafeMemory.TryReadProtectedBytes(pages, Span<byte>.Empty), "zero-length copy rejected");

    var list = hunter + 0x61C8 + 0x68;
    Marshal.WriteIntPtr(list, actions);
    Marshal.WriteInt32(list + 8, 1);
    Marshal.WriteIntPtr(actions, action);
    var text = Encoding.UTF8.GetBytes("Common::DIE\0");
    var textAddress = pages + pageSize - text.Length;
    Marshal.Copy(text, 0, textAddress, text.Length);
    Marshal.WriteIntPtr(action + 0x20, textAddress);
    var names = new HunterActionNames(instance => instance == hunter);
    Check(names.Read(hunter, 0, 0) == "Common::DIE", "read hunter action ending at unreadable page boundary");
    Check(names.Read(hunter + 8, 0, 0) is null, "reject non-hunter owner");
    Marshal.WriteIntPtr(actions, 0x3231);
    Check(names.Read(hunter, 0, 0) is null, "exact crash action pointer returns unknown");
    Marshal.WriteIntPtr(actions, pages + pageSize);
    Check(names.Read(hunter, 0, 0) is null, "in-range inaccessible action pointer returns unknown");
    Marshal.WriteIntPtr(actions, action);
    Marshal.WriteIntPtr(action + 0x20, pages + pageSize);
    Check(names.Read(hunter, 0, 0) is null, "inaccessible action string returns unknown");
    Marshal.Copy(Enumerable.Repeat((byte)'A', 256).ToArray(), 0, pages, 256);
    Marshal.WriteIntPtr(action + 0x20, pages);
    Check(names.Read(hunter, 0, 0) is null, "unterminated native string bounded");
    var freed = pages;
    Check(Native.VirtualFree(pages, 0, 0x8000), "release test pages");
    pages = 0;
    Check(!SafeMemory.TryReadProtected<long>(freed, out number), "freed memory returns failure without crashing");
}
finally
{
    if (pages != 0) Native.VirtualFree(pages, 0, 0x8000);
    Marshal.FreeHGlobal(hunter);
    Marshal.FreeHGlobal(actions);
    Marshal.FreeHGlobal(action);
}
Console.WriteLine($"Passed {checks} native memory regression checks.");
return 0;

static class Native
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
