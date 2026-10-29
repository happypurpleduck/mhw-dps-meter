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
Equal(true, hp.LastPollComplete, "successful monster enumeration is fresh");
Monster.ReadAll = () => throw new InvalidOperationException("Unreadable list");
Equal(2000, hp.Poll(), "failed enumeration preserves damage");
Equal(false, hp.LastPollComplete, "failed enumeration cannot authorize a fresh gauge read");
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
Equal(false, hp.LastPollComplete, "partially torn list is not a fresh snapshot");
Monster.ReadAll = () => [monster, other];
Equal(4000, hp.Poll(), "partial enumeration recovery");
Monster.ReadAll = () => [other];
Equal(4000, hp.Poll(), "real despawn banks damage");
other.Hp = 7000;
Equal(5000, hp.Poll(), "damage after despawn remains cumulative");

// A complete native enumeration that positively identifies a SmallBarrel or
// another excluded/too-small entity at a prior large-monster address must
// retire the old monster; a failed HP/type read must NOT retire it.
var oldTrackedPointer = (nint)0xD0000;
var trackedFixture = new Monster { Instance = oldTrackedPointer, Hp = 900, MaxHealth = 1000 };
var untrackedFixture = new Monster
{
    Instance = oldTrackedPointer, MaxHealth = 1000, Hp = 1000,
    Type = MonsterType.SmallBarrel
};
var smallHealthFixture = new Monster
{
    Instance = oldTrackedPointer, MaxHealth = 500, Hp = 500,
    Type = MonsterType.LargeMonster
};
var untrackedHp = new MonsterHpTracker();
var untrackedRecorder = new HuntRecorder((_, _) => null);
Monster.ReadAll = () => [trackedFixture];
Equal(100, untrackedHp.Poll(), "large tracked monster starts with 100 damage");
untrackedRecorder.ObserveMonsters(0, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal(1, untrackedRecorder.Monsters().Length, "original large monster has m1");

trackedFixture.FailTypeRead = true;
Monster.ReadAll = () => [trackedFixture];
Equal(100, untrackedHp.Poll(), "transient unreadable type preserves tracked fallback damage");
Equal(true, untrackedHp.LiveInstances.Contains(oldTrackedPointer),
    "transient unreadable type preserves a known pointer");
untrackedRecorder.ObserveMonsters(1, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal("m1", untrackedRecorder.SingleLiveMonsterId(),
    "transient unreadable type retains m1");
trackedFixture.FailTypeRead = false;

Monster.ReadAll = () => [untrackedFixture];
Equal(100, untrackedHp.Poll(), "excluded replacement banks previous large-monster damage");
Equal(false, untrackedHp.LiveInstances.Contains(oldTrackedPointer),
    "explicit excluded replacement removes stale large-monster hook target");
untrackedRecorder.ObserveMonsters(2, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal<string?>(null, untrackedRecorder.SingleLiveMonsterId(),
    "recorder live identity is retired when SmallBarrel reuses pointer");
Equal(1, untrackedRecorder.Monsters().Length,
    "old large monster remains in historical record after excluded replacement");

Monster.ReadAll = () => [trackedFixture];
Equal(200, untrackedHp.Poll(),
    "tracked entity reusing excluded pointer starts a separate 100-damage lifecycle");
untrackedRecorder.ObserveMonsters(3, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal(2, untrackedRecorder.Monsters().Length,
    "reappearing large monster receives m2 after excluded replacement");
Monster.ReadAll = () => [smallHealthFixture];
Equal(200, untrackedHp.Poll(), "small readable max-HP replacement banks exactly once");
Equal(0, untrackedHp.LiveInstances.Count,
    "readable sub-800 HP replacement is not a large-monster hook target");
untrackedRecorder.ObserveMonsters(4, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal<string?>(null, untrackedRecorder.SingleLiveMonsterId(),
    "sub-800 HP replacement also retires the live monster ID");
Monster.ReadAll = () => [other]; // restore prior simulated game state


// Identity-reuse regression. A first 1,000-HP monster takes only 100 damage.
// The new monster has the same native address but a different type, so the
// original 25%-of-max-HP heuristic alone would fail to bank its first 100.
var reusedPointer = (nint)0x90000;
var firstSpawn = new Monster
{
    Instance = reusedPointer, MaxHealth = 1000, Hp = 900,
    Type = MonsterType.LargeMonster, Name = "First spawn"
};
var replacementByType = new Monster
{
    Instance = reusedPointer, MaxHealth = 1000, Hp = 1000,
    Type = MonsterType.OtherLargeMonster, Name = "Second spawn"
};
var replacementByVariant = new Monster
{
    Instance = reusedPointer, MaxHealth = 1000, Hp = 1000,
    Type = MonsterType.OtherLargeMonster, Variant = 1, Name = "Third spawn"
};
var reusedHp = new MonsterHpTracker();
var reusedRecorder = new HuntRecorder((_, _) => null);
Monster.ReadAll = () => [firstSpawn];
Equal(100, reusedHp.Poll(), "initial 100 damage is counted");
reusedRecorder.ObserveMonsters(0, reusedHp.LastTracked);
Equal(1, reusedRecorder.Monsters().Length, "first generation uses first fight-local ID");

Monster.ReadAll = () => [replacementByType];
Equal(100, reusedHp.Poll(), "different type at same pointer banks the prior 100 exactly once");
reusedRecorder.ObserveMonsters(1, reusedHp.LastTracked);
Equal(2, reusedRecorder.Monsters().Length, "different type at same pointer gets a new monster ID");
Equal("Second spawn", reusedHp.LastTracked.Single().Name,
    "replacement monster name isn't inherited from the first generation");
replacementByType.Hp = 900;
Equal(200, reusedHp.Poll(), "first 100 damage on new type isn't swallowed by old baseline");
reusedRecorder.ObserveMonsters(2, reusedHp.LastTracked);
Equal(2, reusedRecorder.Monsters().Length, "ordinary damage doesn't create duplicate monster IDs");

Monster.ReadAll = () => [replacementByVariant];
Equal(200, reusedHp.Poll(), "variant change banks second generation exactly once");
reusedRecorder.ObserveMonsters(3, reusedHp.LastTracked);
Equal(3, reusedRecorder.Monsters().Length, "variant change gets a third monster ID");
replacementByVariant.Hp = 930;
Equal(270, reusedHp.Poll(), "first 70 damage on new variant is counted");
reusedRecorder.ObserveMonsters(4, reusedHp.LastTracked);
Equal(3, reusedRecorder.Monsters().Length, "continued damage keeps the correct generation");
Equal("Third spawn", reusedHp.LastTracked.Single().Name,
    "variant replacement refreshes the cached monster name");

Monster.ReadAll = () => [];
Equal(270, reusedHp.Poll(), "despawn banks the third generation without double counting");
Equal(0, reusedHp.LiveInstances.Count, "despawn clears live pointer state");
Monster.ReadAll = () => [replacementByVariant];
Equal(340, reusedHp.Poll(), "reappearing pointer starts a new lifecycle's damage");
reusedHp.Reset();
Monster.ReadAll = () => [];
Equal(0, reusedHp.Poll(), "hunt reset discards previous generation baselines");
Monster.ReadAll = () => [other]; // restore prior simulated game state

// Gauge metadata preserves collision-slot order and separates flinch cycles from HP.
Equal<int?>(2, MonsterParts.FromNormalSlot(0, 0), "Anjanath normal slot zero is head");
Equal<int?>(5, MonsterParts.NextBreakThreshold(0, 2, 0), "Anjanath head next break at five cycles");
Equal<int?>(5, MonsterParts.NextBreakThreshold(0, 2, 4), "Anjanath head still next at four");
Equal<int?>(null, MonsterParts.NextBreakThreshold(0, 2, 5), "Anjanath head threshold exhausted");

// Simulated protected reads validate layout interpretation, NOT in-game offsets.
SafeMemory.Values.Clear();
nint gaugeMonster = 0x20000, partController = 0x30000, ailmentPtr = 0x40000;
SafeMemory.Values[gaugeMonster + 0x1C0F0] = 90f;
SafeMemory.Values[gaugeMonster + 0x1C0F4] = 150f;
SafeMemory.Values[gaugeMonster + 0x1D058] = partController;
SafeMemory.Values[partController + 0x40 + 0x0C] = 200f;
SafeMemory.Values[partController + 0x40 + 0x10] = 100f;
SafeMemory.Values[partController + 0x40 + 0x18] = 1;
SafeMemory.Values[gaugeMonster + 0x1BC40] = ailmentPtr;
SafeMemory.Values[gaugeMonster + 0x1BC48] = (nint)0;
SafeMemory.Values[ailmentPtr + 0x148] = gaugeMonster;
SafeMemory.Values[ailmentPtr + 0x148 + 0x10] = 7;
SafeMemory.Values[ailmentPtr + 0x148 + 0x14] = 10f;
SafeMemory.Values[ailmentPtr + 0x148 + 0x30] = 75f;
SafeMemory.Values[ailmentPtr + 0x148 + 0x40] = 100f;
SafeMemory.Values[ailmentPtr + 0x148 + 0x70] = 0f;
SafeMemory.Values[ailmentPtr + 0x148 + 0x78] = 2;
var gaugeInput = new MonsterState(gaugeMonster, (MonsterType)0, "Anjanath", 0, 1000, 800);
var observed = MonsterGaugeReader.Read([gaugeInput]).Single();
Equal(90f, observed.Stamina, "reads measured stamina");
Equal(150f, observed.MaxStamina, "reads maximum stamina");
Equal(2, observed.Parts[0].Part, "part meter normal slot maps to head");
Equal(100f, observed.Parts[0].Remaining, "part flinch meter remains independent of HP");
Equal<int?>(5, observed.Parts[0].NextBreakThreshold, "part cycle uses definition threshold");
Equal(7, observed.Ailments[0].Id, "KO status id");
Equal(75f, observed.Ailments[0].Buildup, "KO buildup");
Equal(2, observed.Ailments[0].Procs, "KO proc count");

// An independently unreadable middle ailment is skipped without losing later data.
nint corruptAilment = 0x50000, laterAilment = 0x60000;
SafeMemory.Values[gaugeMonster + 0x1BC48] = corruptAilment;
SafeMemory.Values[corruptAilment + 0x148] = (nint)0x70000; // incorrect owner
SafeMemory.Values[gaugeMonster + 0x1BC50] = laterAilment;
SafeMemory.Values[gaugeMonster + 0x1BC58] = (nint)0;
SafeMemory.Values[laterAilment + 0x148] = gaugeMonster;
SafeMemory.Values[laterAilment + 0x148 + 0x10] = 1; // poison
SafeMemory.Values[laterAilment + 0x148 + 0x14] = 15f;
SafeMemory.Values[laterAilment + 0x148 + 0x30] = 20f;
SafeMemory.Values[laterAilment + 0x148 + 0x40] = 200f;
SafeMemory.Values[laterAilment + 0x148 + 0x70] = 0f;
SafeMemory.Values[laterAilment + 0x148 + 0x78] = 1;
Equal(2, MonsterGaugeReader.Read([gaugeInput]).Single().Ailments.Length,
    "invalid middle owner does not hide later ailment");
SafeMemory.Values.Remove(corruptAilment + 0x148);
Equal(2, MonsterGaugeReader.Read([gaugeInput]).Single().Ailments.Length,
    "unreadable middle owner does not hide later ailment");
SafeMemory.Values[gaugeMonster + 0x1BC48] = (nint)0;
Equal(1, MonsterGaugeReader.Read([gaugeInput]).Single().Ailments.Length,
    "real null terminator stops even when later pointer is non-null");
SafeMemory.Values[gaugeMonster + 0x1BC48] = (nint)1;
Equal(1, MonsterGaugeReader.Read([gaugeInput]).Single().Ailments.Length,
    "HunterPie terminal pointer 1 stops even when later slot is non-null");
SafeMemory.Values[gaugeMonster + 0x1BC48] = (nint)0;

// Alatreon's canonical part 3 is an elemental explosion counter, not the
// ordinary +0x18 flinch cycle. Retain ordinary neighboring part snapshots.
var alatreon = gaugeInput with { Type = (MonsterType)87, Name = "Alatreon" };
var alatreonChest = partController + 0x40 + 0x1F8;
SafeMemory.Values[alatreonChest + 0x0C] = 120f;
SafeMemory.Values[alatreonChest + 0x10] = 90f;
SafeMemory.Values[alatreonChest + 0x18] = 2;
var alatreonSnapshot = MonsterGaugeReader.Read([alatreon]).Single();
Equal(false, alatreonSnapshot.Parts.Any(p => p.Part == 3),
    "Alatreon elemental exception must never masquerade as head flinch count");
Equal(true, alatreonSnapshot.Parts.Any(p => p.Part == 4 && p.CompletedFlinches == 2),
    "Alatreon nonexceptional neighboring part remains readable");
SafeMemory.Values.Remove(alatreonChest + 0x0C);
SafeMemory.Values.Remove(alatreonChest + 0x10);
SafeMemory.Values.Remove(alatreonChest + 0x18);

// The expensive memory reader has its own cadence and does not need party data.
var gaugePoller = new MonsterGaugePoller();
var gaugeReadCalls = 0;
MonsterGaugeState[] Sample(IReadOnlyList<MonsterState> _) { gaugeReadCalls++; return [observed]; }
var noPartySnapshot = (PartySnapshot?)null; // party-table acquisition failed
var firstRead = gaugePoller.Tick(0f, true, true, [gaugeInput], Sample);
Equal(true, noPartySnapshot is null && firstRead?.Length == 1,
    "live monster gauges survive independent transient party read failure");
Equal(1, gaugeReadCalls, "first gauge reading refreshes immediately");
Equal<MonsterGaugeState[]?>(null, gaugePoller.Tick(0.1f, true, true, [gaugeInput], Sample),
    "100ms DPS poll doesn't also fetch every monster gauge");
Equal(1, gaugeReadCalls, "no extra native reads before refresh interval");
Equal(1, gaugePoller.Tick(1.1f, true, true, [gaugeInput], Sample)!.Length,
    "gauge refresh happens on its independent cadence");
Equal(2, gaugeReadCalls, "one read per 1s interval");

// Iceborne's dynamic 1 -> 2 -> 1 player difficulty scaling can change max
// HP without changing the monster at this pointer. No new mN, no phantom
// fallback damage, no extra gauge read until its normal 1-second interval.
var scalingMonster = new Monster
{
    Instance = 0xA0000, Type = MonsterType.LargeMonster,
    Name = "Scaling monster", MaxHealth = 1000f, Hp = 800f
};
var scalingHp = new MonsterHpTracker();
var scalingRecorder = new HuntRecorder((_, _) => null);
var scalingPoller = new MonsterGaugePoller();
var scalingReadCalls = 0;
MonsterGaugeState[] ScalingRead(IReadOnlyList<MonsterState> states)
{
    scalingReadCalls++;
    return states.Select(x => observed with { Instance = x.Instance, Name = x.Name }).ToArray();
}
Monster.ReadAll = () => [scalingMonster];
Equal(200, scalingHp.Poll(), "partly damaged solo monster has 200 HP damage");
var soloState = scalingHp.LastTracked.Single();
scalingRecorder.ObserveMonsters(0, scalingHp.LastTracked);
Equal(1, scalingPoller.Tick(700.0, true, true, scalingHp.LastTracked, ScalingRead)!.Length,
    "first solo monster gauge read succeeds");

scalingMonster.MaxHealth = 1500f;
scalingMonster.Hp = 1200f; // same 80% health on two-player difficulty
Equal(200, scalingHp.Poll(), "joining hunter cannot invent 100 damage through max HP rescale");
var joinedState = scalingHp.LastTracked.Single();
Equal(false, MonsterGeneration.Changed(soloState, joinedState),
    "proportional max/current HP rescale is not a new monster generation");
Equal(true, MonsterGeneration.Rescaled(soloState, joinedState),
    "max-HP scaling is explicitly recognized as a baseline rebase");
scalingRecorder.ObserveMonsters(1, scalingHp.LastTracked);
Equal(1, scalingRecorder.Monsters().Length, "joining hunter retains the same fight-local monster");
Equal<MonsterGaugeState[]?>(null,
    scalingPoller.Tick(700.1, true, true, scalingHp.LastTracked, ScalingRead),
    "party scaling does not force premature gauge refresh");
Equal(1, scalingPoller.Current.Length, "gauge cache stays visible through party scaling");
Equal(1, scalingReadCalls, "party scaling respects native gauge refresh throttle");

scalingMonster.Hp = 1100f; // real 100 damage at the new HP scale
Equal(300, scalingHp.Poll(), "first 100 damage after joining counts once");
scalingRecorder.ObserveMonsters(2, scalingHp.LastTracked);
Equal(1, scalingRecorder.Monsters().Length, "continued damage doesn't create a generation");

scalingMonster.MaxHealth = 1000f;
scalingMonster.Hp = 733.3333f; // 1100 / 1500 health proportion at solo scaling
Equal(300, scalingHp.Poll(), "hunter leaving cannot invent or discard damage on downscale");
var leftState = scalingHp.LastTracked.Single();
Equal(false, MonsterGeneration.Changed(joinedState, leftState),
    "downscaled HP still belongs to the original monster");
scalingRecorder.ObserveMonsters(3, scalingHp.LastTracked);
Equal(1, scalingRecorder.Monsters().Length, "joining then leaving retains exactly one monster ID");
Equal<MonsterGaugeState[]?>(null,
    scalingPoller.Tick(700.5, true, true, scalingHp.LastTracked, ScalingRead),
    "downscale does not invalidate a healthy cached gauge");
Equal(1, scalingReadCalls, "join/leave never doubles native gauge read work");

scalingMonster.Hp = 633.3333f;
Equal(400, scalingHp.Poll(), "next 100 damage after leaving counts once");
scalingRecorder.ObserveMonsters(4, scalingHp.LastTracked);
Equal(1, scalingRecorder.Monsters().Length, "later damage keeps the first fight-local monster ID");
Equal(1, scalingPoller.Tick(701.1, true, true, scalingHp.LastTracked, ScalingRead)!.Length,
    "gauges still refresh at normal monotonic cadence after scaling");
Equal(2, scalingReadCalls, "only scheduled native reads occurred over scaling transition");
scalingHp.Reset();
scalingRecorder.Reset();
Equal(0, scalingRecorder.Monsters().Length, "recording reset clears identity across new hunts");
Monster.ReadAll = () => [other];

Equal<MonsterGaugeState[]?>(null, gaugePoller.Tick(1.2f, true, false, [gaugeInput], Sample),
    "torn monster enumeration prevents another protected read");
Equal(0, gaugePoller.Current.Length, "torn monster list invalidates stale overlay");
Equal(1, gaugePoller.Tick(1.3f, true, true, [gaugeInput], Sample)!.Length,
    "reader recovers immediately when a complete monster list returns");
Equal<MonsterGaugeState[]?>(null, gaugePoller.Tick(1.4f, false, true, [gaugeInput], Sample),
    "turning off experimental feature disables all gauge reads");
Equal(0, gaugePoller.Current.Length, "disabled readings never remain on overlay");

// The quest clock may move backwards from 300s to 10s on timer-source change.
// Scheduler receives strictly monotonic time instead, so reads keep flowing.
gaugePoller.Reset();
gaugeReadCalls = 0;
Equal(1, gaugePoller.Tick(300.0, true, true, [gaugeInput], Sample)!.Length,
    "large quest timestamp does not affect the independent cadence");
float questClockAfterSwitch = 10f;
Equal<MonsterGaugeState[]?>(null, gaugePoller.Tick(300.4, true, true, [gaugeInput], Sample),
    "elapsed monotonic time, not restarted hunt clock, controls refresh");
Equal(1, gaugePoller.Tick(301.1, true, true, [gaugeInput], Sample)!.Length,
    "monotonic polling continues after quest clock jumps backward");
Equal(2, gaugeReadCalls, "clock switch does not stall reads until quest catches up");
Equal(10f, questClockAfterSwitch, "observation timestamp may still reflect changed quest clock");

// Test the same plugin integration seam with no usable party snapshot, not
// merely the raw MonsterGaugePoller called by itself.
var integrationPoller = new MonsterGaugePoller();
var integrationRecorder = new HuntRecorder((_, _) => null);
var integrationReadCalls = 0;
MonsterGaugeState[] IntegrationReader(IReadOnlyList<MonsterState> _) { integrationReadCalls++; return [observed]; }
var missingParty = HuntGaugeObservation.CaptureBeforePartyGuard(
    () => null, 300f, 400.0, "quest", true, true, [gaugeInput],
    integrationPoller, integrationRecorder, IntegrationReader);
Equal<PartySnapshot?>(null, missingParty, "party snapshot remains unavailable");
Equal(1, integrationReadCalls, "integration reads monster gauges despite missing party data");
Equal(1, integrationRecorder.GaugeSamples().Length, "integration records a gauge with no party snapshot");
Equal(1, integrationRecorder.Monsters().Length, "integration still tracks the monster when party data fails");
HuntGaugeObservation.CaptureBeforePartyGuard(
    () => null, 10f, 401.1, "local", true, true, [gaugeInput],
    integrationPoller, integrationRecorder, IntegrationReader);
Equal(2, integrationReadCalls, "integrated reader refreshes across backwards hunt clock jump");
Equal(1, integrationRecorder.GaugeSamples().Length, "recorder waits for independent 2s interval");
HuntGaugeObservation.CaptureBeforePartyGuard(
    () => null, 10.2f, 402.2, "local", true, true, [gaugeInput],
    integrationPoller, integrationRecorder, IntegrationReader);
Equal(2, integrationRecorder.GaugeSamples().Length,
    "monotonic recorder accepts new sample despite backwards hunt clock jump");
var clockJumpSamples = integrationRecorder.GaugeSamples();
Equal(300f, clockJumpSamples[0].T, "earlier sample stays first in capture timeline");
Equal(true, clockJumpSamples[1].T > clockJumpSamples[0].T,
    "later observation remains later even when raw quest time moves backwards");
Equal<float?>(10.2f, clockJumpSamples[1].RawHuntT,
    "raw quest time is retained as separately identified source data");
Equal(true, clockJumpSamples[1].ClockAdjusted, "adjusted timestamp marked as such");
Equal(1, integrationRecorder.GaugeClockDiscontinuities, "clock source switch recorded once");

// Production's exact integration seam also catches a throwing party read
// instead of skipping healthy monster observation (unlike the old argument call).
integrationPoller.Reset();
integrationRecorder.Reset();
var thrownPartyError = false;
var afterThrow = HuntGaugeObservation.CaptureBeforePartyGuard(
    () => throw new InvalidOperationException("party table detached"),
    3f, 450.0, "local", true, true, [gaugeInput],
    integrationPoller, integrationRecorder, IntegrationReader,
    _ => thrownPartyError = true);
Equal<PartySnapshot?>(null, afterThrow, "exceptional party acquisition safely yields no snapshot");
Equal(true, thrownPartyError, "party exception is reported through error handler");
Equal(1, integrationRecorder.GaugeSamples().Length,
    "monster observation persists after throwing party acquisition");
Equal(1, integrationRecorder.Monsters().Length,
    "monster identity still registered after throwing party read");

// Both a different type and a large HP reset at the same pointer invalidate
// cached readings immediately, without waiting for a missing-pointer poll.
gaugePoller.Reset();
var older = gaugeInput with { Health = 800 };
var replacement = older with { Type = (MonsterType)1, Name = "New spawn", Variant = 1 };
gaugeReadCalls = 0;
gaugePoller.Tick(500.0, true, true, [older], Sample);
Equal(1, gaugePoller.Tick(500.1, true, true, [replacement], Sample)!.Length,
    "same-pointer type/variant replacement refreshes immediately");
Equal(2, gaugeReadCalls, "new generation bypasses 1s gauge throttle");
Equal(true, MonsterGeneration.Changed(older, replacement), "changed identity is a new generation");
var hurt = older with { Health = 100 };
var respawn = hurt with { Health = 900 };
gaugePoller.Reset();
gaugePoller.Tick(600.0, true, true, [hurt], Sample);
Equal(1, gaugePoller.Tick(600.1, true, true, [respawn], Sample)!.Length,
    "same-pointer large HP reset also forces immediate refresh");
Equal(true, MonsterGeneration.Changed(hurt, respawn), "HP reset identifies a possible respawn");
var generationRecorder = new HuntRecorder((_, _) => null);
generationRecorder.ObserveMonsters(0, [older]);
generationRecorder.ObserveMonsters(1, [replacement]);
Equal(2, generationRecorder.Monsters().Length, "type replacement receives a new fight monster ID");
generationRecorder.ObserveMonsters(2, [hurt]);
generationRecorder.ObserveMonsters(3, [respawn]);
Equal(4, generationRecorder.Monsters().Length, "HP reset receives another fight monster ID");

// A successful empty/nonempty enumeration is positive evidence of a monster
// disappearing. If that address comes back with an identical type, name and
// only a small HP recovery, it is still a *new* fight-local generation.
// A torn/failed native enumeration must NOT be interpreted as a disappearance.
var samePointer = (nint)0xB0000;
var oldSame = new Monster
{
    Instance = samePointer, MaxHealth = 1000f, Hp = 900f,
    Type = MonsterType.LargeMonster, Name = "Same monster", Variant = 0
};
var newSame = new Monster
{
    Instance = samePointer, MaxHealth = 1000f, Hp = 1000f,
    Type = MonsterType.LargeMonster, Name = "Same monster", Variant = 0
};
var lifecycleHp = new MonsterHpTracker();
var lifecycleRecorder = new HuntRecorder((_, _) => null);
var lifecyclePoller = new MonsterGaugePoller();
var lifecycleGaugeReads = 0;
MonsterGaugeState[] LifecycleRead(IReadOnlyList<MonsterState> states)
{
    lifecycleGaugeReads++;
    return states.Select(m => observed with { Instance = m.Instance, Name = m.Name }).ToArray();
}
void ObserveLifecycle(float time, double monotonic)
{
    HuntGaugeObservation.CaptureBeforePartyGuard(
        () => null, time, monotonic, "local", true,
        lifecycleHp.LastPollComplete, lifecycleHp.LastTracked,
        lifecyclePoller, lifecycleRecorder, LifecycleRead,
        knownPresentInstances: lifecycleHp.LiveInstances);
}
Monster.ReadAll = () => [oldSame];
Equal(100, lifecycleHp.Poll(), "first monster starts with 100 real damage");
ObserveLifecycle(0, 900);
Equal(1, lifecycleRecorder.Monsters().Length, "first monster is m1");
Equal("m1", lifecycleRecorder.GaugeSamples().Single().Monster,
    "first gauge sample belongs to original lifetime");

// Native enumeration tears: the tracker retains the last known state,
// but the recorder must not mistakenly remove m1.
Monster.ReadAll = () => throw new InvalidOperationException("Transient torn monster enumeration");
Equal(100, lifecycleHp.Poll(), "failed enumeration retains prior cumulative damage");
Equal(false, lifecycleHp.LastPollComplete, "failed enumeration is not a known empty list");
ObserveLifecycle(1, 901);
Equal(1, lifecycleRecorder.Monsters().Length, "failed enumeration preserves original m1");
Monster.ReadAll = () => [oldSame];
Equal(100, lifecycleHp.Poll(), "recovery from torn enumeration does not double-count");
ObserveLifecycle(1.1f, 901.1);
Equal(1, lifecycleRecorder.Monsters().Length,
    "reappearance after an unverified list must NOT create m2");

// A complete enumeration may still fail to read one known monster's HP.
// That monster remains in LiveInstances and must not be evicted from the
// recorder solely because this poll couldn't make a MonsterState for it.
oldSame.FailHealthRead = true;
Monster.ReadAll = () => [oldSame];
Equal(100, lifecycleHp.Poll(), "temporary HP-read failure retains fallback damage");
Equal(true, lifecycleHp.LastPollComplete, "native list itself was fully enumerated");
Equal(0, lifecycleHp.LastTracked.Count, "unreadable HP has no fresh MonsterState");
Equal(true, lifecycleHp.LiveInstances.Contains(samePointer),
    "successful enumeration still knows the monster pointer is present");
ObserveLifecycle(1.2f, 901.2);
Equal(1, lifecycleRecorder.Monsters().Length,
    "unreadable individual state must not mark known pointer as despawned");
oldSame.FailHealthRead = false;
Equal(100, lifecycleHp.Poll(), "HP-read recovery resumes same generation");
ObserveLifecycle(1.3f, 901.3);
Equal(1, lifecycleRecorder.Monsters().Length,
    "HP-read recovery retains original m1");

// A real, successfully enumerated empty list is an observed despawn.
Monster.ReadAll = () => [];
Equal(100, lifecycleHp.Poll(), "complete empty list banks old 100 damage");
Equal(true, lifecycleHp.LastPollComplete, "empty enumeration is successful");
ObserveLifecycle(2, 902);
Equal(1, lifecycleRecorder.Monsters().Length, "despawn preserves historical m1");
Equal<string?>(null, lifecycleRecorder.SingleLiveMonsterId(),
    "despawn removes stale monster from the live attribution map");
Monster.ReadAll = () => [newSame];
Equal(100, lifecycleHp.Poll(), "reused pointer starts at full health without invented damage");
Equal(false, MonsterGeneration.Changed(
    new MonsterState(samePointer, oldSame.Type, oldSame.Name, 0, 1000, 900),
    lifecycleHp.LastTracked.Single()),
    "only the observed disappearance, not identity/HP heuristics, identifies new generation");
ObserveLifecycle(4, 904);
Equal(2, lifecycleRecorder.Monsters().Length, "same-pointer identical reappearance gets m2");
Equal("m1", lifecycleRecorder.Monsters()[0].Id, "original m1 remains in historical list");
Equal("m2", lifecycleRecorder.Monsters()[1].Id, "replacement gets a distinct fight-local ID");
Equal("m2", lifecycleRecorder.GaugeSamples().Last().Monster,
    "new gauge sample belongs to the replacement generation");
Equal(4, lifecycleGaugeReads,
    "failed enumeration and unreadable-HP recovery each refresh before true pointer replacement");

newSame.Hp = 970;
Monster.ReadAll = () => [newSame];
Equal(130, lifecycleHp.Poll(), "damage on the second lifetime adds to banked first damage");
ObserveLifecycle(4.2f, 904.2);
Equal(2, lifecycleRecorder.Monsters().Length, "ordinary damage does not create m3");
Monster.ReadAll = () => [other];



var gaugeRecorder = new HuntRecorder((_, _) => null);
gaugeRecorder.ObserveMonsters(0, [gaugeInput]);
gaugeRecorder.ObserveGauges(0, 0.0, "local", [observed]);
gaugeRecorder.ObserveGauges(1, 1.0, "local", [observed]);
gaugeRecorder.ObserveGauges(2.1f, 2.1, "local", [observed]);
Equal(2, gaugeRecorder.GaugeSamples().Length, "two-second state capture cadence");
Equal("local-unverified", gaugeRecorder.GaugeSamples()[0].Source, "never claims host authority");
Equal("m1", gaugeRecorder.GaugeSamples()[0].Monster, "stable fight-local monster link");
for (var sampleIndex = 2; sampleIndex < HuntRecorder.MaxGaugeSamples + 3; sampleIndex++)
    gaugeRecorder.ObserveGauges(sampleIndex * 2.1f, sampleIndex * 2.1, "local", [observed]);
Equal(HuntRecorder.MaxGaugeSamples, gaugeRecorder.GaugeSamples().Length,
    "bounded gauge samples never exceed global cap");
Equal(3, gaugeRecorder.GaugeSamplesDropped,
    "truncated intervals counted instead of silently omitted");
gaugeRecorder.Reset();
Equal(0, gaugeRecorder.GaugeSamplesDropped, "new quest resets truncated gauge coverage");
SafeMemory.Values[gaugeMonster + 0x1C0F0] = -1f;
SafeMemory.Values[partController + 0x40 + 0x10] = -5f;
var invalidGauge = MonsterGaugeReader.Read([gaugeInput]).Single();
Equal<float?>(null, invalidGauge.Stamina, "invalid stamina becomes unknown rather than zero");
Equal(0, invalidGauge.Parts.Length, "invalid part gauge omitted, not clamped");
SafeMemory.Values.Clear();

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

    // Real damage detour: capture identity at hook time rather than consulting
    // the latest pointer map when buffered HitRecords are finally drained.
    // Reuse the same target with different monster lifetimes between drains.
    var queuedRecorder = new HuntRecorder((_, _) => null);
    var queuedM1 = new MonsterState(target, (MonsterType)30, "Old native monster", 0, 1000, 900);
    var queuedM2 = queuedM1 with { Name = "New native monster", Health = 1000 };
    queuedRecorder.ObserveMonsters(0, [queuedM1]);
    damage.SetMonsterIdentityResolver(queuedRecorder.CaptureMonsterIdentity);
    damage.UpdateMonsters([target]);
    Emit(hitData + 0xE0); // m1 hit buffered in production detour
    Equal("m1", damage.DrainHits().Single().MonsterId,
        "native hook captures m1 identity synchronously");

    Emit(hitData + 0xE0); // m1 queued before complete empty enumeration
    queuedRecorder.ObserveMonsters(1, [], Array.Empty<nint>());
    damage.UpdateMonsters([]);
    Equal<string?>(null, queuedRecorder.SingleLiveMonsterId(),
        "confirmed disappearance retires live monster map");
    var queuedM1Hits = damage.DrainHits();
    queuedRecorder.AddHits(1.1f, queuedM1Hits, 0);
    Equal("m1", queuedRecorder.Hits().Last().Monster,
        "m1 hit buffered before disappearance retains m1 when drained later");

    queuedRecorder.ObserveMonsters(2, [queuedM2]);
    damage.UpdateMonsters([target]);
    Emit(hitData + 0xE0); // m2 buffered but not drained
    queuedRecorder.ObserveMonsters(3, [queuedM2 with { Name = "Replacement again" }]);
    Emit(hitData + 0xE0); // m3 buffered
    var reassigned = damage.DrainHits();
    Equal("m2", reassigned[0].MonsterId, "hook binds first new generation");
    Equal("m3", reassigned[1].MonsterId, "hook binds second new generation");
    queuedRecorder.AddHits(3.1f, reassigned, 0);
    Equal("m2", queuedRecorder.Hits()[1].Monster,
        "buffered m2 hit cannot be relabeled as m3 on drain");
    Equal("m3", queuedRecorder.Hits()[2].Monster,
        "subsequent m3 hit uses new generation");
    
    queuedRecorder.ObserveMonsters(4, [], Array.Empty<nint>());
    // During pointer handoff the hook still has an accepted target until
    // UpdateMonsters runs; unknown identity must remain unknown after re-spawn.
    Emit(hitData + 0xE0);
    var unidentified = damage.DrainHits();
    Equal(true, unidentified.Single().HasMonsterIdentity,
        "unmapped hook-time identity is still explicitly captured");
    Equal<string?>(null, unidentified.Single().MonsterId,
        "hook snapshot marks unknown rather than guessing future identity");
    queuedRecorder.ObserveMonsters(5, [queuedM1]);
    queuedRecorder.AddHits(5.1f, unidentified, 0);
    Equal<string?>(null, queuedRecorder.Hits().Last().Monster,
        "unknown hit before new mapping is never retrospectively assigned");
    damage.UpdateMonsters([]);
    damage.SetMonsterIdentityResolver(_ => (null, null));


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
