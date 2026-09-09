using System.Diagnostics;
using MhwDpsMeter;
using SharpPluginLoader.Core.Actions;
using SharpPluginLoader.Core.Entities;

var checks = 0;
void Equal<T>(T expected, T actual, string scenario)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{scenario}: expected {expected}, got {actual}");
    checks++;
}

Equal("HeavyBowgun", GameNames.Weapon((WeaponType)12), "heavy bowgun game ID despite old SPL enum");
Equal("LightBowgun", GameNames.Weapon((WeaponType)13), "light bowgun game ID despite old SPL enum");
Equal<string?>(null, GameNames.Weapon((WeaponType)99), "invalid weapon stays unknown");

Equal("Secluded Valley", GameNames.Stage(416), "Alatreon arena display name");
Equal("Stage 999", GameNames.Stage(999), "unknown stage retains ID");
Equal("Coral Pukei-Pukei", GameNames.Monster(69), "abbreviated monster fallback");
Equal("Monster 999", GameNames.Monster(999), "unknown monster retains ID");
Equal("Recovered", GameNames.Quest(123, () => throw new Exception(), _ => "Recovered"), "quest lookup retries after exception");
Equal("Quest 123", GameNames.Quest(123, () => " Unavailable ", _ => "Unavaliable"), "quest placeholders retain ID");
Equal("Quête", GameNames.Quest(123, () => "Quête", _ => throw new Exception()), "localized quest preserved");

var monster = new Monster { Instance = 0x10000 };
Monster.ReadAll = () => [monster];
var hp = new MonsterHpTracker();
Equal(2000, hp.Poll(), "initial HP loss");
Monster.ReadAll = () => throw new InvalidOperationException("Unreadable list");
Equal(2000, hp.Poll(), "failed enumeration preserves damage");
Monster.ReadAll = () => [monster];
Equal(2000, hp.Poll(), "recovered enumeration does not double count");
monster.FailHealthRead = true;
Equal(2000, hp.Poll(), "failed individual HP read preserves damage");
Equal(true, hp.LiveInstances.Contains(monster.Instance), "unreadable monster remains a valid hook target");
monster.FailHealthRead = false;
Equal(2000, hp.Poll(), "recovered HP read does not double count");

var other = new Monster { Instance = 0x20000 };
Monster.ReadAll = () => [monster, other];
Equal(4000, hp.Poll(), "two monsters");
IEnumerable<Monster> PartialRead()
{
    yield return monster;
    throw new InvalidOperationException("List tore midway");
}
Monster.ReadAll = PartialRead;
Equal(4000, hp.Poll(), "partial enumeration preserves unseen monster");
Monster.ReadAll = () => [monster, other];
Equal(4000, hp.Poll(), "partial enumeration recovery");
Monster.ReadAll = () => [other];
Equal(4000, hp.Poll(), "real despawn banks damage");
other.Hp = 7000;
Equal(5000, hp.Poll(), "damage after despawn remains cumulative");

PartyMemberSnapshot Member(int slot, string name, nint instance) => new()
{
    Slot = slot, Name = name, Instance = instance, IsLocal = slot == 0
};
var local = Member(0, "Local", 100);
var oldMember = Member(1, "Old", 1000);
var newMember = Member(1, "New", 2000);
var party = new PartyActionTracker((instance, _, _) => $"WP_{instance}");
party.SetLocal(100, 0);
Equal(false, party.ObserveParty([local, oldMember]), "initial roster");
party.OnAction(new Entity(200), new ActionInfo { ActionSet = 1, ActionId = 10 });
List<FightLogHit> Attribute(params PartyMemberSnapshot[] members) =>
    party.Attribute(1, [0, 100, 0, 0], members, null, (_, _) => { });
