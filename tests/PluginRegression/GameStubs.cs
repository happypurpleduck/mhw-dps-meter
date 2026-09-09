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
    internal readonly record struct HitRecord(long Timestamp, nint Target, int Damage,
        bool Crit, bool Tenderized, int AttackId, int ActionSet, int ActionId);

    /// <summary>Regression stub: Poll paths that touch memory are not exercised here.</summary>
    internal static class SafeMemory
    {
        public static nint Follow(nint address, int[] offsets, out string error)
        {
            error = "stub";
            return 0;
        }

        public static bool TryRead<T>(nint address, out T value) where T : unmanaged
        {
            value = default;
            return false;
        }
    }
}
