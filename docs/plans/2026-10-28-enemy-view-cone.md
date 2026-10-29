# Enemy View Cone Implementation Plan (Enemy States 0.2, piece 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Stop at the play-test gate for the user. Never hand-edit `.prefab` or `.unity` YAML.

**Goal:** Officers notice Ruby in a rounded pie in front of them plus a small circle all round, instead of a 12.5 m circle, and idle officers aggro on sight like everyone else.

**Architecture:**
- `EnemyController` gains a view area test, `InView(point)`: the cone (View Angle, View Range) or the close circle (Close Radius), measured flat.
- `DetectsPlayer` becomes `InView(Ruby) && CanSeePlayer()`, and the table gains Idle → Aggro on it.
- Aggro's contact test (`InContactWithPlayer`: Give Up Radius and sight, all round) is untouched, so a fight doesn't flicker when he runs facing away from her.
- `_alertRadius` is renamed `_viewRange`, with `FormerlySerializedAs` so the prefab keeps its 12.5 m.

**Tech Stack:** Unity 6000.5.10f1, C# 9 (.NET Standard 2.1). Assembly touched: `Ruby.Game` only.

**Spec:** `docs/specs/2026-10-28-enemy-states-0.2.md`, piece 1 (approved 2026-10-28). Read its piece 1 section and the rows of **What the player sees** about sneaking up and idle officers.

## Global Constraints

- **Values:** View Angle 100° (centred on the flattened body facing). View Range is the old Alert Radius's value (12.5 m on `officer.prefab`), carried over by the rename. Close Radius 2 m. Give Up Radius stays at least View Range + 1 m (`OnValidate`).
- **The cone is for noticing only.** It gates Idle, Patrol and Searching → Aggro. Aggro's contact test doesn't change.
- **Sight still needs the ray** (`CanSeePlayer`), inside the close circle too: a wall between them hides her.
- **States never name each other.** The new transition goes in `EnemyController.BuildTransitions`.
- **Conventions:**
  - no namespaces;
  - private `[SerializeField]` `_camelCase` fields with tooltips and expression-bodied read properties;
  - comments explain why;
  - gizmos for anything spatial;
  - no silent fallbacks.
- **Verification:**
  - `python tools/compile_check.py` must end `compile-check: OK`.
  - There are no automated tests. The play-test gate is manual, so don't call it a test.

## Review Focus

