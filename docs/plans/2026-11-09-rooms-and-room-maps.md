# Rooms, Room Maps and the Firing Range Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Levels get rooms: a search sweeps the rooms its circle reaches and runs until it has seen everything, each room can carry its own behaviour map, speech fills a room, and officers can practise at a firing range that hides gunshots while it's busy.

**Architecture:** A `Room` is a scene component made of axis-aligned rectangles with a behaviour map. A plain `RoomIndex`, which `Intel` owns, finds every room at load, works out which holds which, and answers "which room is this point in". Every sense already asks `Intel.TryGetRow(point, kind)`, so room maps slot in behind that one call. A group Search's area becomes a `SearchArea` (the rooms its circle reaches over open ground, and the circle's other ground), which `SearchCoverage` clears as it does today. The firing range is a `RangeSpot` that an idle officer's post can be set to.

**Tech Stack:** Unity 6000.5.10f1, C# 9. New scripts go in `Assets/Scripts/Enemies/` (`Ruby.Game`). The editor goes in `Assets/Scripts/Editor/` (`Ruby.Game.Editor`). The one Pathing change stays free of game types.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 7 (and its notes on piece 8). Read it first. The "Later" sections (other activity spots, alarms) are out of scope.

## Decisions the spec leaves, made here

1. **A room's name is its GameObject's name.** No second name field that could drift from the Hierarchy.
2. **Rectangles are stored relative to the room's transform** (an offset and a size on X and Z, never rotated), so moving the room object moves its rooms. Rotation on the transform is ignored, and `OnValidate` warns if it isn't zero about Y.
3. **"Holds" is tested on the ground.** Room A holds room B when every sample of B's rectangles, at the grid's cell size, lies inside A. Two rooms on one floor whose rectangles overlap while neither holds the other get one warning each at load, naming both. Rooms are per floor: a room's floor is `PathGrid.At` of its first rectangle's centre, and a room on no grid warns once and is left out.
4. **Search Duration's job role goes.** A group Search's step has no length and ends only when everything is seen and the minimum has passed. `EnemyController.SearchDuration` stays for a search of his own (a glance, or no `Intel`), and its tooltip says so. `Intel`'s Gunshot Extension is removed with `GroupJob.ExtendStep`: a gunshot in the area already throws away what was seen, which now starts the minimum over too, since a new coverage's clock starts when it is built.
5. **The minimum lives on `Intel`** as Search Minimum Time (5 s), beside Search Area Radius: it is a job's rule, and the job board reads it once.
6. **A point is unclearable** when a member who claimed it can't reach it (Blocked or Unreachable), or walks to it and arrives, or runs out Search Travel Limit, without it being seen. It is dropped with a warning naming the point and the reason. A search with no ground at all keeps its warning and ends after the minimum, which the warning now says.
7. **The range shooter's facing is the target, not the spot's arrow.** The spot's forward is used only when it has no target, which warns.

## Global Constraints

- **The player's verbs are untouched.** Nothing here slows or moves Ruby.
- **No serialized field moves between components.** New tuning goes on `EnemyController`, `Intel`, `Room`, `RangeSpot` or `BehaviourMap`, in the right Inspector group, with a tooltip. Adding fields is safe; removing Gunshot Extension drops its saved value, which is intended. Never hand-edit `.unity`, `.prefab` or `.asset`.
- **`Ruby.Pathing` never learns about rooms.** `PathGrid` gains only a general flood with a test passed in.
- **States never name each other.** Transitions stay in `BuildTransitions`, `SetState` on the controller. Practising isn't a state: it is something `IdleState` does at a range post.
- **No silent fallbacks.** Anything that can't be done as designed warns, or is a stated rule that shows in the log.
- **One scratch flood at a time** (`FloorReach`). Search areas flood once when a job is made or moves, never per step.
- **Names say Enemy, never Officer.** Comments explain why. No namespaces.
- **The manual stays out of it** until all of enemy states is finished. `CLAUDE.md` is updated in each task.
- **The working tree holds the user's uncommitted tuning.** Never stage: `Assets/BehaviourMap.asset`, `Assets/StimulusRules.asset`, `Assets/Prefabs/officer.prefab`, `Assets/Prefabs/Table.prefab`, `Assets/Prefabs/Pistol.prefab`, `Assets/Scenes/SampleScene.unity`, `Assets/Settings/PC_RPAsset.asset`, `Assets/Settings/SampleSceneProfile.asset`, `Assets/Scripts/PlayerController.cs`, `Assets/Scripts/Squad.meta`, any `.mkv`, `docs/plans/Enemy knowledge notes.md`. Stage named files only. Don't write `.meta` files; Unity makes them, and the user commits them.
- **Verification:** `python tools/compile_check.py` ends `compile-check: OK`. There are no automated tests: each task's gate is the user's play.

