# Clothesline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A taut chain swept across an enemy at Knockdown Speed or faster floors them along the sweep, and the spin carries on.

**Architecture:** A plain class `Clothesline` in `Ruby.Grapple`, owned by `GrappleController` beside `RopeWraps` and
stepped right after `Tether.Solve`. Each physics step it compares the rope's last span (last wrap or Ruby's centre of
mass, to the anchor) with last step's, finds bodies carrying an `IKnockdown` inside the strip swept between them, and
floors any crossed at Knockdown Speed through a new `IKnockdown.KnockDown(Vector3 launchVelocity)`.

**Tech Stack:** Unity 6000.5.10f1, C# 9 / .NET Standard 2.1, PhysX queries.

**Spec:** [docs/specs/2026-11-22-chain-feel.md](../specs/2026-11-22-chain-feel.md), section "Step 3: the clothesline".

## Global Constraints

- No namespaces. Private serialized fields `[SerializeField] private float _name`, with tooltips in the house style.
- `Ruby.Grapple` references only `Ruby.Core` and the Input System: never a `Ruby.Game` type. It reaches enemies only
  through `IKnockdown` (Core).
- Never write a character's Rigidbody velocity to push it: the launch goes through `KnockDown(launchVelocity)` and its
  owner decides (ragdoll at that velocity).
- The tether never moves Ruby; the clothesline never hits Ruby or the host.
- No silent fallbacks: a missing Impact Rules reference logs an error and turns the clothesline off, saying so.
- Threshold: `ImpactRules.KnockdownSpeed`. Launch = rope's flat sideways velocity at the crossing × **Clothesline
  Fling**, default **0.5**. No damage.
- Only a **taut** rope counts: path length ≥ length − 0.05 m.
- Hits log `CLOTHESLINE <name> at <speed> m/s` through `DebugLog.Info`. Enemies are "they/them".
- Comments explain why, naming the failure avoided.
- No test framework is in use (CLAUDE.md): verification is `python tools/compile_check.py` (exit 0) plus a play-test
  by the user. Never recompile or toggle Play in the user's Editor without a fresh, unchained `isPlaying` check.

## Review Focus

1. **Rope catches on a pillar mid-spin.** The span's pivot jumps from Ruby to the corner in one step; the strip
   between the two spans is meaningless and must not floor anyone. Expected: that step tests nothing.
2. **Enemy close to Ruby inside a short spin.** Rope there moves slower than Knockdown Speed. Expected: they stay up,
   the rope passes through as today.
