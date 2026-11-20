# Enemy AI

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do. Searching, group jobs and distraction are in `enemy-searching-and-jobs.md`; bullets here that say "see **Group jobs**", "**Searching**" or "**Distraction**" point there.

#### Enemy AI

- **Who owns what.** `EnemyController` is the one MonoBehaviour per enemy: it holds every serialized
  tuning field, the state machine, and the plain classes that do the deciding, each built in
  `Awake`: `EnemyMemory` (what they know), and, handed the controller to read, `EnemySenses` (hearing
  and scans), `EnemyVision` (the view and the line to a point) and `EnemyReactions` (what they do
  about what they sense), and the parts that carry the rest of the controller's old work:
  `EnemyMovement` (their walking, turning and facing, and the walks the states ask for),
  `EnemyAwareness` (what they take in each frame: sight of Ruby, what they learn and say, and the
  newest thing they know), `EnemyBodyState` (their record of the chain, and the gun and corpse handling
  that goes with it), `EnemyBeat` (the beat they roll through between states, see **Beats**) and `EnemyGizmos` (what they draw in the Scene view, a static class the
  controller's two gizmo messages forward to). `Intel` is the scene's one hub and owns
  `KnowledgePool` (the shared picture), `JobBoard` (group jobs, with the `FloorReach` calling uses)
  and the `FloorReach`es that speech and sound use. The plain classes read the controller's tuning
  on every call, so retuning in the Inspector during Play still works, and **no serialized field
  moves into them**: the scene holds route overrides and per-enemy tuning on `EnemyController` and
  `Intel`, and `FormerlySerializedAs` can't carry a value to another component. A new decision goes
  in the class that owns its subject, not back in the controller.
- **Order.** `Intel` runs at execution order -20, before the enemies (0): it advances every job's
  clock and publishes the pool first, so every member reads a job's new step on the same frame.
  Then each enemy's `Update` runs the machine (transitions, then the state's `Tick`) and
  `EnemyAwareness.Update`; their `FixedUpdate` runs the state's `FixedTick`, then, alive and not overridden, `EnemyBeat.FixedTick` (after the
  state, which waits while a beat runs, so the beat has the last word on facing and feet) and `EnemySenses.Scan`.
  Sounds arrive from the bus whenever a weapon or impact emits them, in any phase.
- **Sight** (`EnemyVision.CanSeePlayer`): a ray from eye height to
  the player. **Anything solid blocks it except characters** (any body under the player or an enemy, so Ruby's own ragdoll bones
  never hide her). Unlike paths, sight is
  checked fresh every frame, so a crate or table can hide the player, while a prop lower than the eye
  line can't. Cached once per frame.
- **Detection**: idle, patrolling or searching, with Ruby in their view **and** in sight → Aggro
  (`EnemyVision.InView`, then `CanSeePlayer`). Their view is a cone (View Angle, 135° on the
  officer prefab) out to View Range (12.5 m) in front of them, plus a Close Radius circle (3 m)
  all round so she can't stand right behind them; with no horizontal facing there's no cone, only
  the circle. **The close circle is for Ruby alone** (`EnemyVision.InView`): everything else they
  notice by sight uses the cone only (`InCone`, see **Senses**). An officer's root never pitches in play (its rotation is frozen and only ever turned
  about Y), so this only covers one placed pitched in the scene. Idle officers aggro on sight like
  the rest; an unarmed one also goes for a free gun in reach. Aggro keeps contact all round, at any
  distance, while they see her. Damage and getting up from a ragdoll (thrown or dropped) enter Aggro from
  any AI state. Short of seeing her, only two things send them to Aggro from a sense: seeing the
  chain drag or throw an enemy (see **Senses**), and a searcher provoked to their limit
  (`EnemyReactions.GoAfterRed`, see **Distraction**), who goes blind to her until she is in their view.
  A grabbed enemy standing still, a knockdown, a death, a job mate lost: they search, and go to Aggro
  only if Ruby then comes into view.
