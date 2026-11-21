# Enemy path tracking, and trimming the enemy's fields

**Status:** built 2026-11-18, to `docs/plans/2026-11-18-enemy-path-tracking.md`; awaiting the user's play-test. Branch `enemy_states_0.2`. Approved approach: B (a guess ahead along
the floor). Patrol together (piece 5 part B) is shelved for this *(user)*. The pathing clean-up
("smarter paths to Ruby without being blocked off") is the next piece, designed separately.

## Intent

Enemies lose Ruby the moment she ducks behind a crate or round a wall. They feel dumb *(user)*.
What the user wants instead:

- **They know her walk path, not her position** *(user)*: where she was when they lost her, and
  where she was going. They hunt that, not a dot.
- **They only lose her after not seeing her for some time** *(user)*. One timer, not two.
- **The path feeds the search that follows** *(user)*: it covers where she was going as well as
  where she vanished.
- **Simplify the enemy** *(user)*: remove useless and redundant tuning fields. The audit's four
  groups below are approved as a whole.

### Why today's rule fails

Sight Follow Through (20 s on `officer.prefab`) gives a hunter Ruby's real position after losing
sight, but Aggro Time (1.5 s on the prefab) keeps counting the whole time and only real contact
resets it (`AggroState.Tick`, the `_lostTimer` lines). So the hunt ends 1.5 s after losing sight,
whatever Follow Through says. On top of that, an armed enemy following through holds their firing
spot and turns to face her through the crate (`MoveToSpot`, `FaceTarget`), which reads as either
dumb or seeing through walls.

## Goals, non-goals, anti-goals

- **Goal:** ducking behind cover doesn't shake them. Breaking sight and *staying* gone for Lose
  Sight Time does.
- **Goal:** they go where she was heading: through the doorway she ran at, round the side of the
  crate she was moving toward.
- **Goal:** fewer fields on `EnemyController`, each with one meaning.
- **Non-goal:** following her real steps while she's hidden. Rejected *(user, option B of the
  trail question)*: the trail stops where she vanished, so doubling back behind cover fools them.
- **Non-goal:** better routes to her (the pathing clean-up is the next piece).
- **Anti-goal:** psychic enemies. Nothing reads her true position while she is out of sight.
- **Anti-goal:** enemies standing still facing a wall. While the timer runs they are moving toward
  the guess or looking around at it.
- **Anti-goal:** a silent fallback (global rule). Every case the design doesn't cover warns.

## Approaches considered

When sight breaks they keep **where she vanished** and **her heading** (direction and speed). How
they turn that into a guess:

- **A. Straight ahead:** project along the heading, stopping short of the first wall. Cheapest, but
  dumb at corners: running at a doorway and turning puts the guess against the wall beside it.
- **B. Ahead along the floor (chosen, user):** one bounded flood from the vanish point; the guess is
  the reachable cell furthest along her heading within Guess Distance. It goes through the doorway
  she ran at and behind the crate on the side she was heading for. One flood per lost sighting, not
  per frame.
- **C. By room:** guess the room she was entering and search it. Coarse, and does nothing on open
  ground with no rooms. Dropped.

## The design

### 1. What they know: a fact with a heading

`RubyFact` gains a flat **Velocity** (metres a second, zero when unknown).

- **Seen:** while an enemy sees her, `EnemyAwareness` keeps a short record of her positions (her
  last half second in view) and the Seen fact carries the velocity averaged over it, so one frame's
  twitch doesn't swing the heading.
- **Told and Pool** copy the fact whole, so a call-out carries the heading and mates hunt where she
  is going too. `WithSource` keeps the velocity.
- **Heard** (a gunshot, an impact) has no heading: Velocity zero.
- **The follow-through fact goes:** nothing writes her live position while she is out of sight.
- **They call out when they lose her** *(my call, so the shared guess the user approved works)*: the
  moment sight breaks, the enemy says the vanish fact, position and heading, once (`Intel.Say`, as
  the first-sight call-out). Today's call-outs happen only while seeing her, so mates and the pool
  were left with a call-out up to a Speak Interval older than the vanish, and a first-sight call-out
  has a single sample and no heading. Word of mouth from it alerts hearers as any call-out does.