Equal("WP_200", Attribute(local, oldMember).Single().Action, "initial teammate match");
Equal(false, party.ObserveParty([local, oldMember]), "stable roster preserves matching");
Equal(true, party.ObserveParty([local, newMember]), "slot replacement resets baseline");
Equal(0, Attribute(local, newMember).Count, "replacement cannot inherit stale action");
Equal(0, party.SlotWeapons().Count, "replacement cannot inherit stale weapon");
party.OnAction(new Entity(300), new ActionInfo { ActionSet = 1, ActionId = 20 });
Equal("WP_300", Attribute(local, newMember).Single().Action, "replacement matched after fresh action");
Equal(true, party.ObserveParty([local]), "departure resets matching");
Equal(0, Attribute(local).Count, "departed entity cannot receive deltas");
Equal(true, party.ObserveParty([local, newMember]), "rejoin resets baseline");
party.OnAction(new Entity(300), new ActionInfo { ActionSet = 1, ActionId = 20 });
Equal("WP_300", Attribute(local, newMember).Single().Action, "rejoin can rematch");
Equal(true, party.ObserveParty([local, Member(1, "New", 3000)]), "same-name replacement detected by pointer");
Equal(0, Attribute(local, newMember).Count, "same-name replacement clears old match");

// Two attacking hunters with equal evidence must not be assigned arbitrarily.
var ambiguous = new PartyActionTracker((_, _, id) => id == 10 ? "WP_ATTACK" : "Common::WAIT");
ambiguous.SetLocal(100, 0);
var thirdMember = Member(2, "Third", 3000);
ambiguous.ObserveParty([local, oldMember, thirdMember]);
ambiguous.OnAction(new Entity(200), new ActionInfo { ActionSet = 1, ActionId = 10 });
ambiguous.OnAction(new Entity(300), new ActionInfo { ActionSet = 1, ActionId = 10 });
Equal(0, ambiguous.Attribute(1, [0, 500, 0, 0], [local, oldMember, thirdMember], null, (_, _) => { }).Count,
    "simultaneous attacks cannot choose a weapon by iteration order");
Equal(0, ambiguous.SlotWeapons().Count, "ambiguous weapons remain unknown");
ambiguous.OnAction(new Entity(300), new ActionInfo { ActionSet = 1, ActionId = 20 });
Equal(1, ambiguous.Attribute(2, [0, 1500, 0, 0], [local, oldMember, thirdMember], null, (_, _) => { }).Count,
    "independent damage disambiguates a hunter");
Equal(1, ambiguous.SlotWeapons().Single().Slot, "confident weapon belongs to the correct slot");

var alive = true;
var despawn = new PartyActionTracker((_, _, _) => "WP_ATTACK", _ => alive);
despawn.SetLocal(100, 0);
despawn.ObserveParty([local, oldMember]);
despawn.OnAction(new Entity(200), new ActionInfo { ActionSet = 1, ActionId = 10 });
despawn.Attribute(1, [0, 100, 0, 0], [local, oldMember], null, (_, _) => { });
alive = false;
Equal(0, despawn.Attribute(2, [0, 100, 0, 0], [local, oldMember], null, (_, _) => { }).Count,
    "despawned hunter loses attribution");
Equal(0, despawn.SlotWeapons().Count, "despawned hunter loses weapon");

var noAwards = new PartyActionTracker((_, _, _) => "WP_ATTACK");
noAwards.SetLocal(100, 0);
noAwards.ObserveParty([local, oldMember]);
noAwards.OnAction(new Entity(200), new ActionInfo { ActionSet = 1, ActionId = 10 });
Equal(0, noAwards.Attribute(1, [0, 0, 0, 0], [local, oldMember], null, (_, _) => { }).Count,
    "weapon discovery without awards does not invent damage");
Equal(1, noAwards.SlotWeapons().Single().Slot, "weapon discovery works without award deltas");

var actionName = "WP_GS";
var recorder = new HuntRecorder((_, _) => actionName);
var hit = new HitRecord(Stopwatch.GetTimestamp(), 0, 100, false, false, 1, 1, 1);
recorder.ObserveWeapon(0, WeaponType.GreatSword, 0);
recorder.AddHits(1, [hit], 0);
actionName = "WP_DB";
recorder.ObserveWeapon(2, WeaponType.DualBlades, 0);
recorder.AddHits(3, [hit], 0);
Equal("WP_GS", recorder.Hits()[0].Action, "prior hit retains original weapon action");
Equal("WP_DB", recorder.Hits()[1].Action, "overlapping action ID resolves after weapon swap");
recorder.ObserveParty(0, [local, oldMember]);
recorder.ObserveSlotWeapons(0, [(1, "GreatSword")]);
recorder.ObserveParty(1, [local, newMember]);
Equal<string?>(null, recorder.WeaponOf(1), "new occupant does not inherit recorded weapon");