- **Aggro** hunts Ruby. Built to `docs/specs/2026-11-18-enemy-path-tracking.md`. **In sight**, unarmed,
  they chase her along the shared chase field; armed (a `GunHolder` with a `Gun`), they walk to a firing spot of
  their own (see **Fights**), and with no spot keep the gun's band: they close in to Min Distance, hold in the
  metre above it, and back away facing her inside it (`GridNavigator.TryFindSpotAway`, `EnemyMovement.StepAway`),
  standing their ground when cornered. They fire alongside all of that, whenever she's in sight, in range and on
  her feet (`AggroState.Firing`); firing is not a state. They only aim within range: beyond it they run with the
  gun down. **Contact is seeing her**, a clear line at any distance (`EnemyVision.InContactWithPlayer`); there is no
  give-up radius. It used to make two rules of one idea: sight past it still taught them where she was but counted
  as no contact.
  **Out of sight they know her path, never her position.** Each Seen fact carries her heading
  (`RubyFact.Velocity`, averaged over her last half second in view, `EnemyAwareness.SampleVelocity`), and nothing is
  learned while she is out of sight. Sight Follow Through, which handed them her live position for a while, is
  gone: it was psychic, and Aggro Time ended the hunt under it anyway.
  - **The glimpse grace** (`EnemyAwareness.GlimpseGrace`, 0.25 s, a constant; user's choice). Sight is one ray a
    frame and flickers at a crate's edge, so she counts as gone only after a quarter second without it
    (`InGlimpseGrace`). Meanwhile an armed enemy keeps their firing spot and its claim and faces
    `LastSeenPosition`; with no spot they hold facing it, or keep walking to it when they were closing in. They
    keep the gun up on that point (`AggroState.Aiming` and `AimPoint`, which the stance and the arm read) but
    don't fire, and don't back away; only a failed walk picks a new spot, since a scheduled pick filters by the
    chase field, which floods from her live cell. Sight back within the grace is the same sighting: it calls
    nothing out (a flicker used to call her out past Speak Interval, handing every hunter a new fact and flood),
    and her heading samples carry across it. Without it each flicker dropped the spot, flooded a guess, turned them to it
    and re-picked a spot when she showed.
  - **The vanish call-out.** When the grace runs out in a fight they say the last fact they saw, heading and all,
    once (`EnemyAwareness.CallOutVanish`, through `Intel.Say`). Call-outs otherwise come only while seeing her, so
    mates would hunt a fact up to a Speak Interval older, and a first-sight call-out has no heading.
  - **The guess** (`EnemyAwareness.TryGetGuess`): one flood from the newest fact's position out to Guess Distance
    (8 m of path, on the enemy), through a `FloorReach`, and the reached cell furthest along her heading less half its
    sideways stray. So it lands through the doorway she ran at, or behind the side of the crate she was making for.
    Cached against the fact: collecting a flood walks the whole grid, so it runs once per new fact, never per frame.
    No heading (under 0.5 m/s, or a sound), no grid, or nothing reachable ahead guesses the fact's own spot, each
    logged. Guess Distance is a field and not her speed times the timer: she runs 15 m/s, which put the guess 60 m
    off. Selected in Play, an enemy draws the vanish point, her heading and the guess.
  - **Covering the ways out** (`HuntPlan`, `HuntAssigner`, `FightBoard.PlanHunt`; spec
    `docs/specs/2026-11-20-covering-the-ways-out.md`). Everyone told the same thing used to walk to the same guess,
    so a floor's worth of officers piled onto one spot and circled it, and when she turned the guess was somewhere
    she never went. Now the fight splits them up:
    - **The plan.** From the newest fact any member hunting out of sight holds (`AggroState.HuntingOutOfSight`;
      those in sight learn every frame, so only hunters' facts are read), `HuntPlan.TryBuild` floods once from the
      vanish point V out to Guess Distance. Its **rim** is reached ground within 1.5 m of the flood's edge with
      walkable ground beyond it the flood didn't reach: where a path carries on out of the area. A room's far corner
      as far along a path leads nowhere, so isn't rim. The rim is joined cell to cell into openings (a doorway, a
      corridor end, an open room's arc). Route 0 is the guess itself (the same scoring,
      `HuntPlan.TryPickAlongHeading`), then **every opening gets a route at its middle** before any gets a second,
      ranked along her heading (with none, widest first), then wide openings are cut into directions 3 m apart,
      up to six routes. Ranked point by point, the open ground ahead took all six and a room's other door got none.
      No rim at all is a **dead end**: it logs, and everyone spreads through the reached ground. A re-plan keeps the
      last plan's ways out that still have rim within a route's width first, so a call-out that creeps V doesn't
      swap which openings are covered.
    - **Who goes where** (`HuntAssigner.AssignAll`, on each new plan). One flood per route measures every hunter's
      walk to it, all read before anything is assigned. Holders whose old spot lies within a route's width of a new
      route keep it, closest pairs first. Then each route in rank order takes the nearest hunter left, then a second
      each, standing Spot Spacing to one side of the first and clear of every slot and other route's spot; with no
      such spot the route takes no second (two is a most, not a must), and a second keeps the side they stood on.
      A route still empty once no free hunter can walk to it within the 30 m bound takes the nearest by straight line,
      with a log line, before anyone spreads: left empty, hunters coming from one side all spread on that side and
      nobody covered the far exit. The rest spread over the plan's reached ground (thinned to about 1 m), each on the
      spot nearest them Spot Spacing from every slot. A hunter beyond every route's bound takes what's left last, with
      a log line. One late to the plan (lost sight after it, or just told its fact) gets the best of what's left from
      one flood of their own (`AssignOne`). Slots live on the `Fight` and go when their holder stops hunting or leaves.
    - **News that hasn't reached everyone.** A holder who hasn't heard a re-plan's fact (beyond Speech Range, waiting
      for the pool) keeps a nearby route of the new plan through the steady pass, or else the slot they hold, held
      over (`Fight.HoldOver`) until they hear it; releasing them sent them back to the one shared guess at every
      call-out. One who has just lost her (`EnemyAwareness.VanishPending`) isn't planned from until their vanish
      call-out, later the same frame (Intel runs first): planned at once, the fact was theirs alone and every mate's
      slot was released. A fact no plan could be made from is remembered on the fight (`NotePlanFailed`) and not
      retried until news changes, or it flooded and logged every frame.
    - **Hunting** (`AggroState.HuntGuess`): past the grace, a hunter with a slot walks to it at Chase Speed and,
      within Search Reach, looks round it (`EnemyMovement.LookAround`, looped, so a hunt a mate keeps going doesn't
      leave them frozen) centred **outward**, from V to their spot, down the route rather than back at the middle.
      Without a slot (they haven't heard the plan's fact, or fight alone on no grid) they hunt their own guess as
      before, logged once per fact. A slot or guess that moves more than half a metre sends them on. One whose walk
      reports Blocked (Stuck Time) looks round where they stand instead of pushing at a mate's back, and one with
      no way there does the same; both log. Officers 70 m or more of path away can fail to plan a walk at all
      (`GridPathfinder`'s 20,000-cell limit) and look round where they stand: that predates this, and in a real
      chase they are already near.
    - Selected, an enemy draws their slot and a ray down the route; with `Intel` selected each fight draws its plan
      (a cross at V, the ranked routes, a line from each hunter to their spot).
  - **One timer, Lose Sight Time** (4 s, on the enemy), replaces Aggro Time, Sight Follow Through and Unseen Hold
    Time. It counts from the last time anyone in the fight had her in contact, the grace included, and when it
    runs out `LostPlayer` sends them to search. Any glimpse, theirs or a mate's, starts it over. Knowing nothing at
    all ends the hunt at once, with a log line. There is no hold at a spot any more, so Known Spot Radius and the
    "stalled behind someone" rule went with it. One who never saw her in this fight (up from a throw, told by word
    of mouth) hunts what they were told and loses her after Lose Sight Time like anyone: Unseen Hold Time used to cut
    that short so a team that came to check didn't leave without them, and that special case went with the field.

  The search starts from where the hunt was leading, or where they stand when they knew nothing:
  `AggroState.Exit` requests it (see **Searching**), and only when `LostPlayer` is what's sending
  them there, since Exit also runs on the way to Rearm, Grabbed, Ragdoll and Dead. A spot with no way in starts
  the search from the ground they can reach nearest it (`TryGetNearestReachable`), because a Search member who
  can't reach their spot is taken off the job.
  In sight, the chase is a **hunt** (`GridNavigator`'s `getClose`): tight behind a crate, Ruby's cell can have no
  way in on the grid, and they walk to the ground they can reach nearest her, within 5 m, and warn once; no ground
  in reach still stops and says so.
  **A provoked enemy sees her only with their own eyes** (`EnemyController.ProvokedUnseen`; spec, "At their
  own limit they run at Ruby, and see her only with their own eyes"). `EnemyReactions.GoAfterRed` sets it
  before `Alert`, and only it: word of mouth, getting up and seeing the chain drag someone keep the
  all-round sight above. **A hit restores it** (user ruling): damage that doesn't floor them clears the flag in
  `EnemyController.HandleDamaged`, with a log line, and they fight as any damaged enemy does. While it is set the all-round sight doesn't apply: every sight read in Aggro is
  `EnemyVision.CanTrackPlayer` (and contact `InContactWithPlayer`), false for them, so they hunt the guess of what
  they were told (the Heard fact, or anything newer they learn), get no glimpse grace, hold no firing spot and don't
  fire, and `EnemyAwareness.SeesPlayer` counts only `DetectsPlayer`, so they learn no Seen fact and call nothing out.
  They don't give up on the way: their Lose Sight Time waits while the walk to the guess is under way
  (`AggroState.OnProvokedRun`), and once there it counts as for anyone.
  With no way there at all they count down as anyone does, and after Search Travel Limit (20 s) of running
  Lose Sight Time counts again, with a log line, so someone standing in their way can't keep them in Aggro for
  good. A fight mate's contact keeps them in as it does anyone. It clears
  the first time `DetectsPlayer` holds (`EnemyVision.StillProvokedUnseen`, which whoever asks first runs,
  so every reader agrees within a frame), and then every normal rule follows, the call-out that brings
  others included. It also clears when they leave the hunt by any route (a lost fight, a grab, a knockdown,
  death), caught in one place: the machine's `StateMachine.Changed` hook, which `EnemyController`
  handles. **Rearm keeps it:** an unarmed provoked enemy going for a gun hasn't seen her, and hands
  straight back to Aggro.
- **Beats** (`EnemyBeat`, a plain class the controller owns; spec `docs/specs/2026-11-20-smooth-transitions.md`).
  Officers roll into four moments instead of snapping between states: the **Draw** on first sight of Ruby,
  a **Notice** before checking something, the **Lose** hold on where they last knew her before searching,
  and the **Stand Down** at a search's end. One runs at a time: a kind, a length tuned on the controller
  (Draw Time 0.5 s, Notice Time 0.6 s, Lose Time 0.5 s, Stand Down Time 1 s; a distraction's Notice runs
  Distraction Time), a point to turn toward at Turn Speed, and the feet. Every beat but the draw plants
  them; the draw lets the momentum they had run down inside it (`EnemyMovement.Coast`), so a walking officer
  doesn't freeze mid-stride. The state that starts a beat waits while it runs, and `EnemyBeat.FixedTick`
  runs after the state's in `FixedUpdate`, so it has the last word on facing and feet.
  - **Cancelled on every change of state** (`OnStateChanged`, before the new state's Enter), so a beat never
    outlives the state that started it, and the interrupts the user chose come from the table with no
    rule of the beat's own: a hit, grab, knockdown or death cuts any beat, and Ruby seen during a Notice,
    Lose or Stand Down cuts straight to Aggro. Nothing else does: a disturbance during a Lose or Stand
    Down is counted toward their limit but doesn't turn them (a log line says so). The controller keeps
    the state they came from (`PreviousState`) and the beat that change cut short (`BeatCutShort`).
  - **The draw is Ruby's window to act first** (the user's pick): for Draw Time Aggro doesn't fire or move
    them, while sight, Lose Sight Time and knowledge count on (`AggroState.Drawing`). Only from calm
    (`CameFromCalm`: Idle, Patrol, Searching; word of mouth and a hit from calm included), never back from
    Rearm or a fall or on a glimpse mid-fight, and not when she steps back in during the Lose beat, while
    the gun is still on her spot. The Aggro-to-Rearm row waits for it, so an unarmed officer still
    flinches and turns before going for a gun. Draw Time 0 is no window: the beat ends on its first step.
  - **Lose** holds the gun on the newest place they know of her, the search's own centre (the controller's
    `HoldingLastSpot` makes the stance Aim and the target that spot). One who knew nothing of her, handed
    through Aggro by Rearm without a sighting, skips it.
- **Fights** (`FightBoard` and `Fight`, in `Enemies/`). Everyone in Aggro belongs to one fight per floor,
  all equal. `FightBoard` is a plain class `Intel` owns and ticks beside the job board, forwarding `Join`
  and `AnyInContact`; it never moves anyone. `AggroState.Enter` calls `Join`: they take the fight
  on their floor (`PathGrid.At`) that has the member nearest them, however far, or starts one. Rearm hands
  back through Aggro, so a re-join is a no-op. **Membership is polled, not hooked:** `Advance` drops a
  member who is destroyed, dead or in neither Aggro nor Rearm (so grabbed, downed and searching all drop
  them at once, and getting up rejoins them by the normal route). A fight nobody is in closes.
  `EnemyController.Fight` is set only by the board. On no grid they fight alone, with one warning.
  **Contact is shared:** `AggroState.LostPlayer` is false, and its timer starts over, while a
  fight mate has Ruby in contact, so a member behind a wall doesn't give up while another has her. Out of sight meanwhile they cover the ways out of
  where the newest fact puts her (see **Covering the ways out**), which the contact holder's call-outs keep moving; never her true position.
  A provoked member who hasn't laid eyes on her gives their mates no contact (see **Aggro**).
  **No squads:** a main squad of up to four that closed in, and a standby squad that held back behind it
  and moved up when a main member fell, were built and taken out (user, 2026-11-07). Firing spots spread
  everyone round her (standby, then, on a slightly wider ring), so the squads never showed on screen, and a
  standby member moving up had nowhere new to walk.
  **Firing spots** (`FiringSpots`, a static picker, with claims kept in the `Fight`). An armed member
  with Ruby in sight, or in the glimpse grace, walks to a spot of their own and holds there facing her, firing
  as before, so a fight spreads into a loose crossfire instead of closing along one line. The pick is
  round where they know she is (her true position in sight, where they last saw her in the grace; past that they hunt the guess
  and hold no spot): 16 angles on 3 rings across the gun's band (its Min Distance out to
  Range less the hold band). A candidate must be walkable and on the floor's shared chase field
  (`FieldToward`, read with `TryGetDistance`: "ground they can reach", out to the field's own bound; it
  refreshes only when read as a route, so the pick asks it to with `FlowField.Refresh`), with them on that
  field too, not within Spot Spacing (2 m, on `EnemyController`) of another member's claim, and not
  reached by a straight line that cuts the gun's Min Distance circle round her. Of those the score
  favours a different angle from their fight mates' (the heaviest weight, which is what spreads them round
  her), near where they stand, the middle of the band, and not on a mate's line to her; the best few (a
  budget of 4) then get an eye-height ray, anything solid but characters blocking it, as sight does. Their
  current spot is kept unless a new one beats it by a margin. They re-picks about once a second
  (staggered), and at once when their spot falls inside the gun's Min Distance or more than half a spacing past the band's far side, a walk to
  it is refused, or they are backed away from her on the way there (either spot is left
  alone for 3 s); never sooner than 0.25 s after the last pick. A blocked shot no longer re-picks: losing the shot is the glimpse grace, which holds the spot, and past it they hold none. While Ruby runs, her spot leaves their band often, so
  re-picks then come about every 0.25 s rather than every second: bounded, and each costs an A\* re-plan. Backing away inside Min Distance is as
  before, and cornered they stand and fights. With no spot they keep the band behaviour, with a log line
  once per fight. Claims go when they leave Aggro (`AggroState.Exit`), loses their gun or knows nothing of
  her, and the board's prune is the guarantee that a claim never outlives its member. Selected, they draw
  their spot and a line to it. With `Intel` selected in Play, each fight rings its members in red with a
  line joining them. **There are no fire lanes:** nothing stops them firing through a fight mate, though
  bullets don't hit enemies on their feet and spot scoring keeps them off each other's lines to her.
  Fire lanes (holding fire and stepping aside for a mate in the way) were built and taken out too: they
  drove spots into Ruby's Min Distance and enemies shuffled instead of fighting.