### 2. Where she's going: the guess

From a fact with a heading, **the guess** is: flood from the fact's Position out to Guess Distance
(path metres); of the reached cells, take the one that is furthest along her heading (the score
rewards both alignment with the heading and distance travelled; the exact score is the builder's,
see below). Velocity zero, or a flood that reaches nothing, means the guess is the fact's Position
itself, a stated rule logged once per fact, not a fallback.

- **One flood per fact per enemy**, never per frame: the guess is cached against the fact it came
  from (its Time and Position) and redone only when a newer fact replaces it.
- **The flood rule holds:** floods are read through `FloorReach` (or the same Begin/End generation
  guard), collected first and acted on afterwards. It runs in the state's step, not inside a speech
  or hearing read.
- **Guess Distance** is a new field on `EnemyController`, default 8 m. *(My call: deriving it from her
  speed times Lose Sight Time, as first proposed, gives 15 m/s x 4 s = 60 m.)*

### 3. The hunt: one timer

**Lose Sight Time** (new field, default 4 s, the user's earlier tuning of Aggro Time) replaces Aggro
Time, Sight Follow Through and Unseen Hold Time.

- **Contact is seeing her:** a clear line (`CanTrackPlayer`), at any distance. Give Up Radius goes.
  A fight mate in contact still counts as contact for the whole fight, as now.
- **A glimpse grace** *(user, 2026-11-18: option A)*: sight is one ray to her centre per frame, and at
  a crate's edge it flickers. She counts as out of sight only after **0.25 s** without it (a constant,
  not a field). During the grace they keep their firing spot and its claim, face where they last saw
  her (never her true position), make no vanish fact and no guess, and don't fire. Losing sight, the
  vanish fact, the call-out and the guess all date from the moment the grace runs out, and the vanish
  fact is the last place and heading they actually saw. Without it, each flicker released the spot,
  flooded a guess, turned them toward it, and re-picked a spot when sight came back. Lose Sight Time
  counts from the last real sight, so the grace is inside it, not added to it.
