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
var hit = new HitRecord(Stopwatch.GetTimestamp(), 0, 100, false, false, 1, 1, 1, null);
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

// Quest-load crash: a non-hunter action owner yielded action pointer 0x3231;
// Action.Name then dereferenced 0x3251 and terminated the game outside managed catches.
nint hunterEntity = 0x200000, actionArray = 0x300000, actionObject = 0x400000, actionText = 0x500000;
var actionNames = new HunterActionNames(instance => instance == hunterEntity);
void SetActionMemory(int set = 0, string name = "Common::DIE")
{
    SafeMemory.Values.Clear();
    var list = hunterEntity + 0x61C8 + 0x68 + set * 0x10;
    SafeMemory.Values[list] = actionArray;
    SafeMemory.Values[list + 8] = 54;
    SafeMemory.Values[actionArray + 53 * 8] = actionObject;
    SafeMemory.Values[actionObject + 0x20] = actionText;
    var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
    for (var i = 0; i < bytes.Length; i++) SafeMemory.Values[actionText + i] = bytes[i];
}
SetActionMemory();
Equal("Common::DIE", actionNames.Read(hunterEntity, 0, 53), "valid hunter death action preserves cart detection");
Equal<string?>(null, actionNames.Read(hunterEntity + 1, 0, 53), "non-hunter callback owner rejected");
Equal<string?>(null, actionNames.Read(0, 0, 53), "null action owner rejected");
Equal<string?>(null, actionNames.Read(hunterEntity, -1, 53), "negative action set rejected");
Equal<string?>(null, actionNames.Read(hunterEntity, 4, 53), "action set past final list rejected");
Equal<string?>(null, actionNames.Read(hunterEntity, 0, -1), "negative action id rejected");
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 54), "action id at count rejected");
SafeMemory.Values[actionArray + 53 * 8] = (nint)0x3231;
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 53), "actual crash action pointer skipped safely");
SetActionMemory();
SafeMemory.Values[actionObject + 0x20] = (nint)0x700000;
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 53), "unreadable action name rejected");
SetActionMemory();
SafeMemory.Values[hunterEntity + 0x61C8 + 0x68 + 8] = int.MaxValue;
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 53), "corrupt action count bounded");
SetActionMemory();
SafeMemory.Values.Remove(actionArray + 53 * 8);
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 53), "torn action list skipped");
SetActionMemory(3, "WP_02::RANBU");
Equal("WP_02::RANBU", actionNames.Read(hunterEntity, 3, 53), "last supported action set and weapon name preserved");
SetActionMemory(name: new string('A', 256));
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 53), "unterminated name bounded to 256 bytes");
SetActionMemory(name: "");
Equal<string?>(null, actionNames.Read(hunterEntity, 0, 53), "empty name remains unknown");

var cartRecorder = new HuntRecorder((_, _) => null);
cartRecorder.AddCart(12.5f, 0, "Local");
cartRecorder.AddCart(40f, null, null);
cartRecorder.AddCart(55f, 1, "Teammate");
Equal(1, cartRecorder.CartsOf(0), "local cart counted");
Equal(1, cartRecorder.CartsOf(1), "teammate cart counted");
Equal(0, cartRecorder.CartsOf(2), "untouched slot has zero carts");
Equal(3, cartRecorder.Events().Count(e => e.Type == "cart"), "all carts appear as events");
Equal<int?>(null, cartRecorder.Events().First(e => e.Type == "cart" && Math.Abs(e.T - 40f) < 0.01f).Slot, "unattributed cart keeps null slot");

Equal("Head", MonsterParts.Display(0, 2), "Anjanath head part name");
Equal("Part 99", MonsterParts.Display(0, 99), "unknown part retains id");

// The game's damage-number callback leaves meters unchanged. Parts must come from
// the synchronous caller context, including slot zero and post-break remapping.
Equal(0, MonsterParts.FromNormalSlot(30, 0), "Tobi head uses collision slot zero");
Equal(2, MonsterParts.FromNormalSlot(0, 0), "Anjanath normal slot skips severable meters");
Equal(6, MonsterParts.FromNormalSlot(0, 4), "Anjanath tail canonical id");
Equal<int?>(null, MonsterParts.FromNormalSlot(0, 15), "missing part mapping stays unknown");

