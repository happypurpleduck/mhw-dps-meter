using System.Text;

namespace MhwDpsMeter;

/// <summary>
/// Bounded, read-only action-name lookup for verified hunters. Layout follows SPL
/// 0.0.7.2's Entity.ActionController, ActionList and Action.Name. No native wrapper
/// dereferences or calls into the supplied object are allowed here.
/// </summary>
internal sealed class HunterActionNames(Func<nint, bool> isHunter)
{
    private const int ControllerOffset = 0x61C8;
    private const int ListsOffset = 0x68;
    private const int ListStride = 0x10;
    private const int MaxActions = 4096;
    private const int MaxNameBytes = 256;

    public string? Read(nint instance, int actionSet, int actionId)
    {
        if (actionSet is < 0 or > 3 || actionId is < 0 or >= MaxActions
            || !SafeMemory.LooksLikeUserPointer(instance) || !isHunter(instance))
            return null;

        var list = instance + ControllerOffset + ListsOffset + actionSet * ListStride;
        if (!SafeMemory.TryReadProtected<nint>(list, out var actions)
            || !SafeMemory.LooksLikeUserPointer(actions)
            || !SafeMemory.TryReadProtected<int>(list + 0x08, out var count)
            || count is <= 0 or > MaxActions || actionId >= count
            || !SafeMemory.TryReadProtected<nint>(actions + actionId * 8, out var action)
            || !SafeMemory.LooksLikeUserPointer(action)
            || !SafeMemory.TryReadProtected<nint>(action + 0x20, out var name)
            || !SafeMemory.LooksLikeUserPointer(name))
            return null;

        Span<byte> text = stackalloc byte[MaxNameBytes];
        for (var length = 0; length < text.Length;)
        {
            var chunk = text.Slice(length, Math.Min(32, text.Length - length));
            if (!SafeMemory.TryReadProtectedBytes(name + length, chunk))
            {
                // A valid short name can end immediately before an unreadable page.
                chunk = chunk[..1];
                if (!SafeMemory.TryReadProtectedBytes(name + length, chunk))
                    return null;
            }
            var terminator = chunk.IndexOf((byte)0);
            if (terminator >= 0)
            {
                var value = Encoding.UTF8.GetString(text[..(length + terminator)]);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            length += chunk.Length;
        }
        return null; // Unterminated or corrupt name: leave the action unnamed.
    }
}
