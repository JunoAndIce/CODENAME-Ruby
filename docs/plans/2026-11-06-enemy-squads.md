# Fighting Together: Squads and Fire Lanes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enemies fighting Ruby fight as a squad: up to four in the main squad, the rest on standby behind them, spreading out to spots of their own and never firing through each other.

**Architecture:** A `Fight` is a plain object held by a new `FightBoard`, which `Intel` owns and ticks at execution order -20, like `JobBoard`. A fight never moves anyone: `AggroState` reads his role and plays it, so the machine stays the one place movement is decided. Scoring lives in a plain `FiringSpots`. Firing is still not a state.

**Tech Stack:** Unity 6000.5.10f1, C# 9. New scripts go in `Assets/Scripts/Enemies/`, in `Ruby.Game`.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 6. Read it first.

## Decisions the spec leaves, made here

1. **Paths don't cost more through a squadmate's lane.** The spec suggests it, but the chase field is one shared flood per floor and can't hold a detour per enemy, and it would need re-flooding as lanes move. Spot scoring keeps them out of each other's lanes, Personal Space keeps them apart while walking, and the hold-and-step-aside rule covers the rest. Noted in the spec as deferred.
2. **Membership is polled.** `FightBoard` drops anyone no longer in Aggro or Rearm each tick, and moves up a replacement. Hooks in `AggroState.Exit` can't tell where he's going, and Rearm must keep his place: an unarmed member is still in the fight.
3. **Joining has no distance limit (user, 2026-11-06).** Anyone who goes to Aggro joins the fight on his floor with the member nearest him, however far away, or starts one if his floor has none. A fight only takes those already in Aggro, so it doesn't call the level by itself. *(User: "if the fight pulls in the whole level, I think its fine.")* For example, patrollers wandering into Ruby's lying body join the officers aimed at it, until there are animations for after she's killed. Fights are per floor, because floors are independent.
4. **A fight's contact is shared.** The spec's "when every member has been out of contact": a member's Aggro Time doesn't run out while a fight mate is in contact. He stays in, chasing what he's told.
5. **Who's on his feet.** A member grabbed, ragdolled or dead is dropped at once, by rule 2. Getting up enters Aggro, so he rejoins through the normal route: main if there's room, standby if not, as the spec's "rejoins on standby if his place has been filled".

## Global Constraints

- **The player's verbs are untouched.** Nothing here slows or moves Ruby.
- **No serialized field moves between components.** New tuning goes on `EnemyController` or `Intel`, in the right Inspector group, with a tooltip. Unity saves by name, so adding fields is safe. Never hand-edit `.unity`, `.prefab` or `.asset`.
- **States never name each other.** Transitions stay in `BuildTransitions`, `SetState` on the controller. Firing and moving aren't states.
- **No silent fallbacks.** Anything that can't be done as designed warns, or is a stated rule that shows in the log.
- **One scratch flood at a time** (`FloorReach`). Nothing here floods per enemy per step. Rays are budgeted per scan, staggered, never all-pairs per frame.
- **Names say Enemy, never Officer.** Comments explain why. No namespaces.
- **The manual stays out of it** until all of enemy states is finished. `CLAUDE.md` is updated in each task.
- **The working tree holds the user's uncommitted tuning.** Never stage: `Assets/BehaviourMap.asset`, `Assets/StimulusRules.asset`, `Assets/Prefabs/officer.prefab`, `Assets/Prefabs/Table.prefab`, `Assets/Scenes/SampleScene.unity`, `Assets/Settings/PC_RPAsset.asset`, `Assets/Settings/SampleSceneProfile.asset`, `Assets/Scripts/PlayerController.cs`, `Assets/Scripts/Enemies/EnemyVision.cs`, any `Squad/` path, any `.mkv`, `docs/plans/Enemy knowledge notes.md`. Stage named files only. Don't write `.meta` files.
- **Verification:** `python tools/compile_check.py` ends `compile-check: OK`. There are no automated tests: each task's gate is the user's play.

## Review Focus

1. **Enemies dropped mid-tick.** A member destroyed, dead or grabbed while being read, and a move-up with nobody on standby.
2. **Flicker.** Roles or spots changing every frame: re-picks are throttled, and a current spot keeps a bonus so a near-equal one doesn't win.
3. **An unarmed member.** He has no band or lane, chases as today, and still holds a squad place.
4. **Knowing too much.** Out of sight he goes on what he knows. Spots are scored around where he knows she is, never her true position, beyond Sight Follow Through.
5. **Cost with many enemies.** Scoring is per enemy about once a second, staggered, with a ray budget. Say so if it can't hold.

---

### Task 1: Squads

**Files:** create `Assets/Scripts/Enemies/Fight.cs`, `Assets/Scripts/Enemies/FightBoard.cs`. Modify `Enemies/Intel.cs`, `States/AggroState.cs`, `EnemyController.cs`, `Enemies/EnemyGizmos.cs`, `CLAUDE.md`.

**Interfaces**

- **`Fight`:** `IReadOnlyList<EnemyController> Main`, `Standby`; `bool IsMain(EnemyController)`; `Id`; `ToString()` for logs.
- **`FightBoard`:** `Fight Join(EnemyController who)`, `void Advance()`, `Fight FightOf(EnemyController)`, `bool AnyInContact(Fight, EnemyController except)`.
- **`Intel`:** forwards those. New `_mainSquadCap` = 4, in its job and squad tuning, with a tooltip.
- **`EnemyController`:** `public Fight Fight { get; internal set; }`, `public bool IsStandby`. New `_standbyDistance` = 3 m under **Fighting**: how much farther back than the main squad standby holds.