- **Rearm** (`RearmState`): an unarmed enemy goes for the nearest free gun within Rearm Radius.
  Free means loose, resting, not on the chain and not claimed by another enemy. It picks the gun
  up within 0.3 m of its body's surface, measured to the nearest point of the gun's colliders
  (`Gun.ClosestPoint`, `EnemyController.BodyRadius`), so the pickup lands before the capsule
  touches the gun: measured to the pivot, an enemy walked into a long gun's far end and kicked it
  away. When the navigator says it has arrived but it's still short (a gun against a wall or in a
  corner sits on a clearance-blocked cell), it stretches to 1 m, or gives up on that gun and tries
  the next, so it can never stand beside a gun for good. A gun it gave up on gets another try once
  it has moved 0.5 m or after 10 s; retrying sooner would send an enemy near a truly unreachable
  gun back into Rearm, and from there back to Aggro, every few seconds. It always hands back to
  Aggro. Having given up on every gun in reach, it waits Route Retry Time (3 s, a fixed constant on `EnemyController`) before trying again, so
  it doesn't flicker.
- **Patrol** walks a route of **area nodes** stored on the enemy (`PatrolRoute`). Each area is a
  circle or a rectangle lined up with X and Z (`GridArea`), so it can fill a room or run along a
  corridor. The enemy heads for an area's centre, and when the centre is occupied the nearest open
  spot inside the area counts as reached. Mode is Loop, Ping Pong or **Random**, which picks any
  point but the one it's on and the one it just came from (so with three points it can only go
  round, and with two it goes back). A route of **one point 4 m or more across**
  (`GridArea.Span`: a circle's diameter, a rectangle's longer side; `PatrolRoute.Wanders`) is
  wandered instead: random open spots inside it, each at least a quarter of the way across from
  where it stands, pausing at each. Spots come only from ground joined to the centre within the
  area (`PathGrid.RandomSpotInArea`), so an area drawn across a wall is wandered on its centre's
  side. Routes are edited with Scene-view handles (`EnemyControllerEditor`): a rim dot sizes a
  circle, and edge handles size a rectangle, as on a Box Collider. Each area is coloured by the
  bake's own clearance test, and a wandered point is labelled "0 · wander". In the Inspector,
  `PatrolPointDrawer` shows only the size field a point's shape uses. Labels number the Safe
  route 0, 1, 2 and the Aggressive one A0, A1, A2, so a runtime warning maps back to the right
  route. A pause at a point of Long Pause Time (10 s) or more isn't spent standing: every Pause Roam Min to Max seconds (3 to 6) they walk to another spot in the point's area, at patrol speed, and the pause still ends on time. No route for their mode means they idle at their post (see **Idle**).
