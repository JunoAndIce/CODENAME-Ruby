# Pathing

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Pathing

`Assets/Scripts/Pathing/` replaced Unity's NavMesh. The rule of thumb is **Dijkstra for shared goals,
A\* for individual ones**:

- **`PathGrid`**, one per floor (floors are independent; each grid owns a Height band). It is baked at
  Start: a cell is blocked when an agent-sized capsule standing on it overlaps anything static, which
  gives walls clearance and never counts the floor. `IsOpenSpot` is that test, shared with the
  editor. Dynamic bodies are skipped by the bake; props that should block carry **`GridObstacle`**,
  which marks per-cell counters while it rests (counters and not flags, because props overlap and one
  leaving mustn't unblock a cell another still covers; see **Prop rules** below for what it marks). A
  `GridObstacle` that rests on no grid (outside every grid's area or Height band)
  warns once, and again only if it gets back on a grid and later leaves one: it used to return
  silently, so a crate placed off the grid never blocked and nothing said why. A prop lying flat still blocks, however thin (`CollectFootprint` tests against the floor, not Floor Clearance): a table flipped onto its 6 cm top used to count as floor. Every change bumps
  the changed rule's version (`VersionFor(rule)`), and that rule's fields and plans re-plan. Per rule,
  because a light prop on the move re-marks Forceful's shove cost every half cell, and one shared
  counter made every calm walker on the floor re-plan while a crate slid.
  **Agent Radius must match the enemy collider's radius**, and doorways need about 1.5 m or more.
  **Tight cells.** A body fits through any gap over twice Agent Radius (0.7 m), but the centre test
  only opens a gap that holds a cell centre at least Agent Radius from both sides: anything from 0.7 m
  to 0.95 m at 0.25 m cells, by where the gap falls against the lattice. A corridor Ruby walked down,
  hugging the wall past a row of crates, read as sealed, and hunters gave up the fight each time.
  Halving the cells fixed it and lagged the game badly. Instead each cell is tested at
  `PropLayer.SampleSide` squared points (5 x 5, 5 cm apart at 0.25 m cells, a lattice that runs on
  evenly into the next cell), and a cell whose centre is blocked but which has a point where a body
  touches nothing is **tight**: walkable, clearance 1, and it keeps its **fit point** (the open point
  with the most open neighbours, nearest the centre on a tie, so a body stands in a lane's middle).
  Any straight gap from about 0.75 m now opens. Routes steer at `StandPoint(cell, rule)` (a tight
  cell's fit point, otherwise its centre) wherever they turn a cell into a point: the field's steps,
  A\*'s corners, area spots. Clearance still counts from every blocked centre, tight ones included,
  so the margin is no closer to walls where there's room; a tight cell is only taken where it is the
  way through (its `Best` is low) or worth its wall penalty. Static points are measured with one box
  and one capsule query for most cells (nothing near, or a wall within Agent Radius minus the points'
  reach), and point by point only between, at bake for cells whose centre a wall blocks and lazily
  when a prop first covers an open cell's centre. Props mark the points they cover
  (`CollectSamples`, the same probe height and flat distance as their core, so the centre point
  agrees with it) into per-point counts per rule (`PropLayer.Samples`), and each mark re-fits the
  cells it touched. The gizmo draws a tight cell as a small teal square at its fit point. A gap under
  0.7 m stays shut: Ruby doesn't fit it either.
  **Corner Margin and the clearance field.** A second field, baked after the cells and redone around
  a prop's cells when it comes or goes, holds each cell's **clearance**: its distance in cells to the
  nearest blocked cell or the grid's edge, capped at `MarginCells + 1`. A cell is *inside the margin*
  when its clearance is at most `MarginCells` (Corner Margin 0.5 m, rounded up to whole cells). A third,
  **`Best`**, holds the highest clearance within `MarginCells` of each cell (capped the same): the room
  a route there could have had. Paths charge `WallPenalty`, `(Best - clearance) x Wall Cost` (20,
  against 10 for a straight step). In an open room `Best` is the cap, so routes keep half a metre off
  corners and run down the middle of doorways instead of hugging a jamb. In a corridor the middle line
  has the best clearance there is and pays nothing, so **a corridor costs its length**. The old charge,
  `(MarginCells - clearance + 1) x Wall Cost`, made a one-cell corridor cost 3x its length at 0.5 m
  cells and 5x at 0.25 m (a two-cell margin), which is why smaller cells made enemies avoid corridors
  more. It is a cost, not a block: a gap too narrow for the margin is still walked, down its middle,
  and never reported blocked. `Best` is baked as a separable max and refreshed over the clearance box
  grown by `MarginCells`. `MarginCells` is read once at bake, so a Corner
  Margin change takes effect on the next play, not live; editing it while playing logs a warning
  saying so (`PathGrid.OnValidate`), never a silent no-op.
- **Prop rules** (`PropRule`, `PropLayer`; spec `docs/specs/2026-11-18-prop-aware-pathing.md`). Calm
  walks must leave a room's props where they are, and hunters may shove light ones aside, so every
  walkability, cost and line query names a rule, and the grid keeps a `PropLayer` per rule (its own
  counts, clearance and `Best`). The overloads that name none mean **Careful**.
  - **Careful** (every state but Aggro and Rearm): every resting prop's core blocks, and its **skin**
    costs Prop Skin Cost (60).
  - **Forceful** (Aggro and Rearm, `EnemyStateBase.PropRule`): a heavy prop (Rigidbody mass at or over
    the grid's **Shove Limit**, 20 kg) is the same; a light one's core costs **Shove Cost** (30) and
    blocks nothing, so a hunter goes through a crate only when going round costs more.
  - A prop marks its **core** (the agent radius round each collider, as walls get), a **skin** ring
    Prop Skin (0.15 m) further out, and its **solid** cells (centre inside a collider), into a
    `PropMarks` it hands back unchanged to unmark. **The skin is a cost, not a wall**: stepping on it
    costs extra, and lines read it as clearance 1, so a route with any other way keeps its gap, while a
    lane only skin leaves open stays open (the user's "keep the lane open"). Adding skin only where the
    core clearance was 2 or more was tried on paper and can't work: a skin cell always neighbours core.
  - **A shove always ends.** A light prop on the move keeps marking its core as Forceful shove cost,
    re-marking each half cell, so a hunter keeps weighing pushing on against going round (a pushed
    prop used to unmark, and the route ran through it forever). One pushed into a wall or another crate
    **jams**: `EnemyMovement.PropStopsThem` reports each press (`GridObstacle.NotePressed`), and pressed
    for Jam Window (0.5 s) while moving under Jam Distance (0.1 m) it counts as heavy for Jam Time (3 s).
    A heavy prop on the move marks nothing.
  - The **limit is per floor**, because every enemy on a floor shares the chase field.
  - **Gizmo Rule** picks which layer the cells draw: red static, orange a blocking prop, pale orange
    skin, blue shove (Forceful), yellow margin. An enemy's play-mode route line is cyan under Careful,
    orange under Forceful. The cells draw as one texture on one quad, a texel per cell, with the tight
    cells' markers as one mesh (`GridOverlay`, Editor-only), refilled only when the layer's version or
    the draw settings change, at most five times a second. A cube per cell issued tens of thousands of
    draws on every repaint, four times as many at half the cell size, and lagged the Editor whenever
    the grid was selected. Builds never draw the grid.
- **`FlowField`** (Dijkstra): one flood from a goal, read by any number of agents. The chase field is
  **shared per floor and rule** (`PathGrid.FieldToward(player, rule)`), so chase cost doesn't grow with enemy count;
  a walk to a fixed point (a chase of what they know, the walk back to their post) floods its own field once. Floods bound the **plain path length** (metres), never the
  penalised cost, so wall costs can't shorten how far a chase reaches or drop a far doorway off the
  field; `TryGetDistance` returns plain metres too, but of the route the field leads, which on a
  penalised field is not necessarily the shortest. `PathGrid.FloodFrom` (hearing, speech and calling
  people in, always read through `FloorReach`) floods without wall costs at all, so its distances
  are the plain shortest: sound doesn't care how close to a wall the route runs. It hands back one
  scratch field per grid, which every flood overwrites; `FlowField.Generation` is bumped by each
  flood so a stale read can be caught (see **Stimuli and hearing**).
  **Floods are lazy.** `Flood` plants the goal; each read (a step, a distance) spreads the flood until
  the cell it asks about is settled, and keeps the frontier for the next read. Dijkstra settles cells
  in cost order, so a settled cost is final and every cheaper cell is settled too, which is all a
  descent reads: answers are exactly a full flood's. `CollectReached` and `TryGetNearestReached` look
  at every cell, so they finish the flood first. A full flood to Max Flow Distance cost 21–28 ms at
  0.25 m cells (85 ms at 0.125 m), and the chase field re-floods several times a second as Ruby
  crosses cells; read lazily it costs what its farthest reader needs: 1.2 ms at 10 m by path,
  4.9 ms at 20 m, the full flood only when a hunter is across the map. A reader nothing can reach
  still runs the flood out, once per flood. **Exact while the grid is unchanged since the flood:** a
  prop that marks between the flood and a read is read by the cells settled after it (a full flood
  read it at its next refresh, as `RefreshIfStale` still does, on the same 0.2 s throttle), and the
  walled-off early exit (see **Connected areas**) waits for that re-flood, so a crate landing on the
  goal can't turn away a reader the flood still reaches.
- **`GridPathfinder`** (A\*, octile heuristic, integer costs 10/14, no corner cutting): patrol legs,
  cached and re-planned when the grid changes or the enemy is knocked off its route.
- **Step caches** (`PropLayer.Moves`, `PropLayer.StepCosts`). Each layer keeps, per cell, a bit per
  `PathGrid.Steps` move that `CanStep` allows and the `StepCost` of stepping onto it. Floods, their
  descent and A\* read those two arrays instead of deciding three cells' walkability and the costs for
  each of eight neighbours, which made a whole flood 5x faster (28 ms to 5.7 ms) and a failed A\* 3x.
  They are filled at bake and redone by `RefreshAround` over every prop's marks: the clearance box
  grown by `MarginCells` (how far a clearance change reaches into `Best`, and so into step costs) and
  one cell more (a cell's moves read its neighbours). The skin is in that box, as it is a step cost.
  **Anything new that changes walkability or a step cost must refresh them there**, or floods and A\*
  read stale moves while `CanStep` answers the new ones. `StepCost` itself returns the cached value.
- **Connected areas** (`PropLayer.Area`, `PathGrid.SameArea`). Each layer labels its walkable cells'
  connected areas. A\* and a flood's reads ask `SameArea` first and fail at once between two areas,
  where they used to explore everything the start could reach: an A\* from inside the building to the
  open ground outside it searched to its 20,000-cell cap (9.6 ms), a flood read there ran 86,000 cells;
  both now take about 0.0001 ms. Labels are rebuilt (0.9 ms) at the first query after a resting prop
  marks or unmarks (`WalkVersion`), so a burst of props settling costs one rebuild; a fight re-marked
  walkability about 7 times a second. They must never miss a join (a prop leaving can join two areas,
  and stale labels would call a reachable cell unreachable), which is why they are rebuilt rather than
  kept up. Same area doesn't mean in range: A\* still gives up past 20,000 cells (a goal 120 m away by
  path does) and a flood past its bound.
- **`GridNavigator`** ties them together and moves any `IPathAgent`: it plans, and the agent steers
  (facing, velocity scheme). It reads `IPathAgent.PropRule` at the top of every move and decision, and a
  change drops its point field, approach field and route, so a hunter who turns searcher never takes
  another step along a route through a crate. There are **no silent fallbacks**: chases and point walks stop
  and log a warning saying why (once per message, so a goal that keeps moving repeats it); area moves report Blocked/Unreachable, and patrol skips that point
  with a warning. **The one rule beside that is a hunt** (`getClose`, Aggro's chase of Ruby or of what they know): with no path, they walk to the ground they can reach nearest her, within 5 m, and warns once. A body can stand where the grid has no way in, tight behind a crate, and an enemy who stood still lost her when their timer ran out though she hadn't moved. Grid line of sight ignores the cell the agent already stands in, which near a wall
  is clearance-blocked.
- **Lines keep the margin too.** `HasLineOfSight(from, to, minClearance)` needs every cell after the
  first to have that clearance. The navigator's shortcut and beeline (`HasClearLine`) ask each cell for
  its `Best`, the clearance `WallPenalty` doesn't charge for, so a pulled line can't graze a corner the
  flood routed round, and a line down a corridor's middle is taken whole. A goal beside a wall can still
  be walked straight to, but the relaxing is a **ramp** near the goal only: each cell needs the lesser of
  its `Best` and the goal cell's clearance plus its Chebyshev distance from the goal cell (never below
  1), so the rest of the line keeps its margin; a line that fails goes to the field or A\*, which keep
  it. Lines read the rule's effective clearance, where skin reads 1, and so does a light prop under
  Forceful: otherwise a shortcut aimed straight through a crate the field had routed round, and the
  hunter pushed it ahead of them the whole way.
  String pulling (`GridPathfinder`) and the flow field's look-ahead use the lowest clearance
  along the cells they skip, capped the same way: where the path had room the pulled line keeps the
  margin, and through a narrow gap it keeps only what the gap has, which is its middle.
  `FindPath` returns each corner's accepted clearance beside the corners, and the navigator checks
  sight to a route corner (the off-route check and the advance to the next corner) at that
  clearance, not the corner's own: a run through a doorway to a roomy corner was pulled at the
  doorway's, and the corner's own made it look off-route, so they re-planned every interval and
  turned only after reaching the corner.
  `TryFindSpotAway`, Aggro's backing off, keeps the plain test: a short step, and a margin would
  leave a cornered enemy nowhere to go.
- **Shortcuts are checked against real shapes** (`PathGrid.HasClearShape`). Cells only approximate
  shapes, and a line the grid accepted clipped real corners and brushed tables, nudging them a little
  each pass. After the grid's check, every straight shortcut (Follow's line, Plan's direct line, the
  corner advance, a field step's look-ahead point, A\*'s pull) sweeps one capsule across the agents'
  band at Agent Radius + Prop Skin. A prop that blocks under the rule refuses it there; a static hit
  refuses it only if a second cast at plain Agent Radius hits too, since walls get no skin. Characters,
  moving bodies, light props under Forceful and whatever the capsule starts inside are ignored. Only
  the point about to be steered at is cast; a field step that fails steps back to its first, always
  taken, step, and Follow caches its answer for Replan Interval.
- **The snap can't cross walls** (`NearestWalkable`). Anything hugging a wall stands in a
  clearance-blocked cell, so starts and goals snap to the nearest walkable cell within a couple of
  rings. It used to take that cell whatever lay between, and a guess once came back 107 m away by path,
  snapped through a wall. It is now a breadth-first search that never enters a **solid** cell (its
  centre inside static geometry, baked with a 0.01 m probe, or inside a resting prop), so Ruby pressed
  against a table snaps to her own side.
- **Personal space** (`EnemyMovement.MakeRoom`, Personal Space 1.5 m on the enemy). Walking any path, they curve away from any enemy on their
  feet inside the radius, harder the closer they are, and one dead ahead adds a step to their own right, so two met head-on pass rather than
  stop. It bends the walk and never the plan: routes don't cost extra near others, because they move. A bend into a wall isn't taken, so a
  corridor too narrow to pass in still falls to the push rule. Roaming and wandering spots avoid others' radius when a few tries can
  (`TryPickFreeSpot`). Someone standing still doesn't step aside.
- **`Steering`** is the turning maths: `TurnToward` rotates a flat heading at most so many degrees
  (a zero direction returns the heading, a rule so a body standing on its point keeps its facing), and
  `SpeedToFit` caps speed so a full-rate turn still reaches a point instead of orbiting it (never
  below a tenth of the speed, which keeps the floor's turning circle inside `CornerReach`, and a
  point right behind from freezing them). `EnemyMovement.MoveToward` turns its walking heading at
  Turn Speed (720 degrees a second, separate from `CharacterAnimator`'s 360 degree leg turn), so
  bends are arcs. **Setting off, they turn on the spot**: a walk that didn't run the step before, or
  that `Stop` or `StepAway` ended, starts along its way, not from their facing. Holding or backing away
  they face Ruby, and an arc from there walked an enemy at the gun's Min Distance into her on their way
  to a firing spot behind them: backed away, they dropped the spot and picked another, again and again
  with her standing still. The sweeping walk keeps its own way. **It slows to fit only for points they must reach** (a goal on a clear line, a route
  corner, a field's last step: `IPathAgent.MoveToward`'s `mustReach`, which `GridNavigator.Steer`
  takes explicitly, and `FlowField.TryGetStep` reports as `atGoal`). A field step rolls ahead and
  can't be orbited, except its last one, the goal cell, which they must reach: the descent ends
  there, so the point stops moving and a fast chaser of Ruby against a wall could circle it.
  Aggro's facing (`FaceTarget`, `StepAway`) stays instant: they aim at Ruby, they aren't walking a bend.
