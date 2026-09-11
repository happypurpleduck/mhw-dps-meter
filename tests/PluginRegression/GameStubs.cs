namespace SharpPluginLoader.Core.Entities
{
    public enum MonsterType { SmallBarrel, LargeBarrel, TrainingPole, TrainingWagon, Magmacore, Magmacore2, Unavaliable, LargeMonster }
    public enum WeaponType { GreatSword = 0, DualBlades = 2, None = 255 }

    public sealed class Monster
    {
        public static Func<IEnumerable<Monster>> ReadAll = () => [];
        public static IEnumerable<Monster> GetAllMonsters() => ReadAll();
        public nint Instance { get; init; }
        public float MaxHealth { get; init; } = 10000;
        public float Hp { get; set; } = 8000;
        public bool FailHealthRead { get; set; }
        public float Health => FailHealthRead ? throw new InvalidOperationException("Unreadable HP") : Hp;
        public MonsterType Type => MonsterType.LargeMonster;
        public string Name => "Test monster";
        public int Variant => 0;
    }

    public class Entity(nint instance)
    {
        public nint Instance => instance;
        public bool IsPlayer { get; init; } = true;
        public bool Is(string name) => IsPlayer;
    }

    public sealed class Player(nint instance) : Entity(instance)
    {
        public static Player? MainPlayer { get; set; }
        public WeaponType CurrentWeaponType => WeaponType.GreatSword;
        public float Health { get; set; } = 100;
        public float MaxHealth { get; set; } = 100;
    }
}

namespace SharpPluginLoader.Core.Actions
{
    public struct ActionInfo
    {
        public int ActionSet;
        public int ActionId;
    }
}

namespace MhwDpsMeter
{
    internal static class PartyDamageReader { public const int PartySlots = 4; }
    internal static class FightLogStore { public const float SampleIntervalSeconds = 2f; }
    internal static class Plugin { public const string BuildStamp = "regression"; }
    /// <summary>Typed sparse memory for exercising production readers without a game.</summary>
    internal static class SafeMemory
    {
        public static readonly Dictionary<nint, object> Values = [];
        public static bool TryReadProtected<T>(nint address, out T value) where T : unmanaged => TryRead(address, out value);
        public static bool TryReadProtectedBytes(nint address, Span<byte> bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!TryRead<byte>(address + i, out bytes[i]))
                    return false;
            }
            return true;
        }
        public static bool TryReadBytes(nint address, int count, out byte[] bytes)
        {
            bytes = Values.TryGetValue(address, out var found) && found is byte[] data ? data : [];
            return bytes.Length == count;
        }

        public static nint Follow(nint address, int[] offsets, out string error)
        {
            error = "stub";
            return 0;
        }

        public static bool TryRead<T>(nint address, out T value) where T : unmanaged
        {
            if (Values.TryGetValue(address, out var found) && found is T typed)
            {
                value = typed;
                return true;
            }
            value = default;
            return false;
        }

        public static bool LooksLikeUserPointer(nint address) => (ulong)address >= 0x10000;
    }
}

namespace SharpPluginLoader.Core
{
    public static class Log
    {
        public static void Info(string message) { }
        public static void Warn(string message) { }
    }
}

namespace SharpPluginLoader.Core.Memory
{
    // The originals simulate native call nesting, without patching process memory.
    public static class Hook
    {
        public static readonly Dictionary<long, Action<object[]>> Originals = [];
        public static readonly Dictionary<long, Delegate> Detours = [];
        public static Hook<T> Create<T>(long address, T detour) where T : Delegate => new(address, detour);
        public static void Invoke(long address, params object[] args) => Detours[address].DynamicInvoke(args);
        public static void CallOriginal(long address, object[] args) => Originals[address](args);
    }

    public sealed class Hook<T> where T : Delegate
    {
        private readonly long _address;
        private readonly T _detour;
        public T Original { get; }
        public bool IsEnabled { get; private set; }
        public Hook(long address, T detour)
        {
            _address = address;
            _detour = detour;
            var parameters = typeof(T).GetMethod("Invoke")!.GetParameters()
                .Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
            var args = System.Linq.Expressions.Expression.NewArrayInit(typeof(object), parameters
                .Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object))));
            var call = System.Linq.Expressions.Expression.Call(typeof(Hook), nameof(Hook.CallOriginal), null,
                System.Linq.Expressions.Expression.Constant(address), args);
            Original = System.Linq.Expressions.Expression.Lambda<T>(call, parameters).Compile();
        }
        public void Enable() { IsEnabled = true; Hook.Detours[_address] = _detour; }
        public void Disable() { IsEnabled = false; Hook.Detours.Remove(_address); }
    }
}