1. **The rename drops the prefab's value.** Without `[FormerlySerializedAs("_alertRadius")]`, every officer silently resets to the code default. Expect `officer.prefab`'s officers to show View Range 12.5 in the Inspector. Owned by Task 1, Step 1, and checked in the gate.
2. **A tilted body tilts the cone.** `transform.forward` isn't flat while he's grabbed or getting up. Expect `InView` to flatten both the facing and the direction to the point, so height never changes the angle.
3. **Ruby on top of him.** At a flat distance of 0, the direction has no angle. Expect the close circle to answer first, so she's in view. With Close Radius set to 0 she's still in view, because a zero direction counts as in front.
4. **Idle and unarmed.** An unarmed idle officer who sees Ruby with a gun in reach must go to Aggro first (declared first, as Patrol's is), then Rearm from Aggro, as a patroller does today.
5. **The gizmo in edit mode.** There's no player reference to test then. Expect it to draw grey without calling `CanSeePlayer`, as today's sight line does.

---

### Task 1: The pie-shaped view, and idle officers aggro on sight

**Files:**
- Modify: `Assets/Scripts/EnemyController.cs` (Awareness fields ~13-22, `BuildTransitions` ~183-204, `DetectsPlayer` ~415, `OnValidate` ~471-475, `OnDrawGizmosSelected` ~487-512, plus a flat-arc helper beside `DrawFlatCircle`)
- Modify after the gate: `CLAUDE.md` (Enemy AI: **Sight** and **Detection** bullets), `docs/manual/design-manual.html` (tuning table row "Alert Radius / Give Up Radius", the sight paragraph, the States table's Idle row, the **Idle** section), `docs/CODENAME-Ruby-Design-Manual.pdf` (rebuilt), and the spec (piece 1's "12 m" to 12.5 m, and its Progress tick)

**Interfaces:**
- Produces: `public bool InView(Vector3 point)` on `EnemyController`. It reports whether a point lies in the view cone or the close circle, measured flat, with no sight ray. Piece 3 uses it for bodies and loose guns.

- [x] **Step 1: Replace Alert Radius with the view fields.** In the Awareness header:
  - `[FormerlySerializedAs("_alertRadius")] [SerializeField, Min(0f)] private float _viewRange = 12f;` (add `using UnityEngine.Serialization;`), tooltip "How far he sees in front of him, within View Angle, if in sight."
  - `[SerializeField, Range(1f, 360f)] private float _viewAngle = 100f;`, tooltip "How wide the view in front of him is, in degrees, centred on his facing."
  - `[SerializeField, Min(0f)] private float _closeRadius = 2f;`, tooltip "Whichever way he faces, he notices Ruby this close, if in sight, so she can't stand right behind him."
  - Update Give Up Radius's tooltip to say it sits outside View Range.

- [x] **Step 2: Add `public bool InView(Vector3 point)`.**
  - Work on the flat offset from `transform.position` to `point` (y zeroed) and the flattened `transform.forward`.
  - True when the flat distance is at most `_closeRadius`.
  - Otherwise true when the distance is at most `_viewRange` and `Vector3.Angle(flatForward, flatOffset) <= _viewAngle * 0.5f`.
  - A zero offset counts as in view.
  - Summary comment: why the close circle exists, and that sight is a separate test.

- [x] **Step 3: Detection uses the view.**
  - `DetectsPlayer() => _player != null && InView(_player.transform.position) && CanSeePlayer();`
  - Its comment becomes "noticed: in his view, and in sight".
  - In `BuildTransitions`, add `At(Idle, Aggro, new FuncPredicate(DetectsPlayer));` beside the Patrol and Searching lines, declared before `At(Idle, Rearm, ...)`.
  - `OnValidate`: `_giveUpRadius = Mathf.Max(_giveUpRadius, _viewRange + 1f);`, with its comment naming View Range.

- [x] **Step 4: The gizmo draws the view.** In `OnDrawGizmosSelected`:
  - Replace the Alert Radius sphere with the cone (its two edges and the arc at View Range) and the close circle, flat at eye height.
  - Colour: in play with a player, green when `InView(player) && CanSeePlayer()` and red when `InView(player)` but not in sight. Grey otherwise, and always grey in edit mode.
  - Add `private static void DrawFlatArc(Vector3 centre, Vector3 forward, float angle, float radius)` next to `DrawFlatCircle`, in the same segment style.
  - Keep the Give Up Radius sphere, the gun's band and the play-mode sight line.

- [x] **Step 5: Compile.** Run `python tools/compile_check.py`. Expected: ends `compile-check: OK`.

- [x] **Step 6: Play-test gate (the user), in `SampleScene`:**
  - **Values:** the officers' Inspector shows View Range 12.5, View Angle 100, Close Radius 2. Selected, an officer draws a cone and a small circle, not a 12.5 m sphere.
  - **From behind:** Ruby walks up behind a patrolling officer and stops 3 m behind him. He doesn't react. She steps within 2 m, and he aggros.
  - **From the side:** at about 8 m and well off his facing (past 50°), he doesn't react. In front of him within 12.5 m, he does.
  - **Cover:** a crate between them in his cone hides her, and the gizmo turns red.
  - **Idle:** the officer with no route aggros when Ruby walks into his cone in sight. Before this change he only reacted to being hurt.
  - **Fights:** in Aggro, Ruby circles behind him. He keeps fighting, and loses her only after Aggro Time out of sight or beyond Give Up Radius, as before.
  - **Searching:** a searching officer notices her only in his cone or close circle.

- [x] **Step 7: Commit the code.**

  ```bash
  git add Assets/Scripts/EnemyController.cs
  git commit
  ```

  The message is imperative, with a prose body that says why the cone gates only noticing and why Idle now aggros. End it with the Co-Authored-By line.
  Note: the code commit (d1ca3af) came before the gate, and the play-test passed on 2026-10-28. A follow-up commit gave a pitched officer no cone, only the close circle.

- [x] **Step 8: Bring the docs up to date.**
  - **CLAUDE.md, Enemy AI:**
    - **Sight** keeps the ray rule.
    - **Detection** becomes: in his view (a 100° cone out to View Range, plus a 2 m circle all round) **and** in sight → Aggro, from Idle, Patrol or Searching. Aggro keeps contact all round within Give Up Radius.
    - Remove "Idle officers hold their post"; idle officers now aggro on sight.
  - **Manual (`docs/manual/design-manual.html`):**
    - The tuning row becomes "View Range / View Angle / Close Radius / Give Up Radius — 12.5 m / 100° / 2 m / 16.5 m".
    - The sight paragraph describes the cone and the circle.
    - The States table's Idle row leaves on "Sees Ruby or is hurt → Aggro".
    - The **Idle** section says an idle officer holds his post until he sees Ruby or is hurt.
  - **The PDF:** rebuild it with headless Chrome (the current one is Skia/PDF from Chromium):
    `"/c/Program Files/Google/Chrome/Application/chrome.exe" --headless --disable-gpu --no-pdf-header-footer --print-to-pdf="E:/Unity/Projects/CODENAME-Ruby/docs/CODENAME-Ruby-Design-Manual.pdf" "file:///E:/Unity/Projects/CODENAME-Ruby/docs/manual/design-manual.html"`.
    Open it and check the edited pages before committing.
  - **The spec:** in piece 1, "the current Alert Radius, 12 m" becomes 12.5 m. Tick `- [x] 1. The pie-shaped view` under Progress.

- [x] **Step 9: Commit the docs.**

  ```bash
  git add CLAUDE.md docs/manual/design-manual.html docs/CODENAME-Ruby-Design-Manual.pdf docs/specs/2026-10-28-enemy-states-0.2.md docs/plans/2026-10-28-enemy-view-cone.md
  git commit
  ```

  Tick this plan's steps in the same commit.

- [ ] **Step 10: End-of-piece review, in the background.** Dispatch a fresh reviewer agent over the branch's piece 1 commits, with `run_in_background`, and tell the user it's running. Act on what it finds before piece 2's check-in.
