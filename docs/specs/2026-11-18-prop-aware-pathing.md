# Prop-aware pathing

**Status:** approved by the user 2026-11-19; built 2026-11-19 from `docs/plans/2026-11-19-prop-aware-pathing.md`,
awaiting the user's play-test. Step 1 of the enemy AI engine roadmap (see `docs/architecture/project-history.md`).
Where the build differs from this text: the rule reaches the navigator through `IPathAgent.PropRule`, not a
property `EnemyMovement` sets, and the navigator's rule-change line is a `RUBY_DEBUG_LOGS` log (Pathing can't
reach `DebugLog`).

## Intent

Enemies should move through a level the way a person would. They should go round walls and fixed
objects without grazing them. They should leave the room's props where they are unless they're
hunting Ruby. They should also use narrow corridors and doorways as ordinary routes.

Today they hug corners. A table or chair they brush past gets nudged a little each time and ends up
moved. They also avoid tight corridors. Shrinking cells to 0.25 m helped, but it didn't fix either
problem.

The rule depends on what they're doing (the user's words):

- **Aggro and Rearm:** push objects out of the way if they can. Objects of 20 kg or more they never
  even try to push, so they don't waste time on them.
- **Patrol and Search:** never push objects. Respect the room's props and keep them in place, at all
  times.

Idle (the walk back to a post) and every other walk follow the Patrol and Search rule. Only Aggro and
Rearm shove. This is my call: the user named the four states, and the other walks are calm ones.

## Goals

- During calm walks, routes keep a visible gap from every prop, so a passing body doesn't touch it.
- During Aggro and Rearm, routes may go through a light prop (under 20 kg) when going round costs
  noticeably more, and they actually push it aside. Heavy props are walls, with the same gap as
  above.
- Routes never graze walls or fixed objects, even where the grid's cells would allow it.
- A narrow corridor or doorway costs about its length, not several times its length.
- Start and goal points never snap across a wall or prop onto ground on its far side (the bug where a
  guess came back 107 m away by path).

## Non-goals

- Pushing props out of the way on purpose (walking to a crate to clear a door). Shoving only happens
  when a route goes through a prop.
- Enemies choosing which prop to move based on cover or sightlines.
- Ruby's own movement. This only covers enemies.
- Retuning Turn Speed. The prefab's 360 makes wider turning arcs than the code's 720. If grazing is
  left after this work, that's the next lever, and it's an Inspector change.

## Anti-goals (a failure even if it works)

- A calm enemy moves a prop at all, including by grazing it.
- A corridor or doorway that was walkable before is now closed. The gap round props must not seal a
  gap between two walls.
- Movement feels slower or more hesitant than now: stopping in front of a crate, wobbling along a
  table edge, or creeping through doorways.
- A hunting enemy pushes against a table, or anything 20 kg or more, for any length of time.
- Any quiet fallback. When a route, snap or prop can't be handled as designed, it says so (CLAUDE.md
  and the user's standing rule).

## Constraints