## Review Focus

1. **Nesting and overlap.** Three levels of rooms, a floor-sized room with offices in it, two rooms touching edge to edge (that is not an overlap), and an L-shaped room of two rectangles. The smallest room wins for maps, and only rooms holding no rooms are searched whole.
2. **A search that never ends.** A point behind a prop nobody can see, a member who leaves mid-search, every member dropped: the search still ends, with a warning for each dropped point.
3. **Walls.** A circle poking through a wall never marks the room behind it. Speech never fills a floor-sized room. A mask measured in a straight line reaches through walls on its own floor, and never onto another floor.
4. **Map fall-through.** A row unset in an office comes from the floor room, then the default. A row "only while in use" falls through while the room isn't in use. A Body row set to Ignore in a room is refused with a warning, never used.
5. **Cost.** A search area floods once per job (and again only when its place moves). Room lookups are a scan of a short list per call, no allocation. The range adds nothing per frame for enemies who aren't practising.

---

### Task 1: Rooms

**Files:** create `Enemies/Room.cs`, `Enemies/RoomIndex.cs`, `Editor/RoomEditor.cs`; modify `Enemies/Intel.cs`, `CLAUDE.md`.

**Interfaces (produced)**

- **`Room : MonoBehaviour`**
  - `[Serializable] public struct Rect { public Vector3 Offset; public Vector2 Size; }`
  - Fields: `_map` (BehaviourMap, may be empty: a room with no map uses the room around it), `_rects` (List<Rect>, one 4 x 4 m rectangle by default), `_inUseTime` (10 s, used by Task 5).
  - `BehaviourMap Map`, `string Name => name`, `int RectCount`, `GridArea AreaOf(int index)` (a world `GridArea.Rectangle`).
  - `bool Contains(Vector3 point)` (flat, inside any rectangle).
  - `float DistanceOutside(Vector3 point)` (flat distance to the nearest rectangle, 0 inside).
  - Set by the index: `PathGrid Floor`, `Room Parent` (the smallest room holding it, or null), `bool HoldsRooms`.
- **`RoomIndex`**, a plain class: `RoomIndex(Object context)` finds every enabled `Room` once.
  - `IReadOnlyList<Room> Rooms`.
  - `Room SmallestAt(Vector3 point)`: on `PathGrid.At(point)`'s floor.
  - `Room LeafAt(Vector3 point)`: the smallest room there if it holds no rooms, else null.
- **`Intel`** builds it in `Awake` beside the boards, and forwards `RoomAt(point)` (`SmallestAt`), `LeafRoomAt(point)` and `Rooms`.

- [x] **Step 1: `Room` and `RoomIndex`** per the interfaces and decisions 1 to 3. The index sorts each floor's rooms smallest area first, so `SmallestAt` returns the first that contains the point. Overlap and no-grid warnings name the rooms and say what to do.
- [x] **Step 2: Gizmos.** `Room.OnDrawGizmos` draws each rectangle's outline, faintly when not selected. A label carries the name and the map's name, or "(map from the room around it)". With the map's Mask Radius above 0 (Task 2 adds it; draw it once that exists), a dashed outline that far out.
- [x] **Step 3: `RoomEditor`.** Scene-view handles as `EnemyControllerEditor` gives patrol rectangles: move a rectangle by its centre, size it by its edges, with Undo. Inspector buttons add and remove a rectangle. Reuse the patrol editor's drawing code by moving what both need into a small static helper in `Editor/` rather than copying it.
- [x] **Step 4: Docs.** `CLAUDE.md`: a **Rooms** bullet under Enemy AI (what a room is, nesting, the overlap warning, decisions 1 to 3), and `Room`/`RoomIndex` in the Assemblies table's `Ruby.Game` row.
- [x] **Step 5: Compile and commit.**
- [x] **Step 6: Play-test gate (the user).** Place a floor-sized room and two offices inside it, and one more room overlapping an office's edge. The outlines and labels draw, the handles size them, and entering Play warns once about the overlapping pair and nothing else.

### Task 2: Behaviour maps per room, and speech across a room