- [x] **Step 1: The board.** `AggroState.Enter` calls `Intel.Join`. Joining: the fight on his floor per decision 3, else a new one; on no grid he fights alone, with one warning; a main place if `Main.Count < cap`, else standby. Logs a callout: "Enemy (3) joins fight #1 on standby". `Advance`, called from `Intel` beside the job board, drops members per decision 2 and moves up the standby member nearest Ruby into any open main place ("moves up"). A fight with nobody in it closes. With no Intel, he fights alone, as already reported at Start.
- [x] **Step 2: Shared contact.** `AggroState.LostPlayer`: not lost while `AnyInContact` holds for someone else in his fight. Keep the rule that the table reads this before Tick.
- [x] **Step 3: Standby holds back.** In `AggroState`, an armed standby member with Ruby in sight treats his band as starting at the farthest main member's distance from her plus Standby Distance, instead of the gun's Min Distance, and doesn't close nearer. He still fires when she's in range and in sight, and out of sight chases what he knows, stopping at that distance from it. An unarmed one stays as he is. Main behaves exactly as today.
- [x] **Step 4: Gizmos and docs.** With `Intel` selected in Play, a line between a fight's main members and a ring per role: main red, standby light grey. `CLAUDE.md`: a **Fights** bullet under Enemy AI, and remove "There is no cap on that yet" from Speech.
- [x] **Step 5: Compile and commit.**
- [x] **Step 6: Play-test gate (the user).** The scene needs more than four enemies for this: duplicate a few officers. Five or six on her: four close in and the rest hang back and shoot when they can. Kill or grab a main member: a standby member moves up at once ("moves up"). The grabbed one gets up and goes on standby. A member behind a wall doesn't give up while another has her.

### Task 2: Firing spots

**Files:** create `Assets/Scripts/Enemies/FiringSpots.cs`. Modify `States/AggroState.cs`, `Enemies/Fight.cs`, `EnemyController.cs`, `Enemies/EnemyGizmos.cs`, `CLAUDE.md`.

**Interfaces**

- **`FiringSpots`:** `bool TryPick(EnemyController who, Fight fight, Vector3 ruby, out Vector3 spot)`.
- **`Fight`:** claims: `Claim(who, spot)`, `Release(who)`, `IsClaimedNear(spot, spacing, except)`.
- **`EnemyController`:** new `_squadSpacing` = 2 m under **Fighting**.

- [x] **Step 1: Candidates.** Open cells on rings around where he knows Ruby is: 16 angles and 3 radii across the band (main: the gun's Min Distance to Range; standby: Task 1's distance onward, within Range). `PathGrid.IsWalkable`, and on ground he can reach.
- [x] **Step 2: Score.** A weighted sum, weights as named constants: not within `_squadSpacing` of a claim (a rejection, not a weight), near where he stands, inside the band's middle, spread round her (a different angle from his squadmates'), not on a squadmate's line to her. Then a **clear shot** ray from the spot at eye height, tested only on the best few, up to a budget. Keep his current spot unless a new one beats it by a margin.
- [x] **Step 3: Walk and re-pick.** An armed member with her in sight (or following through) walks to his spot and holds, facing her, firing as today. Re-pick about once a second, staggered, or at once when she has moved past half the spacing beyond his band or his shot is blocked. The main squad picks before standby because `Main` is walked first when claims are contested. No spot found: he falls to Task 1's band behaviour, with a log line, once per fight.
- [x] **Step 4: Backing away** still applies inside Min Distance, and cornered he stands and fights, as today.
- [x] **Step 5: Gizmos and docs.** Selected enemy: his spot, and a line to it. `CLAUDE.md`.
- [x] **Step 6: Compile and commit.**
- [x] **Step 7: Play-test gate (the user).** They spread round her in a loose crossfire instead of a clump, and never share a spot. Running past them, they shift without shuffling constantly. A wall between a spot and her: nobody picks it.

### Task 3: Fire lanes

> **Built and taken out (user, 2026-11-07: "remove fire lanes").** Built in `c9965f0` and `f07e390`, reverted. In play, spots were dropped for being backed away from Ruby on the walk in 829 times out of 1,111 picks (23 of about 96 before lanes), and the squads read as broken: "We're getting too deep." The steps below are left unticked on purpose.

**Files:** modify `States/AggroState.cs`, `Enemies/EnemyVision.cs` only once the user's uncommitted edit is committed (otherwise a new `Enemies/FireLane.cs`), `Enemies/FiringSpots.cs`, `CLAUDE.md`, `docs/specs/2026-10-28-enemy-states-0.2.md`.

**Interfaces**

- **`FireLane`:** `static EnemyController FirstInLane(EnemyController who, Fight fight, Vector3 target)`.

- [ ] **Step 1: The check.** Before firing: a body-wide capsule from his muzzle to Ruby against his fight's members, each moved a moment ahead along their velocity. No physics query is needed: it's a distance from a line segment, a handful of members, so it is cheap. Not limited to squadmates on a job: anyone in the fight.
- [ ] **Step 2: Hold.** Someone in his lane: he holds fire, and his aim stays up. If that squadmate is moving out of it, he waits a beat. Otherwise he re-picks a spot now, with the lane rule weighted heavily, and steps there. The callout: "Enemy (3) holds: Enemy (4) is in his lane".
- [ ] **Step 3: Docs.** `CLAUDE.md`. The spec's Progress ticks piece 6, and notes that paths costing more through lanes is deferred, and why (decision 1).
- [ ] **Step 4: Compile and commit.** Then the end-of-plan review, in the background, on the most capable model.
- [ ] **Step 5: Play-test gate (the user).** Nobody fires through a squadmate. One who's blocked steps aside and fires. A shield on the chain still stops bullets as before. The fight still feels dangerous, with no long pauses where nobody shoots. This last one is the check that decides the tuning.