nint controller = 0x100000, target = 0x200000, hitData = 0x300000, collision = 0x400000;
void SetupPart(int type, int slot, int state = 0)
{
    SafeMemory.Values.Clear();
    SafeMemory.Values[controller + 0x08] = target;
    SafeMemory.Values[hitData + 0x28] = collision;
    SafeMemory.Values[collision + 0x60] = slot;
    SafeMemory.Values[controller + slot * 0x1F8 + 0x208] = state;
    SafeMemory.Values[target + 0x12280] = type;
}
SetupPart(30, 0);
var context = MonsterPartResolver.Capture(controller, hitData);
Equal(0, context.Match(target, hitData + 0xE0), "unchanged meters still tag Tobi head");
Equal<int?>(null, context.Match(target + 1, hitData + 0xE0), "another target cannot inherit context");
Equal<int?>(null, context.Match(target, hitData + 0xE4), "another position cannot inherit context");
SetupPart(0, 0, 1);
SafeMemory.Values[collision + 0x80] = 4;
Equal(6, MonsterPartResolver.Capture(controller, hitData).Part, "broken part uses alternate collision slot");
SafeMemory.Values[collision + 0x80] = -1;
Equal<int?>(null, MonsterPartResolver.Capture(controller, hitData).Part, "invalid alternate is not guessed");
SetupPart(30, 16);
Equal<int?>(null, MonsterPartResolver.Capture(controller, hitData).Part, "out-of-range collision rejected");
SetupPart(999, 0);
Equal<int?>(null, MonsterPartResolver.Capture(controller, hitData).Part, "unknown monster mapping stays unknown");
SafeMemory.Values.Clear();
Equal<int?>(null, MonsterPartResolver.Capture(controller, hitData).Part, "unreadable context stays unknown");

// Exercise both production detours with a native original that emits two damage
// numbers without changing any part meter. Nested calls must restore their scope.
const long numberAddress = 0x141CC5F80, contextAddress = 0x1402C3030;
byte[] contextPrologue = [0x40, 0x55, 0x56, 0x41, 0x55, 0x48, 0x81, 0xEC,
    0x80, 0, 0, 0, 0x48, 0x8B, 0xF2, 0x4C, 0x8B, 0xE9];
void Emit(nint position) => SharpPluginLoader.Core.Memory.Hook.Invoke(numberAddress,
    target, 25, position, 0, 0, 0, 0, (byte)0, 0);
SetupPart(30, 0);
SafeMemory.Values[new nint(contextAddress)] = contextPrologue;
SharpPluginLoader.Core.Memory.Hook.Originals[numberAddress] = _ => { };
SharpPluginLoader.Core.Memory.Hook.Originals[contextAddress] = args =>
{
    var data = (nint)args[1];
    if (data == hitData)
    {
        // An unreadable nested context must not borrow the outer head tag.
        SharpPluginLoader.Core.Memory.Hook.Invoke(contextAddress, controller, hitData + 0x1000, (nint)0);
    }
    Emit(data + 0xE0);
};
using (var damage = new DamageTracker())
{
    damage.UpdateMonsters([target]);
    damage.RecordHits = true;
    damage.Install(new nint(0x140000000), AddressMap.TryLoadEmbedded(421810)!);
    Equal(true, damage.PartStatus.StartsWith("collision hook"), "verified part hook installed");
    SharpPluginLoader.Core.Memory.Hook.Invoke(contextAddress, controller, hitData, (nint)0);
    var recorded = damage.DrainHits();
    Equal(2, recorded.Count, "nested native calls retain both damage numbers");
    Equal<int?>(null, recorded[0].Part, "unreadable nested hit stays unknown");
    Equal(0, recorded[1].Part, "outer hit retains head after nested call");
    Emit(hitData + 0xE0);
    Equal<int?>(null, damage.DrainHits().Single().Part, "expired context cannot tag later damage");
    Equal(75, damage.LocalDamage, "part capture preserves total damage");
    Equal(true, damage.PartDiagnostics.Contains("tagged=1 unknown=2"), "capture coverage is observable");
    damage.Reset();
    Equal(true, damage.PartDiagnostics.Contains("tagged=0 unknown=0"), "hunt reset clears capture counts");

    var nativeOriginal = SharpPluginLoader.Core.Memory.Hook.Originals[contextAddress];
    SharpPluginLoader.Core.Memory.Hook.Originals[contextAddress] = _ => throw new InvalidOperationException("native failure");
    try { SharpPluginLoader.Core.Memory.Hook.Invoke(contextAddress, controller, hitData, (nint)0); }
    catch (System.Reflection.TargetInvocationException) { }
    Emit(hitData + 0xE0);
    Equal<int?>(null, damage.DrainHits().Single().Part, "exception unwinds hit context");
    SharpPluginLoader.Core.Memory.Hook.Originals[contextAddress] = nativeOriginal;

    damage.AcceptAllTargets = true;
    SharpPluginLoader.Core.Memory.Hook.Invoke(contextAddress, controller, hitData, (nint)0);
    Equal(true, damage.DrainHits().All(h => h.Part is null), "training does not read monster parts");
}
Equal(false, SharpPluginLoader.Core.Memory.Hook.Detours.ContainsKey(contextAddress), "unload disables context hook");
SafeMemory.Values[new nint(contextAddress)] = new byte[contextPrologue.Length];
using (var badSignature = new DamageTracker())
{
    badSignature.Install(new nint(0x140000000), AddressMap.TryLoadEmbedded(421810)!);
    Equal("context hook signature mismatch", badSignature.PartStatus, "changed native function is not hooked");
    Equal(true, badSignature.Hooked, "part hook failure preserves damage capture");
}
using (var oldBuild = new DamageTracker())
{
    oldBuild.Install(new nint(0x140000000), AddressMap.TryLoadEmbedded(421631)!);
    Equal("no verified context hook for this map", oldBuild.PartStatus, "unverified build leaves parts unknown");
}
SafeMemory.Values.Clear();

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