- `Ruby.Pathing` references nothing. The grid can't read `Weight`, enemy states or anything in
  `Ruby.Game`. Props read their mass from `Rigidbody.mass`, which `Weight` sets for props. (Only
  ragdolls spread mass over bones, and props don't ragdoll.)
- Flow fields are shared. The chase field toward Ruby is one per floor, and it must stay shared per
  rule, not become one per enemy.
- One scratch flood at a time. `FloorReach` and `FloodFrom` keep their contract.
- Unity can't serialize across components, and no serialized field moves between components.
- Scene and prefab values are the user's. Anything that needs the Inspector is listed for them, never
  patched in YAML.
- Masses are real kg: Crate 10 (Tiny), Small 20, Medium 30, Large 40, Table 50 (Huge), Enemy 50.

## Approaches considered

- **A. One grid that knows each prop's weight (picked).** Props mark their cells as light or heavy.
  Every query names a rule, Careful or Forceful, and reads the layers that rule needs. There's one bake
  and one grid. The cost is extra bookkeeping: a few more per-cell arrays and a clearance per rule.
- **B. Two grids, one per rule.** Each grid's code stays simpler, but every prop marks both grids, the
  memory and bake time double, and keeping the two grids agreeing is another job. Rejected unless A's
  bookkeeping turns out messy.
- **C. Leave the grid alone and steer away from props up close.** It's cheap, but it only reacts.
  Routes would still be planned tight against tables and enemies would wobble along them. At most it's
  a polish on top of A later.

The user picked A.

## Design

### Two rules

```csharp
public enum PropRule { Careful, Forceful }   // in Ruby.Pathing
```

- **Careful:** every resting prop blocks, with a skin round it (below).
- **Forceful:** heavy props block, with the skin. Light props' cells stay open but cost extra.

### What a prop marks

`GridObstacle` decides on each mark whether it's light: `Rigidbody.mass < grid.ShoveLimitKg`. It then
marks two footprints:

- **Core:** today's footprint, the agent radius round each collider.
- **Skinned:** the agent radius plus Prop Skin round each collider.

The grid keeps three counts per cell instead of today's `_propBlocks`:

| Count (per rule) | Careful: what adds to it | Forceful: what adds to it | Read as |
| --- | --- | --- | --- |
| blocks | every prop's core | heavy (or jammed) props' core | not walkable |
| skin | every prop's skin ring | heavy props' skin ring | Prop Skin Cost, and clearance 1 for lines |
| shove | none | light props' core | Shove Cost |

Walkable under a rule means `_walkable[i] && blocks(rule)[i] == 0`. A prop that crosses the limit
(re-weighed while resting) unmarks and re-marks the next time it marks. Mass isn't watched every frame.

**The skin never closes a lane (the user's choice: keep the lane open).** This holds for every gap,
whatever makes it: wall to wall, a prop to a wall, or prop to prop. If skin would close a lane that's
open with core footprints alone, the skin gives way there and those cells keep the core footprint.

In such a lane a calm enemy passes close to the prop. Their legs still treat it as a wall, so at worst
it's a light brush. The user chose that over a crate cutting a room off from patrol and search.

**How (changed while planning, 2026-11-19):** skin cells are walkable but expensive. The first idea,
adding skin only where the core clearance is at least 2, can't work. A skin ring lies within one cell
of the prop's own core cells, so its core clearance is always 1, and that rule would never add skin.

So under the rule a skin cell applies to, stepping onto it adds Prop Skin Cost. For straight lines and
string pulling it reads as clearance 1, so a shortcut can't cut through skin. A route that has another
way never touches the skin. A lane whose only way through is skin stays open, and routes take it only
when going round costs more than squeezing past. That's the user's choice, with no lane check to keep
up to date.

### New grid settings (on `PathGrid`)

| Setting | Default | Meaning |
| --- | --- | --- |
| Shove Limit (kg) | 20 | A prop lighter than this is light: hunting enemies may walk through it. At or above it they never try. |
| Prop Skin (m) | 0.15 | Extra gap kept round props, beyond the agent radius. Walls don't get it. |
| Prop Skin Cost | 60 | Extra path cost, in tenths of a cell step, for stepping onto a skin cell. A lane through skin stays open; routes squeeze through it only when going round costs more. |
| Shove Cost | 30 | Extra path cost, in tenths of a cell step, for each light-prop cell a Forceful route steps onto. Higher means hunters go round more often. |

The limit lives on the grid because every enemy on a floor shares the chase field, so one floor has
one limit. That's my call; per-enemy limits would need per-enemy fields. The tooltips say so.

### Clearance and wall cost, per rule

- Clearance is kept twice, Careful and Forceful, baked and refreshed together (`RefreshClearance`
  already redoes only the box a prop touched).
- **The narrow-corridor fix.** Today `WallPenalty = (MarginCells − clearance + 1) × WallCost`. With
  cells of clearance 1, that's 10 + 20 per step at 0.5 m cells: a one-cell corridor costs 3× its
  length. At 0.25 m cells the margin is 2 cells, so it's 10 + 40, or 5× its length. That's why smaller
  cells made corridors worse to route through, not better.
- The new penalty charges a cell only for the clearance it lacks compared with the best cell near it:

  ```text
  best = min(MarginCells + 1, the highest clearance within MarginCells cells)
  WallPenalty = max(0, best − clearance) × WallCost
  ```

  This works out as follows:
  - **Corridor:** the middle line pays nothing, and the cells against the walls still pay. A corridor
    costs its length, and routes still take its middle.
  - **Open room:** `best` is the cap, so it behaves exactly as today.
  - **Kept as now:** it only raises costs, so A\* stays admissible.

  `best` is baked and refreshed with clearance, one array per rule.
- `HasClearLine`'s required clearance becomes `best` at each cell, not a flat `MarginCells + 1`. That
  way a straight line down a corridor's middle is accepted, not broken into many corners. This
  replaces today's ramp near the goal only if it gives the same result there; otherwise the ramp stays.
  The builder checks this.

### Precise shortcuts

Every straight-line shortcut is confirmed against real shapes before it's taken, not just against
cells:

- One capsule cast from the body along the line.
- Radius = agent radius + Prop Skin. Height = the grid's agent band.
- It hits static colliders and resting `GridObstacle` colliders. Under Forceful it ignores light
  props, which it may walk through.

If the cast hits, the shortcut is refused and the field or route step is used. Only the shortcut
about to be used is cast, never each look-ahead candidate, so it costs at most one cast per enemy per
replan.

### The snap doesn't cross walls

`NearestWalkable` (and `FlowField`'s start and goal snap through it) stops searching rings
regardless of walls. The bake adds a **solid** array: a cell is solid when its centre, at probe height,
is inside a static collider (a point test, no agent radius). Props mark solid cells too, from a
zero-radius footprint. The snap becomes a small breadth-first search from the cell:

- It walks only through cells that aren't solid.
- It stops at the first walkable cell (under the query's rule) within Snap Radius.
- If it finds none, it returns false, as today. Callers already report that.

### Who uses which rule

- **Enemy state:** `EnemyStateBase` gets `public virtual PropRule PropRule => PropRule.Careful;`.
  `AggroState` and `RearmState` override it to `Forceful`. States still never name each other.
- **`GridNavigator`:** gets a `PropRule Rule` property. `EnemyMovement` sets it from the current
  state before every navigator call.
  - When it changes, the navigator drops its point field, its approach field and its A\* route, so
    nothing planned under one rule is followed under the other.
  - The change is logged with `DebugLog.Info`.
- **Shared chase field:** `PathGrid.FieldToward(target)` becomes `FieldToward(target, rule)`, keyed by
  both. Only Aggro chases today, so in practice one field per target still exists.
- **A\* and flow floods:** `GridPathfinder.FindPath` and `FlowField.Flood` take the rule and read that
  rule's walkability, clearance, `best` and shove cost.
- **Everything else keeps today's meaning, which is Careful:**
  - `FloodFrom` (hearing, speech), `FloorReach` (jobs, guesses, rooms) and `RandomSpotInArea`.
  - Firing spots (`FiringSpots` reads `IsWalkable`). A spot inside a crate is no place to stand,
    even for a hunter.

  The `IsWalkable(cell)` overload without a rule stays as Careful, so existing callers are unchanged.

### The legs follow the same rule

`EnemyMovement.PropsStopHim` becomes per rule:

- **Careful:** any `GridObstacle` body stops their legs (as today).
- **Forceful:** a `GridObstacle` body stops them only when its mass is at or above the grid's Shove
  Limit. That's the "don't even try" rule. A light prop is pushed with `PushLimit`'s normal mass
  ratio (a 10 kg crate against a 50 kg enemy moves at 80% of their speed).

**What ends a shove (the user's choice: a and b together).** Without these rules a shove loops
forever. A pushed crate isn't resting, so it unmarks and the route runs straight through it again. A
jammed crate re-marks as light, so the route keeps going through a crate that won't move.

- **a. A moving light prop keeps its shove cost.** While a light prop moves, it keeps marking its
  core footprint into `_shoveCells`, re-marking as it shifts (the same half-cell rule as today's
  creep). Forceful routes keep weighing pushing against going round, and switch the moment going round
  is cheaper. In a corridor with no way round, they push it ahead. A moving prop never marks the
  blocking counts. Calm enemies don't need it to: they don't push, and Careful routes already avoid
  props.
- **b. A jammed light prop counts as heavy for a while.** A light prop that an enemy pushes into but
  that doesn't move becomes heavy for Jam Time. The test: something presses on it (`PushLimit` reports
  the body) and it moves less than a small distance over about 0.5 s. While jammed it marks the heavy
  footprint (blocking Forceful routes with the skin), and the legs treat it as heavy. Then it goes back
  to light. This also covers two 10 kg crates pressed together: the first doesn't move, so it jams. The
  limit stays per prop. Becoming jammed and becoming light again are logged with `DebugLog.Info`.

`Ruby.Pathing` can't see `PushLimit` (Core), so the report crosses through the game:
`EnemyMovement` hands the body `PushLimit` hit to `GridObstacle.NotePressed()`. That's a public
method on the pathing type, so no reference points the wrong way.

### Debug

- The grid's Gizmo gets a Rule toggle (Careful or Forceful) for which layer it draws. Colours:
  - static: red
  - heavy prop: orange
  - light prop (Forceful only): blue
  - skin: pale orange
  - margin: yellow
- The enemy's route Gizmo is drawn in its rule's colour.

## Inspector work for the user

- Corner Margin back to 0.5 and Wall Cost back to 20 on the scene's PathGrid. With the corridor fix,
  the margin no longer penalises narrow corridors, so leaving it at 0 only brings the grazing back.
- Distraction Time on the officer prefab back to about 1 s (the earlier standing-still bug).
- Check new grid defaults after the build: Shove Limit 20, Prop Skin 0.15, Shove Cost 30.
- A chair, if one is added, needs a `GridObstacle`. None exist by name in the scene today.

## Left to the builder

- How the skin gives way in a narrow gap (one rule is suggested above), as long as no gap open with
  the core footprint closes.
- Whether `HasClearLine` keeps its goal ramp alongside `best`.
- The capsule cast's layer mask and how it tells a light prop from a heavy one (`GridObstacle` exposes
  `IsLight`).
- Array types (`byte` and `int` as today) and whether `best` is stored or computed from clearance at
  each query.
- Updating `docs/architecture/pathing.md` and `movement-and-weight.md`, and adding the project-history
  entry.

## Verification

There are no tests. `python tools/compile_check.py` must pass. The user then plays the scene and
checks:

1. A calm search or patrol through a furnished room leaves every table, chair and crate in place.
2. A hunter cuts through a crate in a doorway when going round is long, and goes round a table every
   time.
3. Enemies follow Ruby down a narrow corridor and don't detour round it.
4. No guess or approach spot lands on the far side of a wall.