**Files:** modify `Enemies/BehaviourMap.cs`, `Enemies/Intel.cs`, `Enemies/EnemySenses.cs`, `Enemies/Room.cs` (gizmo), `CLAUDE.md`.

**Interfaces**

- Consumes: Task 1's `RoomIndex`, `Room.Map`, `Room.Parent`, `Room.DistanceOutside`, `Room.Floor`.
- Produces:
  - `BehaviourMap.Row` gains `public bool OnlyWhileInUse` (tooltip: applies only while the room is in use; otherwise the room around it decides).
  - `BehaviourMap` gains `_maskRadius` (0) and `float MaskRadius`.
  - `bool BehaviourMap.TryGetOwnRow(StimulusKind kind, out Row row)` is the same lookup as `TryGetRow`, clamp included, without the missing-row warning: a room's map leaves rows unset on purpose.
  - `Room.InUse` (false until Task 5 gives it a meaning; Task 2 reads it).
  - **`Intel.TryGetRow(Vector3 point, StimulusKind kind, bool heard, out BehaviourMap.Row row)`** replaces the old signature. `MapAt` goes.

- [x] **Step 1: The lookup.** In this order:
  1. **Mask,** only when `heard`: for each room on the point's floor whose map has `MaskRadius > 0`, with the point outside it but within `MaskRadius` of it (`DistanceOutside`), if that map's own row for the kind applies (not `OnlyWhileInUse`, or the room is in use) and is Ignore, return that row.
  2. **Rooms:** from `SmallestAt(point)` out through `Parent`, the first map whose own row for the kind applies.
  3. **The default map's** `TryGetRow`, which warns about a missing row as today.

  A room row for `StimulusKind.Body` set to Ignore is skipped, with one warning per map naming it. Sight is never masked, because only heard stimuli pass `heard`.
- [x] **Step 2: Callers.** `EnemySenses.Hear` passes `heard: true`. Every other `TryGetRow` call in `EnemySenses` passes `false`. Fix the comment at the glance ("an armory, a morgue"): a room can't make a body ignored, so the morgue example goes.
- [x] **Step 3: Speech across a room.** In `Intel.Say`, while collecting hearers, also take every living listener on the speaker's floor who stands in the speaker's `LeafRoomAt`, and isn't already in range. Leaf only, so a floor-sized room carries nothing. `FindCallees` and Fill are untouched. The log line says how many heard in the room as well as in range.
- [x] **Step 4: Gizmo.** `Room` draws its map's Mask Radius (Task 1, Step 2).
- [x] **Step 5: Docs.** `CLAUDE.md`: the behaviour map bullet says rooms override rows, falling through to the room around them and then the default, plus `OnlyWhileInUse`, Mask Radius (sounds only, straight line, own floor, wins within it), and the Body rule. The Speech bullet says it fills the speaker's room when that room holds no rooms. Armories are a map that ignores Loose Gun.
- [x] **Step 6: Compile and commit.**
- [x] **Step 7: Play-test gate (the user).** An armory room with a map ignoring Loose Gun: an armed enemy walks past a gun lying in it without reacting, and reacts to one just outside. An office map ignoring Impact: a crate thrown into the office brings no one, while one thrown into the corridor does. A shout from inside a small room brings everyone in that room, even those past Speech Range along the floor. A Body row set to Ignore in a room warns and is not used.

### Task 3: Searching by room

**Files:** create `Enemies/SearchArea.cs`; modify `Pathing/PathGrid.cs`, `Enemies/SearchCoverage.cs`, `Enemies/GroupJob.cs`, `Enemies/JobBoard.cs`, `Enemies/Intel.cs`, `States/SearchingState.cs`, `EnemyController.cs` (tooltip only), `CLAUDE.md`.

**Interfaces**