- **Rooms** (`Room`, `RoomIndex`, in `Enemies/`). A room is a placed MonoBehaviour: one or more rectangles
  (an offset and a size on X and Z, relative to its transform, never rotated: `OnValidate` warns if the
  transform is rotated, and the rotation is ignored), an optional `BehaviourMap` and an In Use Time. Its
  name is its GameObject's name, so the Hierarchy and the warnings can't disagree. `GridArea` can't be
  serialized, so `Room.Rect` is the saved form and `AreaOf(i)` builds the world area. `Intel` builds the
  `RoomIndex` in `Awake` (the grids aren't enabled yet, so it finds them in the scene and uses `Covers`)
  and forwards `RoomAt` (the smallest room holding a point, on the point's floor), `LeafRoomAt` (that room
  only if it holds no rooms) and `Rooms`. Rooms nest: **the smallest holds**, so a floor-sized room can
  give a floor its map while an office inside it overrides a row. **"Holds" is tested on the ground:** A
  holds B when A is strictly bigger and every sample of B's rectangles, at the floor's cell size, lies
  inside A (so an L of two rectangles and a room fitted to its parent's wall work). Two rooms on a floor
  whose rectangles overlap while neither holds the other warn once, naming both, and rooms touching edge
  to edge are not an overlap. A room's floor is `PathGrid.At` of its first rectangle's centre, and one on
  no grid warns and is left out. Lookups scan the short list, sorted smallest first, with no allocation.
  `Contains` and `DistanceOutside` read world rectangles the index caches once (`Room.CacheWorldAreas`),
  since rooms don't move at runtime; in edit mode, with no index, they and the gizmos and `RoomEditor`
  read live values, because a designer drags them.
  **Rooms are read once, at load:** a room that is disabled then, or enabled after `Intel` wakes, is not
  indexed, and each case warns by name. A later rectangle of a room that lies off the first rectangle's
  floor warns too, and so does a room holding no rooms that covers more than half its floor's grid: it
  would be searched whole and carry speech across that area, so it should hold the offices inside it. Gizmos draw each outline (faint unselected) with the name and map, and an orange outline at the map's Mask Radius; `RoomEditor` gives move and edge
  handles with Undo.
- **Idle** (`IdleState`). Where an officer starts is their **post** (`PostPosition`, `PostYaw`,
  recorded in `Start`). After a search, a fall or a shove, farther than Post Tolerance (1 m, fixed) from it,
  they walk back at the current patrol speed and turn the way they started, so an idle officer is
  never left wherever their last job ended. The walk gives up after Search Travel Limit with a log
  line and starts again the next time they're at their post, so a post another officer stands on can't
  have them pushing at it for good. Idle activities (a desk, a TV) are a later piece.
- **Firing range** (`RangeSpot`, in `Enemies/`). Set an enemy's Post Spot to a `RangeSpot` and their post is
  the spot (`Start` records its `Position` and `FacingYaw`), so they walk there at the start of play. The
  facing is toward the spot's Target, never its own arrow (the arrow is used only with no target, which
  warns). Practising isn't a state: at the post, armed, with a target in the gun's range, `IdleState`
  aims (`Practising`, which gives the Aim stance and the range target to the animator) and every Practice
  Interval Min to Max (1.2 to 2.5 s) fires a **real** shot at it, so the Gunshot stimulus is real too.
  Before each shot it calls `Room.NotePractice()` on the spot's room, so the shot's own Gunshot is already
  masked: `Room.InUse` is true for In Use Time after the last one. It marks that room **and every room
  around it** (through `Parent`), because the spot's room is the smallest at it, often a lane inside a
  "Range" room that carries the Gunshot row and the Mask Radius; marking only the lane left the range
  never in use and every practice shot a real gunshot. `RangeSpot`'s masking check walks the same chain. A range's map sets Gunshot to Ignore, Only While In Use,
  Mask Radius 8, so shots in or within 8 m of the range bring nobody who doesn't see Ruby, and when they
  stop (grabbed, sent searching) the range goes quiet after In Use Time and a shot there is a shot again.
  Leaving Idle, or walking off the post, lowers the gun. Rules, not fallbacks, each in the log: unarmed at
  a range post they stand there and doesn't practise (one log line); a target out of the gun's range warns
  once and they don't fire; a spot in no room warns once (nothing masks its shots). Wary isn't aggressive
  here: an aggressive enemy with an Aggressive route walks it, as the table already sends them, and one
  without goes back to their post and practises on, wary or not. An enemy with no post spot pays nothing
  new per frame.
- **Patrol Normal and Aggressive.** These are modes of Patrol, not states. `IsAggressive` is
  **derived on every read, never stored**: they have been after Ruby (`EnemyMemory.Wary`, set by any
  search for an attack, including the one after a lost fight, and by a glance at a known body) or knows she is about (`KnowsRed`: they have seen her, or a job mate who had told them). Neither is
  ever cleared, so they never go back. **`EnemyReactions.BecomeWary()` is the one writer of `Wary`**,
  so there is one place to change or log it and no enemy can latch wary by a route nobody can find.
  `BecomeWaryAt(at, why)` is for something sensed at a spot: when that is their own job's place it goes
  through the job first, so the whole team goes wary together (see **Group jobs**). Only what an
  officer themselves has been through makes them aggressive: a **level-wide alert** (the first Aggro flipping every patroller) was removed for now
  and is to be explored later. `ActiveRoute` is the Aggressive route when they're aggressive and have
  one, else the Safe
  route: **an aggressive officer with no Aggressive route keeps walking their Safe route, and
  an idle one stays at their post, alert**. That is a rule and not a fallback, and the Inspector
  says which applies. `PatrolState` reads `ActiveRoute`, and starts over on the nearest point of a
  new one when it changes. Aggressive, they walk at Aggressive Speed Factor (1.25) times Patrol
  Speed, and spends each pause looking around (the point's pause override, else Look Time)
  instead of standing. The table has `Idle` to `Patrol` when `HasActiveRoute`, and `Patrol` to
  `Idle` when not, so becoming aggressive can hand an idle officer a route to walk. Nothing may ask "does they
  patrol?" without saying for which mode: `HasPatrolRoute` is gone.
- **Knowledge** (`Enemies/`). An officer's memory (`EnemyMemory`) holds the newest fact about Ruby
  (`RubyFact`: position, time and a `Knowledge` source, Seen, Heard, Told or Pool), whether they have
  **ever seen her** (`HasSeenRed`, never cleared), and the bodies and loose guns they have already
  taken in. Two more records are their alone and never expire within a level:
  `TimesSentToLook` (how many jobs they have been put on, against Sent To Look Threshold) and
  `WasToldOfRed` (a job mate who had seen her told them she is about). `KnowsRed` is
  `HasSeenRed || WasToldOfRed`, and it is what `IsAggressive` and the problem-to-attack upgrade
  read. Being told is not seeing: only `HasSeenRed` lets them tell others in turn (see **Group jobs**). Between two things they know, **the newer by time wins**, and on a tie their own beats the
  pool's, because it needed no publish to reach them. `EnemyAwareness.TryGetNewestRed` combines their
  memory with the pool's published snapshot. Out of sight, Aggro hunts the guess made from the newest fact
  (`TryGetGuess`); a fact carries her heading (`RubyFact.Velocity`) along with where and when.
  Seeing, remembering and speaking aren't states: `EnemyAwareness.Update` runs beside the machine.
- **The shared pool** (`KnowledgePool`, a plain class that `Intel`, one per scene, owns and ticks;
  `Intel` forwards `Report`, `ReportBody`, `ReportGun` and `Published`). Officers report into an
  inbox, and every Intel Interval (1 s) the inbox is merged into a **new** immutable `IntelSnapshot`
  (`Published`) that all of them read. It is stale on purpose: a fight Ruby has left doesn't end the
  instant she's out of sight, and an officer mid-decision holds a snapshot that can't change under
  them. It holds her newest reported spot and the bodies
  and loose guns the squad has reported (only ever added to within a level). **Speech** is the
  opposite: while an officer sees her they call her position out at once on first sight and then
  every Speak Interval, and every other living officer within Speech Range (15 m) learns it
  immediately as Told. **It also fills the speaker's room** (`Intel.Say`): every living officer on their floor standing in
  their `LeafRoomAt`, past Speech Range or not, hears too, but only when that room holds no rooms, so a floor-sized room carries nothing. The log says how many heard only
  because of the room. Range is **path metres along the floor** (`FloorReach`, over
  `PathGrid.FloodFrom`), so a shout doesn't carry through a wall or to another floor. An enemy on
  no grid, or a scene with no `Intel`, gets one warning saying so, never a silent fallback.
  **Speech is also word of mouth:** each officer who hears them is `Alert()`ed into Aggro whatever they
  were doing (not the grabbed, ragdolling or dead, which `Alert` refuses), so everyone in earshot
  joins the fight; the log says how many heard and how many joined (see **Fights**).
- **Stimuli and hearing** (`Core/Stimuli/`). `Stimuli` is a static bus that weapons and the grapple
  emit into without knowing officers exist: a gunshot (`Gun`, `GunController`), the whip landing
  and an impact. A `Stimulus` carries a kind, a position, an **origin** (where the thing came
  from), a loudness in metres and the object that made it. `Intel` floods the sound's grid **once**,
  unpenalised, out to its loudness and lets every officer inside it hear (`EnemySenses.Hear`), so a
  sound never crosses floors or goes through a wall, and nobody hears their own shot or, once dead,
  anything at all. **One scratch flood at a time.** `FloodFrom` returns the grid's one scratch field,
  and the next flood overwrites it. Speech, sound and calling people in all read it through
  **`FloorReach`**: `Begin` floods, `Measure` reads one enemy off the field, `End` checks that
  nothing flooded in between (`FlowField.Generation`, bumped by every flood) and logs an **error**
  if something did. That error is a guard, not a fallback: the callers **collect first and tell
  afterwards** (`Say`, `Hear` and `JobBoard.FindCallees` gather their hearers into a list while the
  field is intact, then act on them), because acting can flood: a reaction recruits, and recruiting
  floods. Each use owns its own `FloorReach`, so each keeps its own warn-once sets (no grid, nothing
  walkable in reach, a listener off the grid). A sound from Ruby teaches the hearer where **she**
  was (Heard), never the whip's anchor, and only when the map has a row for it there that applies and
  isn't Ignore: the row is looked up first, so Ruby firing inside a busy range's mask teaches nobody
  anything (`EnemySenses.Hear`). Loudness and speeds live in the `StimulusRules` asset (see the manual's tuning table).
- **Origins** (`Disturbance`). The grapple stamps whatever it holds with where Ruby is, every step
  it's on the chain and once more as it leaves, so a throw's origin is where she stood at the
  release. The stamp sits on the body's root and on every Rigidbody under it, so a thrown officer's
  limbs carry it once they go live. A carrier that hits something at Notice Speed or more makes an Impact sound (one per
  Impact Cooldown per stamped body, so a tumble is a few thuds and not dozens) and passes the
  origin on, with the original time, to what it hit, so a knock-on leads back to the throw. The
  Impact is placed half a metre off the surface hit, flat, on the carrier's side (`Disturbance.OffTheSurface`): a contact point on a wall's face is a blocked cell belonging to neither side, and everything that needs open ground there took the asker's side, so an enemy inside a room checked a crate striking its outside wall from within. A
  record lasts Origin Lifetime (5 s), and a character that stands up is cleared, because one on
  their feet is sensed by sight and a stale record would make them thud at every wall they bump.
  **What they see moving leads back to its origin; what they hear doesn't.** A body in motion shows them which way it came from, so they go to where it was thrown from. A sound can't, so they go to where it happened: an impact is checked where it landed, never at the thrower (`EnemySenses.Hear`). The origin still travels with the sound, so a knock-on they then see moving leads back to the throw.
- **The behaviour map** (`BehaviourMap`, an asset assigned to `Intel`'s Default Map). One row per
  `StimulusKind`: a **priority**, 1 to 5, and a **response**: Ignore, SearchForProblem or
  SearchForAttack. Defaults: body, fast object and gunshot 5 (attack); impact and whip landing 3,
  moving object and loose gun 2 (problem). A priority outside 1 to 5 (an asset saved on the old
  10s-and-up scale) warns once and is held to the range; the component's Reset refills the
  defaults. A kind with no row, or a missing map, logs one warning and
  is treated as no reaction. **Rooms override rows** (`Intel.TryGetRow(point, kind, heard, out row)`): a `Room`'s map
  is read from the smallest room holding the point out through `Parent`, the first whose own row for the kind
  applies (`BehaviourMap.TryGetOwnRow`, which never warns of a missing row, since a room leaves rows unset on purpose), and
  then the default map. A row marked **Only While In Use** applies only while `Room.InUse` (true for In Use Time after the last `NotePractice`, a firing-range shot), else the room around it decides. **Mask Radius** (on a map, 0 is off) is for sounds
  only (`heard`, so sight is never masked): within it of a room on the same floor, measured in a straight line through walls, that room's map wins
  when its own applicable row for the kind is Ignore. An armory is a map that ignores Loose Gun. **The Body rule:** a room map's Body row set to
  Ignore is refused with one warning per map and never used, so an enemy can't be made to walk past a corpse. Problem and Attack differ in how they search (see **Searching**), and
  Investigate can upgrade a Problem to an Attack. **`BehaviourMap.Severity`** ranks a response for
  every "is this more serious" test: Ignore 0, problem 1, attack 2, written out, because the enum's
  numbers are saved by assets and a value added later takes the next number whatever its place in the
  order. `GroupJob.Elevate` compares by it, never by the enum. A value with no
  place in `Severity` logs an error (and is ranked by its enum number meanwhile). What a row
  produced travels as an **`Incident`** (point, reason, priority, whether it makes a Search and whether it says nothing of
  Ruby, built once where the stimulus is judged and passed on whole, so a new value can't be added to one call and missed in another).
- **Senses** (`EnemySenses`, one per officer, scanned from `FixedUpdate` every Scan Interval (0.2 s, fixed) at a
  staggered phase). **Every sight notice is the cone only** (`EnemyVision.InCone`: View Angle, out to
  View Range, with a clear line), spec `docs/specs/2026-11-19-sight-through-the-cone.md`. An all-round
  Motion Radius (20 m on the prefab) and a corpse range of its own (15 m, in the cone or the close
  circle) gave them eyes in the back of their head; a thing behind them is now unseen, though a body
  thrown past still makes an Impact sound when it lands. Search coverage keeps the close circle
  (`SearchCoverage`): ground right beside a searcher's path is passed by, and cone-only coverage had
  them stop and turn to clear spots a step away. They notice **bodies in motion** in the cone that they can see clearly:
  at Notice Speed a MovingObject, at Alarm Speed a FastObject, once per Origin Lifetime for each
  body, and only a *taken* reaction counts as one. **A moving thing is judged by what it is as well as
  how fast it goes:** a flying pistol also reads as a LooseGun and a flying enemy as a Body, and the
  stronger of the two rows wins, the response by `Severity` and the priority by number, each on its
  own, an Ignore row adding nothing (an Ignore at 5 must not lift a real row's priority); the log
  names which row each came from. Its point is its origin, where Ruby threw it from, and it teaches
  where she was; with no origin it is only where it lies, which says nothing of her. A flying enemy
  makes a **Search** there, and so does anything at Alarm Speed (something that fast could kill, and
  no ordinary person throws it); a slower gun or crate stays a Check. An officer on their feet is never
  a moving body, only one held on the chain or tumbling, and a **tumbling enemy in their view counts at
  any speed**, since one knocked down or killed may hardly move. The sweep reaches View Range, and its buffer is shared and
  **grows** when a sweep fills it (walls and bodies share the Default layer, so no mask can leave
  the walls out, and a full buffer used to drop bodies with a warning). The motion scan is skipped
  while they are in Aggro or Rearm: `Investigate` would refuse whatever it found, and a refused body
  isn't marked taken, so it was swept, judged and refused again every scan.
  **Enemies on the chain** (`ScanHeld`, first in each scan): another enemy held on the chain, in the
  cone with a clear line. Standing where they were grabbed, they are walked over
  to and searched around, once per hold and only once the reaction was taken, kept apart from the
  motion scan's record or watching a grab would blind them to the throw for Origin Lifetime. Moved
  more than Dragged Distance (0.5 m, `HeldMovedBy`, so a charging throw's jiggle isn't it) from
  where they were grabbed, or thrown within one scan of the release from a spot they see
  (`WasThrownWithin`), someone is plainly on the other end of the chain: `EnemyReactions.SeeAttack`
  teaches them where Ruby is right now, as Heard, and `Alert()`s them into Aggro. That, sight of her
  and `EnemyReactions.GoAfterRed` (a provoked searcher, see **Distraction**, who alone goes in blind to her until she is in their view) are the only ways to Aggro through the senses. **Seeing an enemy killed** (`ScanDeaths`, any enemy, in view with a clear line to the spot they died, within a scan of it) always sends them to check where what killed them came from, as a gunshot does (`Incident.Mobilises`), or where they died if nothing shows it. `ImpactDamage` stamps a body with its killer's origin, since a hit doesn't otherwise. One knocked down, or a flying enemy whose
  throw they didn't see, is a Search at the throw's origin (or where they lie), with no Aggro. They notice **corpses** in the cone
  with clear sight, and **loose guns** in the cone and sight if they are armed. A body or gun they
  haven't seen goes on the squad's books and they investigate it. A body the squad already knows they
  **glances** at: they face it and look from where they stand without walking to it, and are wary
  from then on. A gun the squad already knows gets a log line and nothing more. A booked body or gun counts as seen even when they're too busy to investigate it
  (Aggro, Rearm, or a search at equal or higher priority). Ruby is never a moving body: she's the
  one being hunted, and sight of her is Aggro.

- **Debug overlays**, only in the editor, and the heavy ones off by default: `PathGrid`'s Always
  Show Grid and Show Open Cells draw the whole floor, and `Intel`'s Show Sound Range draws each
  recent sound's reach (the cells its flood covered, shaded by distance, beside the circle the same
  loudness would cover in open air). Draw cost grows with the grid's cell count, so leave them off unless
  you're looking at them. With `Intel` selected in Play, each open job shows as a ring at its place
  (cyan for a Check, orange for a Search), a line from the lead to
  every member and from each member to the spot, the checker ringed, each member out checking a distraction ringed too with a ring where they were sent and a line to it, and a label with the job's id,
  kind, step, members over cap, reason, priority and checker. `Intel` also marks, in Play whenever
  Gizmos are on (no toggle), each open job's Job Match Radius round every one of its places, as `Covers` matches (a grown search has one per patch; in edit mode, with `Intel` selected, the same ring round `Intel`, for sizing it), where Ruby was last reported ("reported Xs ago") and the bodies and
  loose guns the squad has on its books. A selected enemy with recent news of Ruby draws Attack Near Radius round where she was last reported, orange, with a label (only when selected: drawn on every enemy, the rings piled up), and in edit mode it draws round them, for sizing it. In Play it also draws the path it hunts: the vanish point, her heading and the guess. Every officer's routes show
  faintly, cyan for Safe and orange for Aggressive.