- **The timer** counts while they have no contact, resets on any contact (theirs or a mate's), and
  when it reaches Lose Sight Time `LostPlayer` is true and the table sends them to Searching, as now.
  It doesn't pause while they stand at the guess: there is no hold at a spot any more.
- **Knowing nothing** (no fact at all) still ends the hunt at once, with its log line, as now.
- **While the timer runs, out of sight:**
  - Everyone walks to the guess of the newest fact (`TryGetNewestRedFact`), at Chase Speed. A newer
    fact (a mate's call-out, the pool) moves the guess and they turn to it.
  - **Armed:** they hold no firing spot while she's unseen, and never face her true position. When
    they reach the guess (within Search Reach, with a clear line to it) they look around it, the
    look-around sweep, until sight returns or the timer ends.
  - **Unarmed:** the same walk and look-around.
  - **Crowding** *(my call)*: everyone told the same fact gets the same guess, and Known Spot Radius and
    the stall rule are gone. A hunter whose walk to the guess reports `Blocked` (the navigator's Stuck
    Time) counts as arrived and looks around where they stand, with a log line. No per-hunter spread.
  - **Sight returns:** the normal in-sight Aggro resumes at once (firing spots, the band, firing).
- **In sight** nothing changes: firing spots, the gun's band, backing away, firing.
- **Someone who never saw her in this fight** (got up from a throw, told by word of mouth) hunts the
  guess of what they were told and loses her after Lose Sight Time like anyone. *(Consequence, my
  call: Unseen Hold Time, 0.7 s, made such an enemy give up quickly so a team that came to check
  didn't leave without them. That special case goes with the field. If it shows up again in play,
  it comes back as a rule, not a field.)*
- **The provoked enemy** (`ProvokedUnseen`) keeps its rule: the timer waits while they run at where
  they were told (`OnProvokedRun`, bounded by Search Travel Limit). Arriving, it counts as for anyone.
  Their told fact has no heading unless the fact they were given has one.

### 4. The search after a lost fight

`AggroState.Exit` requests the search from **where she vanished** (the newest fact's Position, moved
to the nearest reachable ground as now), and the job then **grows to take in the guess**
(`GroupJob.GrowAt`, with a why such as "where Ruby was heading"). Joining an existing job
(`JobBoard.JoinFromAggro`) does the same: the guess is grown into the job they join or start. With no
heading the guess is the vanish point and nothing extra is grown.

### 5. Gizmos

Selected in Play, an enemy draws the fact they are hunting: the vanish point, an arrow for her
heading, the guess, and the line between. This replaces the Known Spot Radius circle. The timer's
remaining time can go in the existing label if there is one.

### 6. The field audit (approved, all four groups)

| Group | Field | Change |
| --- | --- | --- |
| Replaced by this design | Aggro Time | removed: Lose Sight Time |
| | Sight Follow Through | removed |
| | Unseen Hold Time | removed |
| | Known Spot Radius | removed; reaching the guess uses Search Reach |
| | Give Up Radius | removed; contact is a clear line at any distance |
| Merged | Clearing Speed Factor + Follow Speed Factor | one **Search Creep Factor** (`FormerlySerializedAs("_clearingSpeedFactor")`, carrying 0.75) |
| Renamed | Squad Spacing | **Spot Spacing** (`FormerlySerializedAs("_squadSpacing")`, value carried) |
| Switched off | Roam Look Variation (0 on the prefab) | removed with its randomising code; every look lasts Look Time |
| Plumbing | Scan Interval, Post Tolerance, Route Retry Time, Grab Pop Up, Grab Pop Out | constants at today's prefab values (0.2, 1, 3, 3, 2) |
| New | Lose Sight Time (4 s), Guess Distance (8 m) | added |

57 fields become 47. The code behind the removed fields goes too: in `AggroState` the hold at the
known spot (`_holdingAtKnown`, `_dwellTimer`, `HoldTime`, `_sawHer`), the stall-behind-someone rule
(`StalledBehindSomeone`, `_closestToKnown`, `_stallTimer`, `_stallSpot`, `StallProgress`), and
`FollowingThrough` in `EnemyAwareness`. `Stuck Time` stays: the navigator still uses it.
`OnValidate`'s Give Up Radius clamp goes; any rule that compared against it is restated or removed.

**Inspector work for the user:** the scene override of Aggro Time (8 s on one enemy) is lost with the
field; retune Lose Sight Time there if that enemy should differ. Removed fields leave stale values in
the prefab and scene YAML, which Unity drops on the next save.

## Constraints

- The two-axis rule, the transition table's order, `States never name other states`, and **no
  serialized field moves** out of `EnemyController` or `Intel` (renames use `FormerlySerializedAs`
  on the same component).
- One scratch flood at a time; a job never moves anyone.
- Enemies are they/them in comments, logs and docs.
- No silent fallbacks: every rule that stands in for the designed case logs.
- Verification is `python tools/compile_check.py` and the user's play-test; there are no tests.

## Left to the builder

- The guess's exact score (alignment against distance), and how many of her positions the heading
  averages over, within "her last half second in view".
- Where the guess cache lives (`EnemyAwareness` is the natural home).
- How the look-around at the guess reuses the existing sweep (`EnemyMovement`'s look sweep).
- Which docs change: `docs/architecture/enemy-ai.md` (Aggro, Fights, Knowledge),
  `enemy-searching-and-jobs.md` (the search after a lost fight), `project-history.md`, and
  `CLAUDE.md` only if a cross-system rule changes.

## Play-test (the user)

- Duck behind a crate in view: they come round the side you were heading for, and don't give up
  for Lose Sight Time.
- Run at a doorway and through it: they follow through the doorway.
- Double back behind cover: they go the wrong way, then lose you once the timer runs out.
- After losing you, the search covers where you vanished and where you were heading.
- Armed enemies never stand facing you through a wall.