actionName = "WP_GS";
var trial = new TimeTrial((_, _) => actionName);
trial.Arm(60);
hit = hit with { Timestamp = Stopwatch.GetTimestamp() };
trial.Update([hit], WeaponType.GreatSword);
actionName = "WP_DB";
trial.Update([hit with { Timestamp = Stopwatch.GetTimestamp() }], WeaponType.DualBlades);
var trialLog = trial.BuildLog("Local", 504, "TrainingCamp", 421810);
Equal("WP_GS", trialLog.Hits[0].Action, "trial preserves earlier weapon action");
Equal("WP_DB", trialLog.Hits[1].Action, "trial refreshes weapon before resolving new hits");

Equal(true, CartTracker.LooksLikeDeathAction("Common::DIE"), "die action detected");
Equal(true, CartTracker.LooksLikeDeathAction("Common::DEATH"), "death action detected");
Equal(true, CartTracker.LooksLikeDeathAction("Player_Faint"), "faint action detected");
Equal(false, CartTracker.LooksLikeDeathAction("Common::IDLE"), "idle is not death");
Equal(false, CartTracker.LooksLikeDeathAction("DIESEL"), "token must be bounded");
Equal(false, CartTracker.LooksLikeDeathAction(null), "null action is not death");

var cartRecorder = new HuntRecorder((_, _) => null);
cartRecorder.AddCart(12.5f, 0, "Local");
cartRecorder.AddCart(40f, null, null);
cartRecorder.AddCart(55f, 1, "Teammate");
Equal(1, cartRecorder.CartsOf(0), "local cart counted");
Equal(1, cartRecorder.CartsOf(1), "teammate cart counted");
Equal(0, cartRecorder.CartsOf(2), "untouched slot has zero carts");
Equal(3, cartRecorder.Events().Count(e => e.Type == "cart"), "all carts appear as events");
Equal<int?>(null, cartRecorder.Events().First(e => e.Type == "cart" && Math.Abs(e.T - 40f) < 0.01f).Slot, "unattributed cart keeps null slot");

Equal(true, AddressMap.TryLoadEmbedded(421810)!.TryGetOffsets("QUEST_EXTRA_DATA_OFFSETS", out var deathOffsets)
    && deathOffsets is [0x17370], "421810 map has quest death extras");
Equal(true, AddressMap.TryLoadEmbedded(421631)!.TryGetOffsets("QUEST_DEATH_COUNTER_OFFSETS", out var deathOnly)
    && deathOnly is [0x17374], "421631 map has death counter offset");

var directory = Directory.CreateTempSubdirectory("mhw-map-regression-");
try
{
    File.WriteAllText(Path.Combine(directory.FullName, "MonsterHunterWorld.421631.map"), "Address TEST 1");
    var map = AddressMap.TryLoad([directory.FullName], 421810)!;
    Equal(true, map.SourceFile.StartsWith("embedded:"), "exact embedded map beats stale external map");
    Equal(true, map.SourceFile.EndsWith("421810.map"), "embedded map matches build");
    var exact = Path.Combine(directory.FullName, "MonsterHunterWorld.421810.map");
    File.WriteAllText(exact, "Address TEST 2");
    Equal(exact, AddressMap.TryLoad([directory.FullName], 421810)!.SourceFile, "external exact map remains preferred");
    Equal(exact, AddressMap.TryLoad([directory.FullName], 999999)!.SourceFile, "unsupported build still uses external fallback");
}
finally
{
    directory.Delete(recursive: true);
}

Console.WriteLine($"Passed {checks} plugin regression checks.");