- Consumes: Task 1's `Intel.LeafRoomAt`, `Room.Contains`, `Room.Floor`.
- Produces:
  - **`PathGrid.CollectGround(Vector3 from, float spacing, System.Func<Vector3, bool> inside, List<Vector3> into)`**: the flood `CollectAreaGround` does, with `inside` in place of `area.Contains`. `CollectAreaGround` becomes a call to it.
  - **`SearchArea`**, a plain class:
    - `SearchArea(Vector3 place, float radius, Intel intel)`.
    - `IReadOnlyList<Room> Rooms`: the leaf rooms reached.
    - `IReadOnlyList<Vector3> Ground`: the points to clear, about a metre apart.
    - `bool HasGround`.
    - `bool Contains(Vector3 point)`: within `radius` of the place on its floor, or inside one of `Rooms`.
  - `SearchCoverage(SearchArea area, Object context)` replaces the place-and-radius constructor.
  - `bool SearchCoverage.TryClaimRecheck(EnemyController who, out Vector3 point)`.
  - `void SearchCoverage.Drop(Vector3 point, string why, Object context)`.
  - `GroupJob.Area` (the job's `SearchArea`, built when the job is made and again when its place moves).
  - `Intel.SearchMinimumTime` (5 s, `_searchMinimumTime`, under Group jobs).

- [x] **Step 1: `CollectGround`** per the interface.
- [x] **Step 2: `SearchArea`.**
  1. Flood from the place inside the circle, at cell spacing, with `CollectGround`.
  2. Each reached cell in a `LeafRoomAt` room marks that room. Each reached cell in no leaf room is circle ground: keep it at about 1 m spacing (every n-th cell, as `CollectAreaGround` strides).
  3. Each marked room adds its ground with `CollectGround`, from a reached cell inside it, with `inside` = that room's `Contains`.
  4. Points are kept once each.

  Reaching a room only through a doorway inside the circle falls out of step 1, since the flood never crosses a wall (spec: "touching means reached by open ground").
- [x] **Step 3: Coverage on the area.** `JobBoard.CoverageOf` builds coverage from `job.Area`. The no-ground warning says the team looks about for Search Minimum Time and then stands down.
- [x] **Step 4: No cap.**
  - The Search step has no length: `JobBoard`'s step lengths give Search none, and it waits explicitly, as Go does.
  - The job is Done when coverage is `AllSeen` (or has no ground) **and** `Time.time - coverage.StartedAt >= _intel.SearchMinimumTime`. This replaces the Look Time minimum.
  - A gunshot in the area throws coverage away as today. `ExtendStep`, its call and `Intel._gunshotExtension` go.
  - The Search Duration tooltip says it is the length of a search of his own (a glance, or no Intel), not a group Search's.
- [x] **Step 5: Re-checking.** In `SearchingState.Roam`, when `TryClaimNearest` finds nothing free and the job isn't Done, `TryClaimRecheck` gives a spot to walk to and sweep, at clearing speed. It picks a random point of the area's ground at least a quarter of the area's span from him (the farthest of a few tries), and not within claim spacing of another member's claim. Only when that fails too does he look about where he stands, as today.
- [x] **Step 6: Dropping.** Roam's failures (Blocked or Unreachable, Search Travel Limit, and arriving at a claimed point that is still unseen) call `Drop`, which warns with the point, the member and the reason. `Drop` replaces the quiet `Clear` on those paths. The give-up after several failed spots stays.
- [x] **Step 7: Gizmos.** With `Intel` selected in Play, each Search job's marked rooms are outlined in its colour, and its unseen points are dots.
- [x] **Step 8: Docs.** `CLAUDE.md`'s **A Search clears its area** bullet is rewritten: what the circle marks, no cap, the minimum and re-checking, dropped points, no ground. The Group jobs gunshot bullet loses the extension.
- [x] **Step 9: Compile and commit.**
- [x] **Step 10: Play-test gate (the user).**
  - A gunshot in a corridor between two offices with doors inside the circle: the team clears the corridor and both offices.
  - An office behind a wall within the circle, with its door outside the circle, isn't searched.
  - A search in an open room the team sees at a glance still has them walking and sweeping for about 5 s.
  - A big search runs past 10 s until it's done.
  - A spot nobody can reach is dropped with a warning, and the search still ends.

### Task 4: The search area elsewhere: arriving, gunshots, matching and merging

**Files:** modify `Enemies/JobBoard.cs`, `States/SearchingState.cs`, `CLAUDE.md`.

**Interfaces**

- Consumes: Task 3's `GroupJob.Area` and `SearchArea.Contains`, and `SearchArea.Rooms`.
- Produces: `bool JobBoard.InSearchArea(GroupJob job, Vector3 point)` (`job.Area.Contains(point)`, false with no area), forwarded on `Intel`.

- [ ] **Step 1: In the area.** Every distance-to-place test against Search Area Radius uses `InSearchArea` instead. Those are:
  - `JobBoard` around the gunshot handling and the scout, at the lines comparing `FlatDistance(job.Point, …)` with `SearchAreaRadius`.
  - `SearchingState` where a member counts as in the area. There, a member inside a marked room counts as arrived without the clear line to the place, which only the circle's open ground still needs.
- [ ] **Step 2: Matching.** `JobBoard.Covers` also covers a point inside one of the job's marked rooms, on its floor.
- [ ] **Step 3: Merging.** Two open jobs merge when their places are within Job Match Radius, as today, **or** their marked rooms share a room. Everything else about a merge is unchanged. After a merge that moves the place, the area is rebuilt (Task 3's interface).
- [ ] **Step 4: Docs.** `CLAUDE.md`'s matching, merging and gunshot wording says "in the search area" and what it means.
- [ ] **Step 5: Compile and commit.**
- [ ] **Step 6: Play-test gate (the user).**
  - A thud in the far corner of an office already being searched, beyond 10 m from the job's spot, goes to that job and starts no second one.
  - Two sounds in one big office 12 m apart end as one team.
  - A gunshot in a marked room counts as in the area (the search starts over), not as a shot elsewhere.

### Task 5: The firing range

**Files:** create `Enemies/RangeSpot.cs`; modify `Enemies/Room.cs`, `EnemyController.cs`, `States/IdleState.cs`, `Enemies/EnemyGizmos.cs`, `CLAUDE.md`.

**Interfaces**

- Consumes: Task 1's `Intel.RoomAt`, Task 2's `Room.InUse` and `OnlyWhileInUse`.
- Produces:
  - **`RangeSpot : MonoBehaviour`**: `_target` (Transform). `Vector3 Position`, `Transform Target`, `float FacingYaw` (toward the target, or its own forward with no target: decision 7), and `Room Room`, resolved on first use through `Intel.RoomAt`. A spot in no room warns once: nothing will mask its shots.
  - `Room.NotePractice()` (records the time) and `Room.InUse` (true for In Use Time after the last `NotePractice`).
  - `EnemyController`: `_postSpot` (RangeSpot, under Patrol beside Post Tolerance), `_practiceIntervalMin` (1.2 s) and `_practiceIntervalMax` (2.5 s), `RangeSpot PostSpot`, `IdleState Idle`.
  - `IdleState.Practising` (bool).

- [ ] **Step 1: The post.** With `_postSpot` set, `Start` records the post from the spot (`Position`, `FacingYaw`) instead of where he stands, so he walks there at the start of play.
- [ ] **Step 2: Practising.**
  - At his post in `IdleState`, armed, with a spot that has a target, he faces the target. Each interval (random between min and max) he calls `Room.NotePractice()` and then fires at the target through his `GunHolder` (`AimAt`, `TryFireAt`). Noting first means the shot's own Gunshot is masked.
  - `Practising` is true while he does this.
  - A target out of the gun's range warns once and he doesn't fire.
  - Unarmed at a range post, he stands at it and doesn't practise, with one `DebugLog.Info` line saying why (spec: a stated rule).
  - The shots are real: real bullets, real stimuli.
- [ ] **Step 3: Animation.** `EnemyController.Stance` gives Aim while `Idle.Practising`, and `TryFindTarget` gives the range target then.
- [ ] **Step 4: Gizmos.** The spot draws its standing point, a facing arrow and a line to its target. A selected enemy with a post spot draws a line to it.
- [ ] **Step 5: Docs.** `CLAUDE.md`: a **Firing range** bullet. It covers the post spot, practising, real shots, the in-use rule and its masking (the range's map: Gunshot set to Ignore, Only While In Use, Mask Radius 8). It also covers what an aggressive officer does: walks his Aggressive route if he has one, as the table already sends him, or practises on, wary or not.
- [ ] **Step 6: Compile and commit.**
- [ ] **Step 7: Close the piece.** The spec's Progress ticks piece 7, noting that the other activity spots and alarms wait for assets and piece 5 part B is next. Then run the end-of-plan review, in the background, on the most capable model.
- [ ] **Step 8: Play-test gate (the user).**
  - Make a range room whose map has Gunshot: Ignore, Only While In Use, Mask Radius 8, and post an enemy at a spot with a target.
  - He walks there and fires at the target in the Aim pose.
  - Ruby fires inside the range or within 8 m of it: nobody who doesn't see her comes. She fires 15 m away: they come.
  - When he stops (grabbed, or sent searching), the range goes quiet after 10 s, and a shot there is a shot again.
  - Crossing his lane can get Ruby shot.