3. **First step after the whip lands (or after a re-attach).** There is no previous span. Expected: record only.
4. **A ragdolled enemy's many bone colliders.** Expected: at most one knockdown call per `IKnockdown` per step, and a
   body already down stays down (the enemy's own `KnockDown` guards).
5. **Rope goes slack after a fan-out ends.** Expected: no hits once the rope is no longer taut.

Each is pinned by a play-test check in Task 2.

---

### Task 1: Knockdown with a launch velocity

**Files:**
- Modify: `Assets/Scripts/Core/IKnockdown.cs`
- Modify: `Assets/Scripts/EnemyController.cs:449-461` (`KnockDown`)
- Modify: `Assets/Scripts/PlayerController.cs:168-180` (`KnockDown`)

**Interfaces:**
- Produces: `IKnockdown.KnockDown(Vector3 launchVelocity)`: ragdoll at `launchVelocity` instead of the body's own.
  `KnockDown()` stays, unchanged in behaviour, and becomes a call to the new member with the body's own velocity
  (enemy: `VelocityUtil.Flatten(Body.linearVelocity)`; Ruby: `_myRigidbody.linearVelocity`). `Bullet` and
  `ImpactDamage` keep calling `KnockDown()`.

- [ ] **Step 1: Add `void KnockDown(Vector3 launchVelocity);` to `IKnockdown`** with a summary: knocked down and sent
  off at this velocity (the chain's clothesline), where `KnockDown()` uses the velocity the hit left. No default
  interface implementation (keep both members explicit in each implementer).
- [ ] **Step 2: `EnemyController.KnockDown(Vector3 launchVelocity)`** carries today's body, with `launchVelocity`
  passed to `Ragdoll.Launch` and to `Ragdoll.Relaunch` (the getting-up case). Same guards: dead, grabbed, or down and
  not standing, does nothing.
- [ ] **Step 3: `PlayerController.KnockDown(Vector3 launchVelocity)`** carries today's body, ragdolling at
  `launchVelocity`.
- [ ] **Step 4: Compile.** Run `python tools/compile_check.py`. Expected: exit 0, both passes.
- [ ] **Step 5: Commit** `Assets/Scripts/Core/IKnockdown.cs`, `EnemyController.cs`, `PlayerController.cs` with an
  imperative subject ("Let a knockdown send the body off at a given velocity") and a prose body on why (the
  clothesline floors along its sweep; a character's velocity is never written from outside).

### Task 2: The clothesline

**Files:**
- Create: `Assets/Scripts/Grapple/Clothesline.cs` (Unity writes its `.meta` on import; commit it)
- Modify: `Assets/Scripts/Grapple/Tether.cs` (one property)
- Modify: `Assets/Scripts/Grapple/GrappleController.cs` (fields, construction, `StepAttached`, gizmos)
- Modify: `docs/architecture/grapple.md`, `docs/architecture/impact-and-ragdolls.md`,
  `docs/specs/2026-11-22-chain-feel.md`, `docs/architecture/project-history.md`, `docs/manual/design-manual.html`,
  `docs/CODENAME-Ruby-Design-Manual.pdf`

**Interfaces:**
- Consumes: `IKnockdown.KnockDown(Vector3 launchVelocity)` (Task 1); `ImpactRules.KnockdownSpeed`;
  `RopeWraps.Points` (nearest Ruby first, so the last is the host side's pivot); `Tether.Anchor`, `Tether.Host`.
- Produces:
  - `Tether.IsTaut` (`public bool`): `_pathLength >= _length - 0.05f`, the tolerance a `const` with a why-comment.
  - `class Clothesline` (internal, like `RopeWraps`):
    - `public Clothesline(Rigidbody player)`
    - `public void Reset()`: forget the previous span (called on attach and on every detach path).
    - `public void Step(Vector3 pivot, Vector3 anchor, bool pivotIsWrap, Vector3 wrapPoint, Rigidbody host, bool taut, float knockdownSpeed, float fling, float dt)`
      (the builder may fold `pivotIsWrap`/`wrapPoint` into one "pivot identity" check as long as a changed pivot is
      detected).
    - `public void DrawGizmos()`: the last strip tested, as four lines (gold if anyone was floored that step,
      grey otherwise).

- [ ] **Step 1: `Tether.IsTaut`.** Add the property next to `Taut`.

- [ ] **Step 2: `Clothesline.Step`, the rules it must keep:**
  - Not taut, or no previous span, or the pivot changed identity since last step (wrapped, unwrapped, or a different
    last wrap point: compare with `== ` on the stored `Vector3`, since `RopeWraps` keeps points fixed until they
    unwrap): store this span and return. (Review Focus 1, 3, 5.)
  - Candidates: `Physics.OverlapSphereNonAlloc(pivot, spanLength + 1f, buffer[32], ~0, QueryTriggerInteraction.Ignore)`.
    Skip colliders under Ruby (`IsChildOf(player.transform)`) or under the host (`host != null &&
    IsChildOf(host.transform)`), and those with no `IKnockdown` in parents. One test per `IKnockdown` per step (a
    small reused `List<IKnockdown>` cleared each step). (Review Focus 4.)
  - Body on the floor plane: centre = flattened `collider.bounds.center`; radius = a `CapsuleCollider`'s radius ×
    the larger of its transform's lossy x/z scale, else `max(bounds.extents.x, bounds.extents.z)`.
  - Crossed: the circle meets the flat quad (prevPivot, prevAnchor, anchor, pivot): centre inside either triangle
    of the quad, or within radius of any of its four edges. This is what keeps a fast spin from stepping over
    someone, however far it turns in a step.
  - Speed at the crossing: `t` = the body's centre projected onto this step's span, clamped 0..1;
    `v = (lerp(pivot, anchor, t) − lerp(prevPivot, prevAnchor, t)) / dt`, flattened, minus its component along the
    span. At `|v| >= knockdownSpeed`: `knockdown.KnockDown(v * fling)` and
    `DebugLog.Info($"CLOTHESLINE {name} at {speed:0.0} m/s")` where `name` is the collider's root name. Below it,
    nothing. (Review Focus 2: near Ruby `t` is small, so `v` is.)
  - Store this span for the next step.

- [ ] **Step 3: Wire it in `GrappleController`.**
  - New header `[Header("Clothesline")]` with `[SerializeField] private ImpactRules _impactRules;` (tooltip: the
    shared rules asset; its Knockdown Speed is the speed a swept chain floors at) and
    `[SerializeField, Range(0f, 1f)] private float _clotheslineFling = 0.5f;` (tooltip: a floored enemy is sent off
    at this share of the rope's speed where it crossed them; 0.5 keeps a 25 m/s sweep's launch under Knockdown Speed
    so they don't floor the next by bumping; 1 for chain reactions through a crowd).
  - In `Awake`: missing `_impactRules` → `Debug.LogError` naming the field and saying the chain won't clothesline;
    the clothesline then never steps. Construct `_clothesline` beside `_wraps`.
  - `StepAttached`, right after `_tether.Solve(_wraps.Points)`: pivot = last wrap if any, else
    `_body.worldCenterOfMass`; call `Step` with `_tether.Anchor`, `_tether.Host`, `_tether.IsTaut`,
    `_impactRules.KnockdownSpeed`, `_clotheslineFling`, `Time.fixedDeltaTime`.
  - `Reset()` wherever `_wraps.Clear()` is called, and in `LandWhip`.
  - `OnDrawGizmosSelected` (new) calls `_clothesline?.DrawGizmos()` while attached.

- [ ] **Step 4: Compile.** Run `python tools/compile_check.py`. Expected: exit 0, both passes. If `Clothesline.cs`
  logs from inside `#if RUBY_DEBUG_LOGS`, say so (the check doesn't compile that code); prefer `DebugLog.Info`,
  which needs no `#if`.

- [ ] **Step 5: Inspector work for the user.** Tell them: on Red's `GrappleController`, assign the project's
  `ImpactRules` asset to **Impact Rules** (the same asset `ImpactDamage` uses). Until then the Console shows the
  error from Step 3 and nothing clotheslines.

- [ ] **Step 6: Play-test checklist for the user** (with `RUBY_DEBUG_LOGS` on to see hits):
  1. A wide spin through a group: those the outer rope crosses go down, once each in the log, over the way the rope
     swept; the spin keeps going.
  2. An enemy within about a metre of Ruby during a short spin stays up. (Review Focus 2)
  3. Spin until the rope catches on a pillar: nobody near the pillar is floored on the catch step. (Review Focus 1)
  4. A rope at rest, or Ruby walking round an anchor, still bends round enemies and floors no one.
  5. Let go of a spin: the fan-out floors anyone it sweeps fast; once the rope hangs slack, nothing. (Review Focus 5)
  6. A floored enemy swept again while down doesn't log a second hit; one getting up is floored again. (Review Focus 4)
  7. The Scene view shows the swept strip with Red selected.

- [ ] **Step 7: Docs.**
  - `grapple.md`: a **clothesline** paragraph after the pass-through paragraph (the rule, taut only, last span,
    swept strip and why, speed along the rope, fling and its default, no damage, the pivot-change skip, Impact
    Rules wiring and its error).
  - `impact-and-ragdolls.md`: the knockdown paragraph gains `KnockDown(launchVelocity)` and its one caller.
  - Spec: status line says step 3 built, awaiting play-test; note that `Clothesline` is owned by the
    `GrappleController` beside `RopeWraps` (not the `Tether`, which the design named) because the controller holds
    the wraps and the settings, and the gizmo lives there (the tether has none).
  - `project-history.md`: a line under the chain-feel work for step 3.
  - Manual: in the spin section ("Letting go of a spin" in `docs/manual/design-manual.html`) add a short paragraph
    on the clothesline, and a tuning-table row for Clothesline Fling (GrappleController, 0.5). Reprint the PDF with
    headless Chrome: `"C:\Program Files\Google\Chrome\Application\chrome.exe" --headless --disable-gpu
    --no-pdf-header-footer --print-to-pdf="docs\CODENAME-Ruby-Design-Manual.pdf" docs\manual\design-manual.html`
    (if Chrome isn't there, say so rather than skip it). Check the page count changed sensibly.

- [ ] **Step 8: Commit** after the user's play-test, with the code, the `.meta`, the docs and the PDF. Imperative
  subject ("Floor enemies a fast chain sweeps across"), prose body on the swept-strip choice over a trigger, the
  fling default, no damage, and the owner change from the spec.
