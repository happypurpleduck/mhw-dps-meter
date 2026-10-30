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

// A temporarily uninitialized or corrupt native max HP is not proof of
// pointer reuse. Both zero and negative must preserve the old damage,
// known pointer, recorder ID and enumeration-complete signal.
trackedFixture.MaxHealth = 0;
Monster.ReadAll = () => [trackedFixture];
Equal(100, untrackedHp.Poll(), "zero max HP preserves previously counted damage");
Equal(true, untrackedHp.LastPollComplete, "zero max HP is an invalid state, not a torn list");
Equal(true, untrackedHp.LiveInstances.Contains(oldTrackedPointer),
    "zero max HP preserves the known large-monster pointer");
untrackedRecorder.ObserveMonsters(1.1f, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal("m1", untrackedRecorder.SingleLiveMonsterId(),
    "zero max HP cannot retire the live m1");
Equal(1, untrackedRecorder.Monsters().Length, "zero max HP cannot duplicate historical monster");

trackedFixture.MaxHealth = -100;
Equal(100, untrackedHp.Poll(), "negative max HP must not throw or bank damage");
Equal(true, untrackedHp.LastPollComplete, "negative max HP cannot tear enumeration via Math.Clamp");
Equal(true, untrackedHp.LiveInstances.Contains(oldTrackedPointer),
    "negative max HP preserves the known large-monster pointer");
untrackedRecorder.ObserveMonsters(1.2f, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal("m1", untrackedRecorder.SingleLiveMonsterId(),
    "negative max HP cannot retire the live m1");
Equal(1, untrackedRecorder.Monsters().Length, "negative max HP cannot duplicate historical monster");

trackedFixture.MaxHealth = 1000;
Equal(100, untrackedHp.Poll(), "recovered max HP does not double count old damage");
Equal(true, untrackedHp.LastPollComplete, "recovered max HP produces a complete list");
untrackedRecorder.ObserveMonsters(1.3f, untrackedHp.LastTracked, untrackedHp.LiveInstances);
Equal("m1", untrackedRecorder.SingleLiveMonsterId(),
    "recovered max HP retains the original monster generation");
Equal(1, untrackedRecorder.Monsters().Length,
    "transient max HP corruption does not create another mN");

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

 // Rendering is a pure projection of cached data. No extra native reads or
 // gameplay claims are permitted by switching between compact/detailed rows.
 var secondGauge = observed with
 {
     Instance = gaugeMonster + 1, Name = "Second",
     Stamina = null, MaxStamina = null, Parts = [], Ailments = []
 };
 var thirdGauge = observed with { Instance = gaugeMonster + 2, Name = "Third" };
 var compactRows = MonsterGaugeOverlayRows.Build([observed, secondGauge, thirdGauge], false);
 Equal(3, compactRows.Length, "compact view bounded at two monster rows plus overflow note");
 Equal(true, compactRows[0].Heading && compactRows[0].Text.Contains("Anjanath | stamina 90/150"),
     "compact heading uses existing cache and real stamina");
 Equal(true, compactRows[0].Text.Contains("1 sampled status")
     && compactRows[0].Text.Contains("1 sampled part meters"),
     "compact status and part counts are independently visible sampled evidence");
 Equal(true, compactRows[1].Text.Contains("Second | stamina unavailable"),
     "unavailable stamina is never rendered as zero");
 Equal(true, compactRows[2].Text.Contains("+1 more sampled monster(s) not shown"),
     "more than two monsters are acknowledged instead of silently discarded");
 Equal(false, compactRows.Any(r => r.Text.Contains("buildup") || r.Text.Contains("flinch ")),
     "compact mode hides detailed sampled status and part rows");
 var expandedRows = MonsterGaugeOverlayRows.Build([observed], true);
 Equal(3, expandedRows.Length, "detail view includes one heading and existing status/part rows");
 Equal(true, expandedRows[1].Text.Contains("KO / Stun: buildup 75/100, sampled procs 2"),
     "status labels distinguish sampled counters from exact proc events");
 Equal(true, expandedRows[2].Text.Contains("Head: flinch 100/200 remaining, sampled cycle 1/5"),
     "part display distinguishes remaining gauge from sampled cycle");
 Equal(0, MonsterGaugeOverlayRows.Build([], true).Length,
     "empty cache never becomes a fake monster observation");

// A proc count is a sampled counter even after buildup and duration return to
// zero. It must remain visible in detailed mode, including when more than three
// active statuses compete for the bounded row limit.
var counterOnlyAilment = observed.Ailments[0] with
{
    Id = 12, Name = "Counter only", Buildup = 0f, Duration = 0f, Procs = 4
};
var counterOnlyRows = MonsterGaugeOverlayRows.Build(
    [observed with { Ailments = [counterOnlyAilment], Parts = [] }], true);
Equal(2, counterOnlyRows.Length, "counter-only cached ailment remains an available detailed row");
Equal(true, counterOnlyRows[1].Text.Contains("Counter only: buildup 0/100, sampled procs 4"),
    "zero buildup and duration do not mask a nonzero recorded proc count");
var busyGauge = observed with
{
    Parts = [],
    Ailments =
    [
        observed.Ailments[0] with { Id = 21, Name = "Active A", Buildup = 95f, Duration = 0f, Procs = 0 },
        observed.Ailments[0] with { Id = 22, Name = "Active B", Buildup = 80f, Duration = 0f, Procs = 0 },
        observed.Ailments[0] with { Id = 23, Name = "Active C", Buildup = 60f, Duration = 0f, Procs = 0 },
        counterOnlyAilment,
    ]
};
var prioritizedRows = MonsterGaugeOverlayRows.Build([busyGauge], true);
Equal(4, prioritizedRows.Length, "detailed gauge remains capped at three ailment rows");
Equal(true, prioritizedRows[1].Text.Contains("Counter only: buildup 0/100, sampled procs 4"),
    "counter-only observations receive priority when the three-row cap is full");
Equal(true, prioritizedRows[2].Text.Contains("Active A: buildup 95/100"),
    "highest active buildup is retained after the counter-only evidence");
Equal(true, prioritizedRows[3].Text.Contains("Active B: buildup 80/100"),
    "next active buildup follows in stable descending order");
Equal(false, prioritizedRows.Any(r => r.Text.Contains("Active C")),
    "lower-ranked active status does not overrun the bounded display");
Equal(1, MonsterGaugeOverlayRows.Build([busyGauge], false).Length,
    "compact mode remains a single sampled-summary row regardless of proc counters");



 // Independent F9 category preferences filter cached rows but never affect
 // the experimental native capture switch or the bounded display limits.
 var statusOnly = new MonsterGaugeOverlayCategories(false, true, false);
 var statusCompact = MonsterGaugeOverlayRows.Build([observed], false, statusOnly);
 Equal(1, statusCompact.Length, "status-only selection remains a compact one-row summary");
 Equal(true, statusCompact[0].Text.Contains("1 sampled status"),
     "selected status count remains visible");
 Equal(false, statusCompact[0].Text.Contains("stamina") || statusCompact[0].Text.Contains("part meters"),
     "disabled stamina and part categories cannot leak into compact summary");
 var statusExpanded = MonsterGaugeOverlayRows.Build([observed], true, statusOnly);
 Equal(2, statusExpanded.Length, "status-only details contain no part row");
 Equal(true, statusExpanded[1].Text.Contains("sampled procs"),
     "status-only details retain proc evidence");

 var partsOnly = new MonsterGaugeOverlayCategories(false, false, true);
 var partsCompact = MonsterGaugeOverlayRows.Build([observed], false, partsOnly);
 Equal(1, partsCompact.Length, "parts-only compact presentation remains one heading");
 Equal("Anjanath | 1 sampled part meters", partsCompact[0].Text,
     "parts-only heading includes sampled part count without hidden stamina or ailments");
 var partsExpanded = MonsterGaugeOverlayRows.Build([observed], true, partsOnly);
 Equal(2, partsExpanded.Length, "parts-only details contain no ailment row");
 Equal(true, partsExpanded[1].Text.Contains("sampled cycle"),
     "parts-only view retains part remaining/cycle semantics");

 var staminaOnly = new MonsterGaugeOverlayCategories(true, false, false);
 var staminaCompact = MonsterGaugeOverlayRows.Build([observed], false, staminaOnly);
 Equal(1, staminaCompact.Length, "stamina-only compact presentation remains one heading");
 Equal("Anjanath | stamina 90/150", staminaCompact[0].Text,
     "stamina-only heading includes measured stamina without hidden ailment or part counts");
 var staminaExpanded = MonsterGaugeOverlayRows.Build([observed], true, staminaOnly);
 Equal(1, staminaExpanded.Length, "stamina-only display never includes status/part details");
 Equal(true, staminaExpanded[0].Text.Contains("stamina 90/150"),
     "stamina-only cached reading still displayed");

 var noneSelected = MonsterGaugeOverlayRows.Build(
     [observed, secondGauge, thirdGauge], true, new MonsterGaugeOverlayCategories(false, false, false));
 Equal(3, noneSelected.Length, "all-disabled selection stays bounded and includes overflow note");
 Equal(true, noneSelected[0].Text.Contains("no sampled gauge categories selected"),
     "all-disabled selection is explicit rather than implying zero gauge readings");
 Equal(false, noneSelected[0].Text.Contains("stamina") ||
     noneSelected[0].Text.Contains("sampled status") ||
     noneSelected[0].Text.Contains("part meters"),
     "disabled categories never appear as fabricated measurements");
 Equal(true, noneSelected[2].Text.Contains("+1 more"),
     "monster overflow remains visible regardless of category preferences");
 var noneCompact = MonsterGaugeOverlayRows.Build(
     [observed, secondGauge, thirdGauge], false,
     new MonsterGaugeOverlayCategories(false, false, false));
 Equal(3, noneCompact.Length, "all-hidden compact mode stays bounded with overflow");
 Equal("Anjanath | no sampled gauge categories selected", noneCompact[0].Text,
     "all-hidden compact heading explicitly explains the selection without false readings");
 Equal("Second | no sampled gauge categories selected", noneCompact[1].Text,
     "second compact heading likewise omits hidden stamina/status/part readings");
 Equal(true, noneCompact[2].Text.Contains("+1 more"),
     "all-hidden compact mode retains monster overflow notice");
 Equal(0, MonsterGaugeOverlayRows.Build([], false, partsOnly).Length,
     "empty samples remain empty for any category selection");


 // Manual gauge selection is strictly over the cached roster and never
 // treated as a native active-target signal. A generation change or a missing
 // selected monster clears the override even if a pointer is reused.
 var monsterChoice = new MonsterGaugeOverlaySelection();
 var firstRoster = new[] { observed, secondGauge, thirdGauge };
 monsterChoice.Observe(10, firstRoster);
 Equal(false, monsterChoice.IsManual, "selector defaults to automatic first-two mode");
 Equal(3, monsterChoice.Visible(firstRoster).Count,
     "automatic selection preserves all cached monsters for existing two-row/overflow logic");
 Equal(true, monsterChoice.Select(secondGauge, 10, firstRoster),
     "manual selection accepts a currently sampled monster");
 Equal(true, monsterChoice.IsManual, "manual sampled monster choice is active");
 Equal(1, monsterChoice.Visible(firstRoster).Count,
     "manual mode filters overlay to a single cached monster");
 Equal("Second", monsterChoice.Visible(firstRoster)[0].Name,
     "manual selection presents the chosen monster");
 var reorderedRoster = new[] { thirdGauge, observed, secondGauge };
 monsterChoice.Observe(10, reorderedRoster);
 Equal("Second", monsterChoice.Visible(reorderedRoster)[0].Name,
     "roster reorder cannot silently switch the selected monster");
 var duplicateName = thirdGauge with { Name = "Second" };
 monsterChoice.Observe(10, [duplicateName, secondGauge]);
 Equal((nint)(gaugeMonster + 1), monsterChoice.Visible([duplicateName, secondGauge])[0].Instance,
     "same-name monsters are disambiguated by current entity instance");
 monsterChoice.Observe(10, [observed, thirdGauge]);
 Equal(false, monsterChoice.IsManual,
     "despawn removes the stale manual selection and falls back to automatic");
 Equal(false, monsterChoice.Select(secondGauge, 10, [observed, thirdGauge]),
     "selection rejects an entity absent from the current cached roster");
 Equal(true, monsterChoice.Select(secondGauge, 10, firstRoster),
     "manual selection can be re-established on current roster");
 monsterChoice.Observe(11, firstRoster);
 Equal(false, monsterChoice.IsManual,
     "generation change clears even a reused pointer and identical name");
 Equal(true, monsterChoice.Select(secondGauge, 11, firstRoster),
     "new generation can be selected manually after an explicit action");
 monsterChoice.Observe(11, []);
 Equal(false, monsterChoice.IsManual, "empty/unreadable roster clears manual selection");
 Equal(8, MonsterGaugeOverlaySelection.MaxMenuChoices, "F9 sampled choice list is bounded");
 monsterChoice.Select(secondGauge, 12, firstRoster);
 monsterChoice.Clear();
 Equal(false, monsterChoice.IsManual,
     "hunt-start and automatic-mode reset clears manual selection");


 // Exercise the same production menu model used by F9: ImGui control IDs
 // follow sampled identity + generation rather than the transient row index.
 var uiChoice = new MonsterGaugeOverlaySelection();
 var sameNameA = secondGauge with { Name = "Twin" };
 var sameNameB = thirdGauge with { Name = "Twin" };
 var uiRoster = new[] { observed, sameNameA, sameNameB };
 uiChoice.Observe(30, uiRoster);
 var uiBefore = MonsterGaugeOverlayMenuChoices.Build(uiRoster, 30, uiChoice);
 var uiAfter = MonsterGaugeOverlayMenuChoices.Build(
     [sameNameB, observed, sameNameA], 30, uiChoice);
 Equal(3, uiBefore.Choices.Length, "each current unique sampled monster has an F9 control");
 var aBefore = uiBefore.Choices.Single(c => c.Sample.Instance == sameNameA.Instance);
 var aAfter = uiAfter.Choices.Single(c => c.Sample.Instance == sameNameA.Instance);
 var bBefore = uiBefore.Choices.Single(c => c.Sample.Instance == sameNameB.Instance);
 var bAfter = uiAfter.Choices.Single(c => c.Sample.Instance == sameNameB.Instance);
 Equal(aBefore.ControlId, aAfter.ControlId,
     "mouse-down identity stays attached to the same sampled monster across roster reorder");
 Equal(bBefore.ControlId, bAfter.ControlId,
     "other same-name monster also retains its own control identity after reorder");
 Equal(false, aBefore.ControlId == bBefore.ControlId,
     "same-name monsters never share an ImGui control identity");
 Equal("Twin (sample #1)", aBefore.Label,
     "lowest instance receives a nonpersistent duplicate-name marker");
 Equal("Twin (sample #2)", bBefore.Label,
     "other same-name instance is clearly distinguished");
 Equal(aBefore.Label, aAfter.Label,
     "duplicate-name marker is independent of roster position");
 Equal(bBefore.Label, bAfter.Label,
     "other duplicate-name marker is also position-independent");
 Equal(false, uiBefore.Choices.Any(c => c.Selected),
     "automatic selection displays no misleading selected-row state");
 Equal(true, uiChoice.Select(sameNameB, 30, uiRoster),
     "manual selection chooses an exact currently cached monster");
 var uiSelected = MonsterGaugeOverlayMenuChoices.Build(
     [sameNameB, observed, sameNameA], 30, uiChoice);
 Equal(1, uiSelected.Choices.Count(c => c.Selected),
     "F9 reflects exactly one manually selected control");
 Equal(true, uiSelected.Choices.Single(c => c.Selected).Sample.Instance == sameNameB.Instance,
     "selected-row feedback follows the sampled monster rather than its position");
 Equal(bBefore.ControlId,
     uiSelected.Choices.Single(c => c.Selected).ControlId,
     "selected label state never changes the parent ImGui ID");

 // Check the actual button-label string passed from the production menu
 // model into SmallButton, not just the PushID(ControlId) parent scope.
 // Dear ImGui hashes the suffix after ### (unlike ##, which also hashes
 // the visible label); the final scoped identity must survive selection.
 static string ComposedImGuiButtonIdentity(MonsterGaugeOverlayMenuChoice choice)
 {
     var label = choice.ButtonLabel;
     var marker = label.IndexOf("###", StringComparison.Ordinal);
     if (marker < 0)
         throw new Exception("F9 menu button must use Dear ImGui's ### ID separator");
     return choice.ControlId + "/" + label[(marker + 3)..];
 }
 Equal("Show###pick", bBefore.ButtonLabel,
     "unselected button uses the stable ###pick widget ID suffix");
 var bSelected = uiSelected.Choices.Single(c => c.Selected);
 Equal("Selected###pick", bSelected.ButtonLabel,
     "selected button changes only the visible label before ###");
 Equal(ComposedImGuiButtonIdentity(bBefore),
     ComposedImGuiButtonIdentity(bSelected),
     "full composed F9 widget identity remains stable when selected label changes");
 Equal(ComposedImGuiButtonIdentity(bBefore),
     ComposedImGuiButtonIdentity(bAfter),
     "full composed F9 widget identity remains stable across roster reordering");
 Equal(false, ComposedImGuiButtonIdentity(aBefore) ==
     ComposedImGuiButtonIdentity(bBefore),
     "two same-name monsters still have distinct full composed button identities");
 var uiNextEpoch = MonsterGaugeOverlayMenuChoices.Build(uiRoster, 31, uiChoice);
 Equal(false, aBefore.ControlId == uiNextEpoch.Choices.Single(
     c => c.Sample.Instance == sameNameA.Instance).ControlId,
     "pointer reuse across generations cannot inherit a previous active button ID");
 var renamedGauge = sameNameA with { Name = "Renamed" };
 var uiRenamed = MonsterGaugeOverlayMenuChoices.Build(
     [observed, renamedGauge, sameNameB], 30, uiChoice);
 Equal(false, aBefore.ControlId == uiRenamed.Choices.Single(
     c => c.Sample.Instance == renamedGauge.Instance).ControlId,
     "same pointer with changed name also has a distinct control ID");
 var duplicateRecord = MonsterGaugeOverlayMenuChoices.Build(
     [sameNameA, sameNameA, sameNameB], 30, uiChoice);
 Equal(2, duplicateRecord.Choices.Length,
     "duplicate cached identity never creates duplicate ImGui button IDs");
 Equal(0, duplicateRecord.MoreCount,
     "duplicate records do not invent menu overflow");
 var menuOverflowRoster = Enumerable.Range(0, MonsterGaugeOverlaySelection.MaxMenuChoices + 2)
     .Select(i => observed with { Instance = gaugeMonster + i + 100, Name = $"Sample {i}" })
     .ToArray();
 var boundedMenu = MonsterGaugeOverlayMenuChoices.Build(menuOverflowRoster, 30, uiChoice);
 Equal(MonsterGaugeOverlaySelection.MaxMenuChoices, boundedMenu.Choices.Length,
     "F9 menu choice count remains bounded to eight");
 Equal(2, boundedMenu.MoreCount,
     "additional distinct monsters are acknowledged");
 Equal(boundedMenu.Choices.Length, boundedMenu.Choices.Select(c => c.ControlId).Distinct().Count(),
     "all rendered ImGui controls have unique stable identities");
 uiChoice.Observe(31, uiRoster);
 Equal(false, MonsterGaugeOverlayMenuChoices.Build(uiRoster, 31, uiChoice)
     .Choices.Any(c => c.Selected), "generation change clears selected-row feedback");


 // A stale *age* is only a display mask, not evidence that the selected
 // monster despawned. Test the same availability-aware selection path used
 // by both the regular HUD and F9.
 var staleSelection = new MonsterGaugeOverlaySelection();
 var selectedRoster = new[] { observed, secondGauge, thirdGauge };
 var freshRoster = new MonsterGaugeOverlayAvailability(true, true, true, 3, 0.3);
 var staleRoster = freshRoster with { SampleAgeSeconds = 3.1 };
 staleSelection.ObserveAvailability(80, selectedRoster, freshRoster);
 Equal(true, staleSelection.Select(thirdGauge, 80, selectedRoster),
     "manual user selection may target the third sampled monster");
 Equal((nint)thirdGauge.Instance, staleSelection.Visible(selectedRoster)[0].Instance,
     "chosen third sampled monster is displayed before a sampling gap");
 staleSelection.ObserveAvailability(80, null, staleRoster);
 staleSelection.ObserveAvailability(80, selectedRoster, staleRoster);
 Equal(true, staleSelection.IsManual,
     "both HUD and F9 stale frames retain pending manual monster choice");
 Equal(false, staleRoster.MayRenderGauges,
     "stale data and pending selection cannot make cached gauge values visible");
 staleSelection.ObserveAvailability(80,
     [secondGauge, thirdGauge, observed], freshRoster);
 Equal(true, staleSelection.IsManual,
     "successful fresh same-generation sample restores manual selection");
 Equal((nint)thirdGauge.Instance, staleSelection.Visible(
     [secondGauge, thirdGauge, observed])[0].Instance,
     "fresh roster reorder does not return selected third monster to automatic first-two");

 staleSelection.ObserveAvailability(80, selectedRoster, staleRoster);
 staleSelection.ObserveAvailability(80, [observed, secondGauge],
     freshRoster with { CachedMonsterCount = 2 });
 Equal(false, staleSelection.IsManual,
     "fresh observation missing selected monster clears the pending choice");
 Equal(2, staleSelection.Visible([observed, secondGauge]).Count,
     "missing selected monster falls back to automatic selection");

 Equal(true, staleSelection.Select(thirdGauge, 80, selectedRoster),
     "selection can be restored for generation-invalidation regression");
 staleSelection.ObserveAvailability(81, selectedRoster, staleRoster);
 Equal(false, staleSelection.IsManual,
     "epoch change during stale interval clears even an identical reused instance");
 Equal(true, staleSelection.Select(thirdGauge, 81, selectedRoster),
     "explicit manual choice can resume on the newly observed generation");
 staleSelection.ObserveAvailability(81, null,
     freshRoster with { CaptureEnabled = false });
 Equal(false, staleSelection.IsManual,
     "disabled capture still immediately clears stale manual preference");
 Equal(true, staleSelection.Select(thirdGauge, 81, selectedRoster),
     "selection can resume for empty-roster regression");
 staleSelection.ObserveAvailability(81, [],
     freshRoster with { CachedMonsterCount = 0 });
 Equal(false, staleSelection.IsManual,
     "fresh empty enumeration remains a real invalidation of manual choice");

 // The production poller resets the manual display token on roster changes,
 // even if the same address/name appears after an observed HP generation reset.
 var selectionPoller = new MonsterGaugePoller();
 MonsterGaugeState[] SelectionRead(IReadOnlyList<MonsterState> _) => [observed];
 var lowHealthGeneration = gaugeInput with { Health = 200 };
 selectionPoller.Tick(700, true, true, [lowHealthGeneration], SelectionRead);
 var stableEpoch = selectionPoller.SelectionEpoch;
 selectionPoller.Tick(700.1, true, true, [lowHealthGeneration], SelectionRead);
 Equal(stableEpoch, selectionPoller.SelectionEpoch,
     "stable sampled roster leaves manual UI token unchanged");
 var nextGeneration = gaugeInput with { Health = 900 };
 selectionPoller.Tick(700.2, true, true, [nextGeneration], SelectionRead);
 Equal(stableEpoch + 1, selectionPoller.SelectionEpoch,
     "large HP generation reset invalidates manual selection token");
 selectionPoller.Tick(700.3, false, true, [nextGeneration], SelectionRead);
 Equal(stableEpoch + 2, selectionPoller.SelectionEpoch,
     "disabled native capture invalidates existing manual selection token");
 selectionPoller.Tick(700.4, false, true, [nextGeneration], SelectionRead);
 Equal(stableEpoch + 2, selectionPoller.SelectionEpoch,
     "repeated disabled polls do not churn selection token");


 // Availability/provenance is a pure display classification over the same
 // existing capture gates and cache. It does not prove host authority, read
 // anything natively or mistake no data for a measured zero.
 var availabilityOff = new MonsterGaugeOverlayAvailability(false, true, true, 2);
 Equal(MonsterGaugeAvailabilityKind.Disabled, availabilityOff.Kind,
     "off has precedence even if a just-disabled cache still holds old samples");
 Equal(false, availabilityOff.MayRenderGauges, "off suppresses stale cached HUD readings");
 Equal(false, availabilityOff.ShowEmptyHudHint, "off does not clutter default HUD");
 Equal(true, availabilityOff.StatusText.Contains("disabled"),
     "F9 reports capture disabled explicitly");

 var availabilityBlocked = new MonsterGaugeOverlayAvailability(true, false, true, 2);
 Equal(MonsterGaugeAvailabilityKind.UnsupportedBuild, availabilityBlocked.Kind,
     "unconfirmed or mismatched build cannot report cached readings as current");
 Equal(false, availabilityBlocked.MayRenderGauges,
     "build mismatch suppresses old cached HUD values");
 Equal(true, availabilityBlocked.StatusText.Contains("421810")
     && availabilityBlocked.StatusText.Contains("matching map"),
     "F9 explains the hard build and map requirement");

 var availabilityOutside = new MonsterGaugeOverlayAvailability(true, true, false, 2);
 Equal(MonsterGaugeAvailabilityKind.OutsideQuest, availabilityOutside.Kind,
     "old cached monster readings do not survive a quest boundary in the UI");
 Equal(false, availabilityOutside.MayRenderGauges,
     "no quest means no displayed current monster samples");
 Equal(false, availabilityOutside.ShowEmptyHudHint,
     "no quest does not add an in-hunt empty-sample hint");

 var availabilityEmpty = new MonsterGaugeOverlayAvailability(true, true, true, 0);
 Equal(MonsterGaugeAvailabilityKind.NoCurrentSamples, availabilityEmpty.Kind,
     "empty sampled roster is reported as unknown rather than measured zero");
 Equal(false, availabilityEmpty.MayRenderGauges,
     "empty roster cannot show sampled monsters");
 Equal(true, availabilityEmpty.ShowEmptyHudHint,
     "enabled in-quest preview shows a concise no-current-samples HUD hint");
 Equal(true, availabilityEmpty.StatusText.Contains("no current")
     && availabilityEmpty.StatusText.Contains("not a zero"),
     "empty status explicitly rejects fabricated zero");

 var availabilitySampled = new MonsterGaugeOverlayAvailability(true, true, true, 2, 0.5);
 Equal(MonsterGaugeAvailabilityKind.LocalUnverified, availabilitySampled.Kind,
     "sampled data never graduates to verified-host classification");
 Equal(true, availabilitySampled.MayRenderGauges,
     "only explicitly enabled and supported in-quest samples can be rendered");
 Equal(false, availabilitySampled.ShowEmptyHudHint,
     "populated cache does not emit no-data hint");
 Equal(true, availabilitySampled.StatusText.Contains("Local-unverified")
     && availabilitySampled.StatusText.Contains("NOT verified host state"),
     "F9 provenance is explicit even with two sampled monsters");

 // On an F9 gate transition, the plugin clears old cached observations
 // immediately. The current frame's status must also treat pre-toggle
 // samples as unavailable rather than showing them as newly measured.
 var toggledOn = availabilityOff with { CaptureEnabled = true, CachedMonsterCount = 0 };
 Equal(MonsterGaugeAvailabilityKind.NoCurrentSamples, toggledOn.Kind,
     "enabling capture does not report samples from the preceding disabled state");
 Equal(MonsterGaugeAvailabilityKind.Disabled,
     (availabilitySampled with { CaptureEnabled = false }).Kind,
     "status recomputes immediately when F9 experimental capture is switched off");
 Equal(MonsterGaugeAvailabilityKind.NoCurrentSamples,
     (availabilitySampled with { CachedMonsterCount = -1 }).Kind,
     "invalid negative cache count never fabricates a sample");


 // Source-only, process-monotonic freshness tracking for cached readings.
 // Renderer may be invoked even when the 100 ms update loop is blocked.
 var freshnessPoller = new MonsterGaugePoller();
 var freshnessReads = 0;
 MonsterGaugeState[] ReadForFreshness(IReadOnlyList<MonsterState> _)
 {
     freshnessReads++;
     return [observed];
 }
 Equal<double?>(null, freshnessPoller.SampleAgeSeconds(810),
     "new poller has no sample-age evidence");
 freshnessPoller.Tick(810, true, true, [gaugeInput], ReadForFreshness);
 Equal(1, freshnessReads, "initial freshness sample uses one existing poller read");
 Equal(0d, freshnessPoller.SampleAgeSeconds(810),
     "first successful reading gets a monotonic timestamp");
 freshnessPoller.Tick(810.4, true, true, [gaugeInput], ReadForFreshness);
 Equal(1, freshnessReads, "skipped subsecond tick never adds a native read");
 Equal(true, Math.Abs(freshnessPoller.SampleAgeSeconds(811.3)!.Value - 1.3) < 0.0001,
     "skipped poll does not refresh age of the same cached reading");
 Equal<double?>(null, freshnessPoller.SampleAgeSeconds(809),
     "clock rollback never makes old samples appear fresh");
 Equal<double?>(null, freshnessPoller.SampleAgeSeconds(double.NaN),
     "non-finite monotonic time cannot prove freshness");
 var ageBeforeReacquire = freshnessPoller.SampleAgeSeconds(813);
 Equal(MonsterGaugeAvailabilityKind.StaleSamples,
     new MonsterGaugeOverlayAvailability(true, true, true,
         freshnessPoller.Current.Length, ageBeforeReacquire).Kind,
     "rendering after stalled updates hides old sampled values");
 freshnessPoller.Tick(813, true, true, [gaugeInput], ReadForFreshness);
 Equal(2, freshnessReads, "next normal tick reacquires without changing cadence");
 Equal(0d, freshnessPoller.SampleAgeSeconds(813),
     "successful re-read restores a zero-age timestamp");
 freshnessPoller.Tick(814.2, true, true, [gaugeInput], _ => []);
 Equal<double?>(null, freshnessPoller.SampleAgeSeconds(814.2),
     "successful empty capture has no sampled timestamp");
 freshnessPoller.Tick(815.3, true, true, [gaugeInput], ReadForFreshness);
 Equal(0d, freshnessPoller.SampleAgeSeconds(815.3),
     "fresh reading after empty capture starts a new age");
 freshnessPoller.Tick(815.4, false, true, [gaugeInput], ReadForFreshness);
 Equal<double?>(null, freshnessPoller.SampleAgeSeconds(815.4),
     "disabled capture clears the timestamp and cached rows");
 freshnessPoller.Tick(815.5, true, true, [gaugeInput], ReadForFreshness);
 Equal(0d, freshnessPoller.SampleAgeSeconds(815.5),
     "re-enabled capture starts with a new native observation");
 freshnessPoller.Tick(816.6, true, true, [gaugeInput],
     _ => throw new InvalidOperationException("synthetic read teardown"));
 Equal<double?>(null, freshnessPoller.SampleAgeSeconds(816.6),
     "reader failure invalidates sample age alongside cached observations");

 var freshnessNow = new MonsterGaugeOverlayAvailability(true, true, true, 1, 2.5);
 Equal(MonsterGaugeAvailabilityKind.LocalUnverified, freshnessNow.Kind,
     "exact conservative 2.5-second boundary is still displayable, not verified");
 var freshnessLate = freshnessNow with { SampleAgeSeconds = 2.5001 };
 Equal(MonsterGaugeAvailabilityKind.StaleSamples, freshnessLate.Kind,
     "values older than the bound are stale instead of locally current");
 Equal(false, freshnessLate.MayRenderGauges,
     "stale cached values are not rendered");
 Equal(true, freshnessLate.ShowStaleHudHint,
     "stale cached values have an explicit in-hunt HUD warning");
 Equal(false, freshnessLate.ShowEmptyHudHint,
     "stale cache is not falsely classified as an empty enumeration");
 Equal(true, freshnessLate.StatusText.Contains("Stale")
     && freshnessLate.StatusText.Contains("hidden"),
     "F9 and diagnostics explain that stale values are suppressed");
 Equal(MonsterGaugeAvailabilityKind.StaleSamples,
     (freshnessNow with { SampleAgeSeconds = null }).Kind,
     "unknown sample age cannot establish a current observation");
 Equal(MonsterGaugeAvailabilityKind.StaleSamples,
     (freshnessNow with { SampleAgeSeconds = -0.01 }).Kind,
     "negative age cannot establish a current observation");
 Equal(MonsterGaugeAvailabilityKind.StaleSamples,
     (freshnessNow with { SampleAgeSeconds = double.PositiveInfinity }).Kind,
     "infinite sample age cannot establish a current observation");
 Equal(MonsterGaugeAvailabilityKind.Disabled,
     (freshnessLate with { CaptureEnabled = false }).Kind,
     "disabled-capture precedence still overrides stale cache");
 Equal(MonsterGaugeAvailabilityKind.UnsupportedBuild,
     (freshnessLate with { BuildReady = false }).Kind,
     "unsupported-build precedence still overrides stale cache");
 Equal(MonsterGaugeAvailabilityKind.OutsideQuest,
     (freshnessLate with { InQuest = false }).Kind,
     "outside-quest precedence still overrides stale cache");
 Equal(MonsterGaugeAvailabilityKind.NoCurrentSamples,
     (freshnessLate with { CachedMonsterCount = 0 }).Kind,
     "zero cached monsters are missing, not stale samples");


 // F9 receives structured diagnostics: only this owned boundary inserts
 // the live gauge state after processing the capture checkbox. A multiline
 // free-form field with the exact same prefix cannot spoof the owned row.
 var diagnostics = new MonsterGaugeOverlayDiagnostics(
     "Plugin build: regression\nStatus: unchanged\nPart capture: unchanged\n",
     "Recorder: unchanged\n");
 var enabledNoSamples = availabilityOff with
 {
     CaptureEnabled = true, CachedMonsterCount = 0, SampleAgeSeconds = null
 };
 var afterEnableDiagnostics = diagnostics.Render(enabledNoSamples);
 Equal("Plugin build: regression\nStatus: unchanged\nPart capture: unchanged\n" +
     $"Monster gauges: {enabledNoSamples.StatusText}\n" +
     "Recorder: unchanged\n", afterEnableDiagnostics,
     "same-frame F9 enable structurally renders only the post-toggle no-data state");

 var afterDisableDiagnostics = diagnostics.Render(
     availabilitySampled with { CaptureEnabled = false });
 Equal(true, afterDisableDiagnostics.Contains(
     $"Monster gauges: {availabilityOff.StatusText}\n"),
     "same-frame F9 disable structurally renders the off state");
 Equal(true, diagnostics.Render(freshnessLate).Contains(
     $"Monster gauges: {freshnessLate.StatusText}\n"),
     "stale age status is consistently rendered by the structured formatter");
 Equal(true, diagnostics.Render(availabilitySampled).Contains(
     $"Monster gauges: {availabilitySampled.StatusText}\n"),
     "local-unverified state is consistent with dedicated F9 status");

 var spoofedDiagnostics = new MonsterGaugeOverlayDiagnostics(
     "Status: first line\nMonster gauges: spoofed local observation\n" +
     "Part capture: unchanged\n",
     "Recorder: unchanged\nMonster gauges: spoofed trailing observation\n");
 var spoofedRendered = spoofedDiagnostics.Render(enabledNoSamples);
 Equal("Status: first line\nMonster gauges: spoofed local observation\n" +
     "Part capture: unchanged\n" +
     $"Monster gauges: {enabledNoSamples.StatusText}\n" +
     "Recorder: unchanged\nMonster gauges: spoofed trailing observation\n",
     spoofedRendered,
     "untrusted preceding/trailing exact-prefix rows remain untouched and never replace the owned row");
 Equal(true, spoofedDiagnostics.Render(availabilityOff).Contains(
     $"Part capture: unchanged\nMonster gauges: {availabilityOff.StatusText}\nRecorder:"),
     "rendering off updates only the controlled gauge position even with exact-prefix collisions");
 Equal("Monster gauges: " + enabledNoSamples.StatusText + "\n",
     new MonsterGaugeOverlayDiagnostics("", "").Render(enabledNoSamples),
     "empty diagnostic sections still compose the owned gauge row without a marker");
 Equal("End\nMonster gauges: " + availabilityOff.StatusText + "\nSuffix without newline",
     new MonsterGaugeOverlayDiagnostics("End\n", "Suffix without newline").Render(availabilityOff),
     "non-terminated trailing diagnostics are preserved exactly");
 
 // Real settings.json roundtrip, including a legacy file with no presentation
 // field; the experimental capture gate must remain false by default.
 var overlaySettingsDir = Path.Combine(Path.GetTempPath(), "mhw-gauge-rows-" + Guid.NewGuid().ToString("N"));
 Directory.CreateDirectory(overlaySettingsDir);
 try
 {
     var defaultSettings = PluginSettings.Load(overlaySettingsDir);
     Equal(false, defaultSettings.ExperimentalMonsterGauges,
         "new installs still do not opt into native monster gauge reads");
     Equal(false, defaultSettings.MonsterGaugeOverlayDetails,
         "new installs get compact gauge rows");
     Equal(true, defaultSettings.MonsterGaugeShowStamina, "new installs show sampled stamina");
     Equal(true, defaultSettings.MonsterGaugeShowAilments, "new installs show sampled ailments");
     Equal(true, defaultSettings.MonsterGaugeShowParts, "new installs show sampled normal parts");
     File.WriteAllText(Path.Combine(overlaySettingsDir, PluginSettings.FileName),
         """{"overlayVisible":false,"experimentalMonsterGauges":false,"trialDurationSeconds":45}""");
     var legacySettings = PluginSettings.Load(overlaySettingsDir);
     Equal(false, legacySettings.MonsterGaugeOverlayDetails,
         "existing settings without a detail field stay compact");
     Equal(false, legacySettings.ExperimentalMonsterGauges,
         "legacy preferences do not gain native gauge access");
     Equal(true, legacySettings.MonsterGaugeShowStamina, "legacy preferences show stamina");
     Equal(true, legacySettings.MonsterGaugeShowAilments, "legacy preferences show ailments");
     Equal(true, legacySettings.MonsterGaugeShowParts, "legacy preferences show parts");
     legacySettings.MonsterGaugeShowStamina = false;
     legacySettings.MonsterGaugeShowAilments = true;
     legacySettings.MonsterGaugeShowParts = false;
     legacySettings.MonsterGaugeOverlayDetails = true;
     legacySettings.Save();
     var persisted = PluginSettings.Load(overlaySettingsDir);
     Equal(true, persisted.MonsterGaugeOverlayDetails,
         "the details preference survives settings.json reload");
     Equal(false, persisted.ExperimentalMonsterGauges,
         "presentation preference cannot enable native reads on reload");
     Equal(false, persisted.MonsterGaugeShowStamina, "stamina preference survives reload");
     Equal(true, persisted.MonsterGaugeShowAilments, "ailment preference survives reload");
     Equal(false, persisted.MonsterGaugeShowParts, "part preference survives reload");
     persisted.MonsterGaugeShowAilments = false;
     persisted.Save();
     var allHidden = PluginSettings.Load(overlaySettingsDir);
     Equal(false, allHidden.MonsterGaugeShowStamina || allHidden.MonsterGaugeShowAilments
         || allHidden.MonsterGaugeShowParts, "all-disabled visibility preferences persist without enabling capture");
     Equal(false, allHidden.ExperimentalMonsterGauges, "all-disabled display does not change capture state");
     Equal(false, persisted.OverlayVisible,
         "existing overlay visibility survives gauge preference update");
     Equal(45, persisted.TrialDurationSeconds,
         "existing training duration survives gauge preference update");
 }
 finally
 {
     Directory.Delete(overlaySettingsDir, recursive: true);
 }


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
SafeMemory.Values[gaugeMonster + 0x1BC48] = (nint)(-1);
var negativePointerAilments = MonsterGaugeReader.Read([gaugeInput]).Single().Ailments;
Equal(2, negativePointerAilments.Length,
    "negative invalid middle pointer is skipped instead of ending the scan");
Equal("Poison", negativePointerAilments[1].Name,
    "later valid poison meter survives corrupt signed pointer");
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
Equal<bool?>(true, clockJumpSamples[1].ClockBoundary,
    "clock boundary persists across a throttled poll to next recorded reading");
Equal(1, integrationRecorder.GaugeClockDiscontinuities, "clock source switch recorded once");

// Normalization persists but the boundary is a one-time occurrence. Three
// post-reset samples at the 2s cadence must share a continuous curve.
foreach (var (rawT, monoT) in new[] { (12.5f, 404.5), (14.7f, 406.7), (16.9f, 408.9) })
    integrationRecorder.ObserveGauges(rawT, monoT, "local", [observed]);
var postResetSamples = integrationRecorder.GaugeSamples();
Equal(5, postResetSamples.Length, "three valid samples recorded after reset");
Equal(1, postResetSamples.Count(s => s.ClockBoundary == true),
    "only one actual discontinuity is marked");
Equal(true, postResetSamples.Skip(1).All(s => s.ClockAdjusted),
    "persistent normalization offset remains visible");
Equal(true, postResetSamples.Skip(2).All(s => s.ClockBoundary == false),
    "subsequent records explicitly have no new boundary");
Equal(1, integrationRecorder.GaugeClockDiscontinuities,
    "post-reset samples do not invent clock discontinuities");
Equal(true, System.Text.Json.JsonSerializer.Serialize(postResetSamples[2]).Contains("\"clockBoundary\":false"),
    "new recordings serialize explicit false for older-log compatibility");

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
    // Restore the original native-detour fixture after the pointer-lifetime
    // scenarios; the following exception-unwind regression still expects a
    // large-monster hit to be accepted.
    damage.UpdateMonsters([target]);
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
