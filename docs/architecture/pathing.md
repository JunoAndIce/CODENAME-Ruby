# Pathing

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Pathing

`Assets/Scripts/Pathing/` replaced Unity's NavMesh. The rule of thumb is **Dijkstra for shared goals,
A\* for individual ones**:

- **`PathGrid`**, one per floor (floors are independent; each grid owns a Height band). It is baked at
  Start: a cell is blocked when an agent-sized capsule standing on it overlaps anything static, which
  gives walls clearance and never counts the floor. `IsOpenSpot` is that test, shared with the
  editor. Dynamic bodies are skipped by the bake; props that should block carry **`GridObstacle`**,
  which adds one to a per-cell counter (`_propBlocks`) under each cell it covers while it rests
  (a counter and not a flag, because props overlap and one leaving mustn't unblock a cell another
  still covers). A `GridObstacle` that rests on no grid (outside every grid's area or Height band)
  warns once, and again only if it gets back on a grid and later leaves one: it used to return
  silently, so a crate placed off the grid never blocked and nothing said why. A prop lying flat still blocks, however thin (`CollectFootprint` tests against the floor, not Floor Clearance): a table flipped onto its 6 cm top used to count as floor. Every change bumps
  `Version`, and fields re-plan.
  **Agent Radius must match the enemy collider's radius**, and doorways need about 1.5 m or more.
  **Corner Margin and the clearance field.** A second field, baked after the cells and redone around
  a prop's cells when it comes or goes, holds each cell's **clearance**: its distance in cells to the
  nearest blocked cell or the grid's edge, capped at `MarginCells + 1`. A cell is *inside the margin*
  when its clearance is at most `MarginCells` (Corner Margin 0.5 m, rounded up to whole cells). Paths
  charge `WallPenalty` there, `(MarginCells - clearance + 1) x Wall Cost` (20, against 10 for a
  straight step), so routes keep half a metre off corners and run down the middle of doorways instead
  of hugging a jamb. It is a cost, not a block: a gap too narrow for the margin is still walked,
  down its middle, and never reported blocked. `MarginCells` is read once at bake, so a Corner
  Margin change takes effect on the next play, not live; editing it while playing logs a warning
  saying so (`PathGrid.OnValidate`), never a silent no-op.
- **`FlowField`** (Dijkstra): one flood from a goal, read by any number of agents. The chase field is
  **shared per floor** (`PathGrid.FieldToward(player)`), so chase cost doesn't grow with enemy count;
  a walk to a fixed point (a chase of what they know, the walk back to their post) floods its own field once. Floods bound the **plain path length** (metres), never the
  penalised cost, so wall costs can't shorten how far a chase reaches or drop a far doorway off the
  field; `TryGetDistance` returns plain metres too, but of the route the field leads, which on a
  penalised field is not necessarily the shortest. `PathGrid.FloodFrom` (hearing, speech and calling
  people in, always read through `FloorReach`) floods without wall costs at all, so its distances
  are the plain shortest: sound doesn't care how close to a wall the route runs. It hands back one
  scratch field per grid, which every flood overwrites; `FlowField.Generation` is bumped by each
  flood so a stale read can be caught (see **Stimuli and hearing**).
- **`GridPathfinder`** (A\*, octile heuristic, integer costs 10/14, no corner cutting): patrol legs,
  cached and re-planned when the grid changes or the enemy is knocked off its route.
- **`GridNavigator`** ties them together and moves any `IPathAgent`: it plans, and the agent steers
  (facing, velocity scheme). There are **no silent fallbacks**: chases and point walks stop
  and log a warning saying why (once per message, so a goal that keeps moving repeats it); area moves report Blocked/Unreachable, and patrol skips that point
  with a warning. **The one rule beside that is a hunt** (`getClose`, Aggro's chase of Ruby or of what they know): with no path, they walk to the ground they can reach nearest her, within 5 m, and warns once. A body can stand where the grid has no way in, tight behind a crate, and an enemy who stood still lost her when their timer ran out though she hadn't moved. Grid line of sight ignores the cell the agent already stands in, which near a wall
  is clearance-blocked.
- **Lines keep the margin too.** `HasLineOfSight(from, to, minClearance)` needs every cell after the
  first to have that clearance. The navigator's shortcut and beeline (`HasClearLine`) ask for
  `MarginCells + 1`, the clearance `WallPenalty` doesn't charge for, so a pulled line can't graze a
  corner the flood routed round. A goal beside a wall can still be walked straight to, but the
  relaxing is a **ramp** near the goal only: each cell needs the lesser of `MarginCells + 1` and the
  goal cell's clearance plus its Chebyshev distance from the goal cell (never below 1), so the rest
  of the line keeps its margin; a line that fails goes to the field or A\*, which keep it.
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
