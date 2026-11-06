# Enemy States 0.2: a squad that thinks

**Status:** design, approved 2026-10-28. Branch `enemy_states_0.2`, from `main`. It
will be built one piece at a time, in the order below, with a check-in and play-test after each.
It takes over the unbuilt parts of `2026-09-29-enemy-states-v2.md`: the Cautious state and route
(now Patrol Aggressive and the Aggressive route), Search v2 and sound. Every piece's rules are
decided here except piece 9 (breakables), which comes much later and is designed at its check-in.
Each piece's plan is written when it's next, so it builds on what the pieces before it became.

## Intent

Officers stop being lone sentries that see in a circle and chase on sight. They become a police
squad defending their building. They sense the world, know things (some of it out of date), and
decide together who does what. In the user's words:

- "We want a system like Hitman. Where enemies use a complex web of choice and design ideas and
  some unique interactions we place per scene that will make enemies react a certain way."
- "Enemies like guards work in groups, they will check loud objects together, check the source of
  sound together, patrol in groups, break off, feel like a real police squad working together to
  protect their home."
- "It's not a stealth game, but Ruby can use stealth to her advantage to drop aggro and give her a
  breather on the floor."
- "The other lifeblood of this game is the robust enemy AI."
- "Officers will check for other officers, before making a decision as to what they should be
  doing."

The model is Hitman (2016), as described in "The AI of Hitman (2016)" (Game Developer, Tommy
Thompson). Two of its ideas are taken whole:

- **Each officer has a knowledge base.** Part of it is his own (what he has seen and heard), and
  part is shared with the squad.
- **The shared part is a pool that updates periodically**, so it can be out of date. That's what
  lets Ruby drop a fight: officers act on the pool's last update, so once she breaks sight they're
  hunting where she *was*.

Two others are adapted:

- Hitman's **situations** become **group jobs**. A job sits above the officers, works through its
  own steps and gives each member a role.
- Hitman builds its characters on **behaviour trees**. Officers here keep the state machine they
  already run on (the user's decision: no crowds, so no need for trees), and the "smaller states
  inside the main ones" live inside it.

## What the player sees

This table is the play-test script. Rows marked *(user)* are from the user's design notes; the
rest are my guesses, flagged for review.

| Moment | What the officers do |
| --- | --- |
| Ruby sneaks up behind a patrolling officer | He doesn't see her from behind at a distance: he sees in a rounded pie-shaped view in front of him, plus a small circle all round him, so Ruby can't sit directly behind him *(user)* |
| Ruby throws a body past three officers in a room | One is spooked first. He alerts the other two if they're near, and the three start checking together. One goes to **where the body came from** (not where it landed), and the other two follow slowly behind *(user)* |
| The checker finds nothing | He gestures to the others, and after about 1.5 s they all go back to their normal patrol *(user)* |
| The checker finds Ruby | Every officer in range engages her *(user)* |
| The checker finds a dead body | They all search the area together for Ruby (Searching for Attack, as a group). When the search runs out, they walk their Aggressive routes *(user)* |
| The checker finds broken objects or a loose gun | They search the area together (Searching Plus). A loose gun in an armory room is normal and doesn't count *(user)* |
| Ruby throws a body into a table near an officer | He reacts to the table moving as well as to the body. Both lead him back to where Ruby threw from *(user)* |
| A gunshot rings out | Officers who hear it search for Ruby where it came from (Searching for Attack). A crash or thud gets them checking for a problem instead (Searching for Problem) *(user)* |
| Ruby breaks line of sight and hides in a room | The officers who lost her count each other. Up to three search the room she went to together, checking behind sofas, desks, tables and crates, with one going in at a gesture while the others hang back. Any others go back on patrol, aggressive *(user)* |
| Ruby gets away clean | The search times out, and the searchers walk their Aggressive routes. They never go back to Normal patrol, because they've seen her face *(user)* |
| An officer in Aggro runs past other officers, whatever they're doing | They join his fight, as if he'd told them *(user)* |
| A firefight breaks out in one room | The officers who hear it join the fight nearest them. Up to four make the **main squad**, which fights her. Everyone else is the **standby squad**, as many as there are: they hang back as backup, and shoot when they have range and a clear line *(user)* |
| A main squad officer is grabbed, knocked down or killed | One standby officer moves up at once to take his place *(user)* |
| Officers line up to shoot | They spread out, never stand in front of each other's guns or on each other's spots, and step aside before firing if a squadmate is in the way *(user)* |
| Ruby breaks line of sight mid-fight and keeps running | The officers fighting her run to where they last saw her, not to where she is now, and search from there. Breaking sight is how she drops a fight *(my reading of the user's goal)* |
| Ruby looks into a break room | Officers without a route are busy at their posts: one watching TV, one cleaning his gun, one at a desk. Each faces his activity, so his view points at it, and he aggros on sight like anyone *(user)* |
| Ruby watches the halls after the alert | Up to two groups of two or three officers patrol together behind their leaders. Now and then a follower breaks off to search an area or a room, then rejoins his group, another group nearby, or his own route *(user)* |
| Officers are practising on the firing range, and Ruby fires a shot next to it | Nobody reacts: gunfire is normal in and around a range in use. If nobody is practising, a shot there is a shot *(user)* |
| Ruby sets off an alarm | The nearest officer goes and switches it off, then goes back to what he was doing *(user)* |
| She sets it off again, and again | By the third time the officer who switches it off is suspicious, and he and those near him search around it *(user; "third" is my call)* |
| Ruby, standing still in the dark corner of a room the squad is clearing | The pointman checks each hiding spot in turn. If she's in one, he finds her. If she slips past while his back is turned, she's out *(my guess)* |

## Goals, non-goals, anti-goals

Goals:

- Officers see in a pie-shaped view in front, plus a small circle all round, not a big circle.
- Officers react to what they sense: sight of Ruby, bodies, fast-moving objects (and what those
  set moving), sound, loose guns and, much later, ripped-out lights and broken props. Each kind of
  thing has a weight that decides what he does.
- Each officer remembers what he has sensed. The squad shares a pool of what's been reported,
  which updates periodically and can go stale.
- Officers work in groups of up to three: checking a disturbance together with one going ahead,
  searching for Ruby together, clearing a room's hiding spots, patrolling together and breaking
  off.
- Officers fight as squads: a main squad of up to four, and a standby squad behind it that fills
  the gaps. They keep out of each other's lines of fire and off each other's spots.
- The main states gain smaller states inside them: Patrol Normal and Aggressive (alone, or in a
  group behind a leader), Searching for Problem and for Attack (alone, or Plus with company), Idle
  at an activity, and Aggro's main squad and standby roles.
- Things placed in a scene shape the reactions: rooms and floors with their own behaviour maps
  (a firing range where gunfire is normal, an armory where loose guns are), activity spots (a
  desk, a TV, a gun-cleaning bench, the range), alarms, hiding spots, Aggressive routes and,
  much later, light fixtures and breakable props.
- Officers walk like people: a margin off wall corners, and curves rather than sharp turns.

Non-goals:

- **Behaviour trees, crowds, civilians, VIPs and disguises.** No crowds means no need for trees.
- **Audio.** "Sound" here means what officers hear. Making the game play sounds is separate work.
- **New animation clips.** Gestures and look-arounds are stand-ins (a pause, a turn to face) until
  clips exist, and activities get empty slots in the animation set that play the Rest idle, with a
  warning, until clips are added.
- **Melee and Ruby's revolver rework.** Unchanged.
- **Making it a stealth game.** Stealth is a way to catch a breath, not the goal of a level.

Anti-goals, failures even if they technically work:

- **An all-knowing squad.** Every officer on the floor converging on Ruby the moment one sees
  her. Only officers who hear a fight or are told join it, only four of them fight her head on
  while standby hangs back, and the rest of the level only goes aggressive, acting on the stale
  pool.
- **A dumb squad.** Two officers checking the same spot, a squad jammed in a doorway, officers
  walking through each other, an officer standing beside a body and not reacting, or a whole room
  standing still after a gunshot.
- **Flicker.** Officers switching jobs or states back and forth each frame because two stimuli
  compete.
- **Breaking the two-axis rule.** Moving and firing are still not states. A smaller state is only
  for something an officer does *instead of* something else.
- **States naming each other.** Transitions stay in `EnemyController.BuildTransitions`, as today.
- **Silent fallbacks, ever** *(user: "don't fallback silently ever")*. When something can't be
  done as designed (no path, no room, no spots, no grid, a missing component), the officer stops
  and one warning says why. He never quietly does something else instead. A rule this design
  writes down for a case, such as an aggressive officer with no Aggressive route walking his Safe route,
  is not a fallback, and the Inspector shows it.
- **Anything that slows Ruby's verbs or moves her.** None of this touches the player.

## How it fits together

```text
  the world ──► SENSES (each officer) ──► his MEMORY ──► reports ──► SHARED POOL (the squad)
                  sight cone, close circle,  Ruby's face,            updates every Intel
                  hearing, noticing          last sighting,          Interval: Ruby's last
                                             what he's sensed        reported spot, bodies
                                                                     found, disturbances,
                                                                     open group jobs
                                    │                                      │
                                    ▼                                      ▼
                         DECIDE: what is this, and is someone already on it?
                                    │
                  ┌─────────────────┴──────────────────┐
                  ▼                                    ▼
          GROUP JOB                           his own STATE MACHINE
          steps, and a role for               main state + smaller state:
          each member                         Patrol Normal/Aggressive (+ groups),
          (checker, follower, leader,         Searching Problem/Attack (+ Plus),
          pointman, main, standby) ─ role ─►  Aggro main/standby, Rearm, ...
```

- **Senses** turn the world into **stimuli**: "I saw a body at X", "I heard a gunshot from Y",
  "something flew past, thrown from Z". Each stimulus has a kind, a place, where it came from, a
  time and a priority.
- **Memory** is an officer's own knowledge. He acts on it at once.
- **Speech** is officers talking across a room. What one says reaches those in earshot at once.
- **The shared pool** is the squad's knowledge, for the whole level. Officers write reports into
  it, and it publishes them every Intel Interval. Everyone reads the published copy, so the pool
  is never newer than its last update.
- **Deciding** is where an officer "checks for other officers": is this already being handled
  (an open job on the same thing)? Who's free nearby? Is Ruby known to be near here?
- **Group jobs** hand roles to officers. An officer's state machine runs his role: a checker is in
  Searching, walking to the source; a follower is in Searching too, trailing him.

## The pieces, in build order

### 1. The pie-shaped view

Officers see in a rounded pie in front of them, plus a small circle all round, instead of a
big circle. *(User: "Officers still have a small circle of aggro range around them this way, so
Ruby can't sit directly behind them.")*

- **The cone:** View Angle wide (default 100°, centred on the body's facing, `transform.forward`)
  and View Range long (the current Alert Radius, 12.5 m), with a rounded far edge (every point at
  View Range from him).
- **The close circle:** Close Radius (default 2 m, *my call on the size*) all round him. Inside
  it he notices Ruby whichever way he faces. She can sneak up from behind, but she can't sit right
  behind him.
- Both still need the ray that crosses nothing solid except characters, so walls and crates still
  hide Ruby, as today. A wall between them hides her inside the close circle too.
- **The cone is for noticing.** It gates going from unaware to aware: Idle, Patrol and Searching
  to Aggro. Once in Aggro, contact keeps today's all-round line-of-sight test within Give Up
  Radius. He knows she's there and keeps turning to her, so the cone doesn't make a fight
  flicker as he runs along the chase field facing away from her. *(My call.)*
- **Idle officers aggro on sight like everyone else** *(user)*. This piece adds Idle → Aggro on
  `DetectsPlayer` to `BuildTransitions`, ending today's rule that they hold their post until
  hurt. Their activities come in piece 7.
- **Bodies, loose guns and props** (piece 3) are noticed with the same cone and close circle.
  Fast-moving objects and sounds aren't: they're all round (see piece 3).
- **Gizmo:** the cone and the close circle drawn flat at eye height when the officer is selected,
  green when Ruby's in them and in sight, red when she's in them but blocked, grey otherwise. It
  replaces the Alert Radius sphere. There's no in-game cone: top-down, the officer's facing shows
  where he looks.
- `OnValidate` keeps Give Up Radius outside View Range, as it keeps it outside Alert Radius today.

### 2. Knowledge: each officer's memory and the shared pool

**His memory** (private):

- **Ruby's face.** Once he has seen her, he never forgets it. *(User.)* The effect *(my
  reading)*: he never goes back to Patrol Normal, only to Aggressive, and any disturbance he checks
  is checked as Searching for Attack, because he knows she's in the building.
- **His last sighting:** where and when he last saw her himself.
- **What he's sensing now:** the stimuli of the last few seconds, highest priority first, so he
  can act on the most important and ignore lesser ones.
- **The bodies he knows about,** so a corpse he has already reacted to doesn't spook him again.

**The shared pool** (public, one per scene, read by every officer):

- **Ruby:** whether she's been seen at all, her last reported position, when, and by whom.
- **Bodies found:** each corpse once it has been reported, and where.
- **Disturbances:** each one reported (kind, place, where it came from, time, priority), and
  which group job, if any, is handling it.
- **Group jobs:** each open job, its members and its step, so a second officer doesn't start a
  duplicate job on the same thing and each job's cap can be counted.

**How it updates:**

- Officers write **reports** whenever they sense something worth sharing or change what they're
  doing. Reports wait in an inbox.
- Every **Intel Interval** (default 1 s, *my call*) the pool takes the inbox, merges it, and
  publishes a new snapshot. Officers only read the published snapshot.
- So news takes up to one interval to spread, and Ruby's position in the pool is wherever an
  officer last *reported* seeing her. That staleness is the point.
- **Group jobs are the exception.** The list of open jobs is live, not published on the interval,
  because it's coordination rather than knowledge. Two officers hearing the same gunshot in the
  same frame must find each other's job, not start two.

**Speech** *(user: "Officers can speak to each other across the room")*. Alongside the pool,
officers talk:

- What one officer says reaches every officer within **Speech Range** at once (default 15 m,
  measured along the PathGrid like sound, so it carries across a room and through a doorway but
  not through a wall; *my call on the range*). Once rooms exist (piece 7), everyone in the same
  room hears too.
- An officer speaks when he sees Ruby (where she is), finds a body, starts or ends a group job,
  and recruits ("you two, with me").
- Hearers update their own memory at once, as if they'd seen it. So officers in the room with a
  fight know where Ruby is, while officers elsewhere only have the pool's last update.
- He also reports it to the pool, which spreads it to everyone at the next update.

**What the pool changes on its own:**

- **The first aggro puts the level on alert.** *(Removed for now, user, 2026-10-30: only what an
  officer has himself seen or been after makes him aggressive. To be explored later; see Decisions.)*
  Once Ruby aggros any officer, every officer on
  patrol switches to Patrol Aggressive at the next pool update *(user: "Once Ruby aggros one
  officer, all officers are patrol cautioused")*. It doesn't pull anyone toward her. They walk
  their Aggressive routes, wary, and the level never goes back to Normal *(my reading)*.
- Otherwise, the pool doesn't change anyone's state on its own *(my proposal)*. It informs his
  decision when something happens to him: whether a disturbance is already being handled, whether
  Ruby was reported near here (which makes a thud worth Searching for Attack instead of Problem),
  and where to search when he's sent to look for her. That's the line between a squad that's
  informed and one that's all-knowing.

**Aggro chases what he knows, not where she is** *(my reading of the user's goal: "Ruby can use
stealth to her advantage to drop aggro")*:

- Today an officer in Aggro who can't see Ruby chases along a field toward her real position, and
  his search starts from wherever she really is when he gives up. That tracks her through walls,
  so breaking sight barely matters.
- Instead, out of sight, he runs to the newest thing he knows: his own last sighting, what he heard
  said, or the pool's spot. When he gets there and doesn't see her, or after Aggro Time, he searches
  from there.
- An officer sent into Aggro without seeing her (hurt, getting up from a fall, recruited) starts
  from the same newest knowledge.
- The shared chase field becomes a field toward a point, not toward Ruby's Transform. Officers
  heading for the same point share it.

**Idle officers** sense, speak and report like anyone *(user: they "will aggro just like everyone
else")*.

**Debugging:** with the pool's GameObject selected, a gizmo draws Ruby's last reported spot, the
bodies found, open disturbances and lines from each job's lead to its members. `DebugLog.Info`
logs each report and each published update.

### 3. Senses and what they weigh

Each sense makes stimuli. An officer acts on the highest-priority stimulus he has. A new one
interrupts what he's doing only if it outranks it, which is what stops flicker. *(The ranking is
the user's; the table layout and the lower rows are my reading of the notes.)*

**This table is data, not code.** It's the level's default **behaviour map** (piece 7): an asset
holding each kind of stimulus's priority and response. Rooms and floors can override rows, so a
firing range can make gunfire normal *(user: "unique behaviour maps per room or floor")*. The
senses, the pool and the jobs read the map that applies where the stimulus came from. They never
switch on the kind themselves.

| Stimulus | Sensed by | Priority | Sends him to |
| --- | --- | --- | --- |
| Ruby in sight | the cone or the close circle | top | Aggro |
| A body he didn't know about | the cone, within Body Notice Range (default 6 m), or the close circle | highest *(user)* | Searching for Attack |
| A fast object: at or above Alarm Speed | all round, within Motion Radius, with a clear line | highest *(user)* | Searching for Attack, at where it came from |
| A gunshot | hearing | high *(user)* | Searching for Attack, at the shot |
| An alarm ringing *(piece 7)* | hearing | high, below gunshots | Switch it off *(user)* |
| Any other sound (a crash, a thud, a body hitting a wall) | hearing | high, below gunshots *(user)* | Searching for Problem |
| A moving object: at or above Notice Speed but below Alarm Speed | all round, within Motion Radius, with a clear line | medium *(my call)* | Searching for Problem, at where it came from |
| A ripped-out light or a broken prop *(piece 9, much later)* | the cone, or being in the room | medium *(my call)* | Searching for Problem |
| A loose gun, outside an armory room | the cone | medium *(my call)* | Searching for Problem (unarmed, he goes to Rearm instead, as today) |

A stimulus that's already known (a body in the pool, a disturbance an open job is handling)
doesn't start a new job. If the job is recruiting and he's in Speech Range, he joins it. If it's
full, a body or a fast object sends him to Patrol Aggressive, and a Problem-level stimulus is left
to the job. He never just stands there *(my call)*. Gunfire is different: there's always room in
a fight.

**Gunfire:**

- Every gun counts: Ruby's revolver and officers' pistols alike *(my call)*.
- A run of shots is **one disturbance**: a shot within 10 m and 5 s of an open gunfire disturbance
  joins it rather than making a new one. So a firefight is one thing to respond to, not one per
  shot *(my call)*.
- Officers who hear it join the fight nearest them, in its main squad if it has room and its
  standby squad otherwise *(user; see piece 6)*. Until they see Ruby, they head for what the fight
  knows about where she is.

**Fast and moving objects.**

- **Speeds:** Notice Speed default 4 m/s; Alarm Speed default 15 m/s. Motion Radius default 8 m.
  A thrown crate leaves at about 90 m/s and a table knocked by a body much slower, so the two
  bands separate a throw from its knock-on. *(My calls; tune in play.)*
- **What counts:** any non-kinematic Rigidbody, ragdoll bones counted once per character.
  Characters on their feet don't count (Ruby's walk is 15 m/s, and she's sensed by sight). Ruby's
  bullets are heard, not seen.
- **Where it came from** *(user: "This is important")*. Every fast object carries an **origin**:
  the place the chain of motion started. A body thrown by Ruby gets the release point, near her.
  Anything that body knocks into while it's fast inherits the same origin, so a table set moving
  by a thrown body leads back to Ruby, not to where the body landed. An object moving with no
  origin (one nobody set off, or set off long ago) takes the spot where the officer first saw it
  moving. The origin fades after Origin Lifetime (default 5 s). *(My design for the user's rule.)*
  **Only what he sees moving leads back to the origin (user, 2026-11-04).** A sound sends him to
  where it happened: "If an enemy hears an impact, they should go check the source of the sound
  not the person who threw it. The only time they should go to the person who threw it, is if
  they're reacting to a fast or moving object from a direction."

**Hearing.**

- A sound has a place, a kind and a loudness (the distance it carries). Defaults *(my calls)*:
  gunshot 25 m; a body or a heavy prop hitting something above Notice Speed 10 m, growing with
  impact speed; the whip landing 6 m.
- **It travels through the level, not through walls:** the distance is measured along the floor's
  PathGrid (a flood from the source, bounded by the loudness), so a sound carries round corners and
  through doors but not through a wall into the next room. *(From v2's plan.)*
- **Who makes sounds:** Ruby's revolver (`GunController`), officers' pistols (`Gun`), heavy impacts
  of fast bodies (from the same origin tracking), and the whip landing (`GrappleController`).
  Systems emit through a small bus in `Ruby.Core`, so Weapons and Grapple can make noise without
  knowing officers exist.

**Bodies.** A corpse is noticed in the cone within Body Notice Range, or in the close circle when
an officer walks into it *(user: "officers react to dead officers they walk into")*.

- A body nobody knew about: Searching for Attack, and he reports it.
- A body the pool knows about, that he hasn't seen himself: he stops, looks at it for a moment
  (Look Time), and goes to Patrol Aggressive. No new job. *(My call: he mustn't walk past a body
  without reacting, but a known body mustn't start a second search either.)*
- A body he has seen himself: nothing, unless it flies past him fast, and then it's a fast
  object.

**Broken props and lights** come much later, together, in piece 9. Until then there's nothing
broken for an officer to notice, and a crate knocked about only counts while it's moving fast.

**Loose guns.** A gun that's loose and at rest (`Gun.IsResting`, not held) in the cone. An
**armory** is a room whose behaviour map ignores loose guns (piece 7), so they're normal there
*(user)*. Until rooms exist, every loose gun counts. Unarmed officers still go for free guns first
(Rearm, unchanged).

**Cost.** The cone runs a cheap angle and distance test before any ray, and casts one only for
what's inside it. Motion and body checks run a few times a second per officer, staggered, not
every frame. A sound floods once, bounded by its loudness, and every officer reads the same flood.

### 4. Smaller states inside the main ones

The main states stay as they are: Idle, Patrol, Searching, Aggro and Rearm, with Grabbed, Ragdoll
and Dead as overrides. Each main state is entered with a **smaller state** that sets how it
behaves, and the table in `BuildTransitions` picks it.

**Patrol** has two modes. *Aggressive* is the user's new name for what earlier drafts called
Cautious.

- **Normal:** today's patrol, on the Safe route.
- **Aggressive:** walks the officer's **Aggressive route**: a second route on the officer, edited
  with the same Scene-view handles, "the elevation of the normal patrol or idle" *(user)*. Any
  officer can have one, patrollers and idle officers alike. Optionally a little faster, pausing to
  look around at each point.
  - Entered once Ruby has aggroed any officer (the level's alert, piece 2), after Searching for
    Attack ends, after a body has been found, or when he's been sent to patrol because a search
    team was already full *(user)*.
  - **No Aggressive route:** a patroller walks his Safe route (the user's rule: "patrol cautious
    officers walk their normal route"), and an idle officer stays at his post, alert. These are
    rules, not fallbacks, and the Inspector says which applies to an officer without an
    Aggressive route.
  - **Alone or in a group:** officers on Aggressive patrol also walk together behind a leader and
    break off to search (the user's "Patrol Plus"). The rules are piece 5's **Patrol together**.

**Searching**, entered for one of two reasons, alone or with company:

- **Searching for Problem:** something odd. The steps: go to the source, look, and stand down
  with a gesture after about 1.5 s if he finds nothing, back to the patrol he was on (Normal stays
  Normal) *(user)*. If he finds something, he escalates (see the table above).
- **Searching for Attack:** Ruby is, or may be, here. Entered from a gunshot, a body, a fast
  object, or from Aggro when he loses her. It searches the area for Search Duration, and when that
  runs out he goes to Patrol Aggressive *(user)*.
- **Plus:** the same search done as a group job (piece 5). "Plus" isn't a separate state, just a
  Searching officer who's a member of a job.
- When he searches from Aggro, he starts from the newest thing he knows about where she is
  (piece 2), not from where she really is.

**Aggro:**

- **Normal:** today's Aggro, except that out of sight he chases what he knows (piece 2).
- **Word of mouth** *(user: "An officer in Aggro recruits all nearby officers regardless of
  state")*: an officer in Aggro tells the officers in Speech Range where Ruby is, and they join his
  fight, whatever they were doing (but not Grabbed, Ragdoll or Dead). The fight's squads (piece 6)
  decide who fights and who stands by, so the cap is the main squad's four, not a count of who was
  told. Ruby can still aggro any officer herself by being seen.
- **Main squad** and **standby squad** are Aggro's roles in a fight (piece 6).
- **No Calculated Aggro.** The user's "Calculated Aggro" was the whole squad fighting smart, which
  is piece 6, so it isn't a state of its own *(user: "Calculated aggro was really just the whole
  squad shooting")*. Its two triggers are covered elsewhere. With Ruby down, nobody shoots a body
  (today's rule). With Ruby hiding, out of sight, officers chase what they know (piece 2) and
  then search.

**Idle** *(user: "Idle officers will be doing unique actions: cleaning guns, watching TV, sitting
at desks, and will aggro just like everyone else")*:

- An idle officer is at his **post**, doing its **activity** (piece 7's activity spots), or
  standing there if his post has none.
- He senses, speaks, reports, joins jobs and aggros on sight like everyone else. That ends
  today's rule that idle officers hold their post until hurt (the aggro on sight comes in piece 1).
- After a job that pulled him away, he goes back to his post. After the alert, he walks his
  Aggressive route if he has one, and is back at his post, alert, if not.

The v2 plan's separate `CautiousState` and its permanent `IsCautious` flag are replaced by Patrol's
Aggressive mode and the officer's memory of Ruby's face.

### 5. Group jobs

A group job is a plain object (not a MonoBehaviour) owned by the shared pool. It has members, a
step, and a role for each member. Members' state machines run their roles. The job never moves
anyone itself. Each kind of job sets its own cap: three for checking and searching (the lead
included) *(user)*, one for switching off an alarm (piece 7), and a fight's main squad of four
with an unlimited standby squad (piece 6).

**Starting one.** The officer who senses something decides first:

1. Is a job already handling this? If it's recruiting and he's near, he joins. Otherwise he leaves
   it alone.
2. If not, he starts one and becomes its lead. He calls in the free officers within Speech Range,
   nearest first, up to three in all. Free means on patrol or searching for something lower
   priority, not in Aggro or on another job. *(User: "Officer 1 alerts Officer 2 and 3 if
   they're nearby (proximity check).")*

**Check together** (Searching for Problem, or for Attack from a stimulus) *(user's flow)*:

1. **Gather:** members turn toward the source.
2. **Send one:** the member nearest the source is the **checker**. He walks to the source (for a
   fast object, where it came from). The others **follow slowly behind** (Follow Distance default
   3 m, at a slower speed), and hold back when he arrives.
3. **Look:** he looks around the source for Look Time.
4. **Report:**
   - Nothing: he gestures, and after about 1.5 s everyone goes back to their patrol.
   - Ruby: the job becomes a fight (piece 6). Its members are the main squad, and anyone his
     shout reaches joins as in any fight (word of mouth, piece 4).
   - A body: the job becomes **search together**, for Attack.
   - A loose gun outside an armory, or (from piece 9) broken objects or a ripped-out light: the
     job becomes **search together**, for Problem *(my call: the user's notes say "Searching
     Plus" without a reason)*.

**A new sound during a job** *(user, 2026-11-03; not yet built)*: the job sends one member to
its source to check it out, and the rest carry on. He returns to the job only once his check is
complete. If the job ends before he makes it back, he goes back to his patrol route. Recorded
with its open questions as decision 8 of `docs/plans/2026-11-02-enemy-group-jobs-part-2.md`.

**Gunshots always mobilise** *(user, 2026-11-03; not yet built)*: officers always go to the sound
of a new gunshot, even mid-search or as a search ends. An officer on a job first signals to its
other members, then they mobilise toward it. Decision 9 of the same plan.

**A grabbed, downed or dead enemy leads to the chain's source** *(user, 2026-11-03; not yet
built)*: an enemy who sees one goes to where the chain came from, Ruby. Decision 10 of the same
plan, which records what happens today and the open questions.

**A job's search clock waits for its team** *(user, 2026-11-03; not yet built)*: the countdown
starts once every member has reached the area, and a member who can't reach it leaves the job and
goes back to his patrol. **A gunshot extends the search** by 4 to 5 seconds. Decisions 12 and 13 of
the same plan.

**A job mate's death sends the others to Aggro** *(user, 2026-11-03; not yet built)*: when a member
of a job dies, the others go to Aggro on Ruby at once. Decision 14 of the same plan.

**Search together** (Searching for Attack or Problem, Plus):

- **A search team is at most three,** and can be two or just one *(user)*. That holds for every
  search, however big the fight before it was.
- From Aggro: when officers lose Ruby, the ones who lost her count each other. Up to three nearest
  where the hunt was leading form the team. Any others go to Patrol Aggressive *(user)*.
- The team searches the area: the room that place is in, by clearing its hiding spots (piece 8),
  or the open ground around it if it isn't in a room.
- It runs for Search Duration, then everyone goes to Patrol Aggressive (Attack) or back to their
  patrol (Problem).

**Patrol together** (Patrol Aggressive, the user's "Patrol Plus") *(user: "officers on patrol
walking with each other and breaking off")*:

- **Leaders** are set in the Inspector, with a Leader tick on the officer, so the same officers
  lead every time the map is played *(user)*. A leader walks his own route and leads a group along
  it. He never breaks off *(user)*.
- **Groups** are two or three officers, the leader included. At most **two groups** walk the map
  at a time *(user)*. With more leaders than that, the rest walk their own routes alone until a
  group's place frees up *(my call)*.
- **When they form** *(my reading; the level alert it relied on was removed on 2026-10-30, so
  the trigger is open and part B must pick one)*: once the level is on alert, leaders
  go to Patrol Aggressive and gather followers. Before the alert, everyone walks Normal, alone.
- **Joining:** an officer walking his Aggressive patrol alone checks for a group near him (in Speech Range) with
  room, and joins it. A follower walks the leader's route behind him, keeping Squad Spacing, and
  not his own route *(user: "patrol routes that are not leaders")*.
- **Breaking off** *(user)*: a follower breaks off to search for Ruby in an area or a nearby room
  (Searching for Attack, alone and brief). When he's done, he rejoins:
  1. his own group, if it's close;
  2. otherwise any group close enough with room;
  3. otherwise his own Aggressive route, alone.
  Then the check begins again, so a group can pick him up later.
- **When a follower breaks off:** every whole in-game minute *(user)*, so every 60 s of game time
  (paused time doesn't count). One follower per group each minute, the one who has gone longest
  without breaking off *(my reading)*. He searches the room nearest him, or the area around him if
  he isn't near one.

**Gestures** are stand-ins until clips exist: the officer stops, faces the members, holds for the
gesture time, and logs it with `DebugLog.Info`.

### 6. Fighting together: squads and fire lanes

*(User: "Officers not in the main group... stay in the back as backup. Ready to engage Ruby if any
member of the first squad goes down... The main group can grow to a max of 4 only. Enemies fill
the group they're closest to... The standby group can be as large as possible. The standby squad
can shoot their weapons if in range, and if they have line of sight." And: "Enemies should also
respect each others fire paths, and standing spots. Enemies should get in formations to shoot, or
move out of each others way before shooting.")*

This piece is also what the user first called Calculated Aggro: *"The squad system is simply just
officers being smart about their positioning, their space between each other, and their
strategicness in trying to take Ruby down."*

**A fight is a group job.** It starts when an officer goes to Aggro away from any open fight, and
it has two squads:

- **The main squad,** up to four *(user)*. It fights as Aggro does today: it closes to the gun's
  band, holds, backs away inside it, and fires.
- **The standby squad,** everyone else in the fight, with no limit *(user)*. Its officers stay at
  the back as backup: farther from Ruby than the main squad and out of its lines of fire. They
  fire when Ruby is in range, in sight and their lane is clear *(user)*.
- **Joining:** an officer joining a fight (he heard it, was told, or saw Ruby) joins the fight
  nearest him. He takes a main squad place if one is open, and goes on standby otherwise *(user:
  "Enemies fill the group they're closest to")*.
- **Moving up:** when a main squad officer is grabbed, knocked down (Ragdoll) or killed, one
  standby officer moves up into his place at once *(user)*. Which one: the standby officer
  nearest Ruby *(my call)*. A main squad officer who gets back up after a fall rejoins on standby
  if his place has been filled *(my call)*.
- **Losing her:** when every member has been out of contact for Aggro Time, the fight ends and
  the search rule takes over (piece 5): the officers nearest where the hunt led search together,
  and the rest go to Patrol Aggressive. The team is at most three, so not the whole main squad
  *(user)*.

**Firing spots, not drawn formations.** Shooters such as Killzone and Uncharted don't draw
formations. Each enemy scores the spots near him and takes the best free one, and the formation
comes out of that. Officers do the same:

- **Candidates:** open PathGrid cells around Ruby. For the main squad, they're within the gun's
  band. For standby, they're in a ring farther back (default 3 m beyond the main squad's
  distance, *my call*).
- **Scored** as a weighted sum *(the weights are my calls, tuned in play)*:
  - a clear shot at Ruby from the spot;
  - not standing in a squadmate's lane (the line from him to Ruby);
  - at least Squad Spacing (default 2 m, the figure Uncharted 2 used) from every squadmate's spot;
  - close to where he stands, so he doesn't run laps to reach it;
  - inside the gun's preferred range;
  - spread round her: a spot at a different angle from his squadmates' scores higher, so Ruby is
    caught in a crossfire rather than facing one firing line *(my call, for the user's "their
    strategicness in trying to take Ruby down")*.
- **Claimed:** one officer per spot, and claims keep Squad Spacing apart, so two officers never
  stand on the same spot *(user: "respect each other's standing spots")*. The main squad picks
  first, and standby picks from what's left.
- **Re-picked** about once a second, or at once when Ruby moves well away or his lane closes. Not
  every frame, so officers don't shuffle.
- Unarmed officers have no lane to keep and chase as today. Out of sight, every officer chases
  what he knows (piece 2).

**Fire lanes** *(user: "respect each others fire paths")*:

- **Before firing,** he checks his lane: a capsule as wide as a body, from his muzzle to Ruby, with
  squadmates moved a moment ahead along their velocity. If a squadmate is in it, he holds fire.
- **Held,** he steps aside to the nearest spot with a clear lane, or waits a beat if the squadmate
  is already leaving it (*user: "move out of each others way before shooting"*).
- **Walking,** he avoids crossing a squadmate's lane: paths cost more through it, as in Killzone's
  pathfinding, so he goes round behind.
- **Bullets still pass through free officers** (unchanged: there's no friendly fire). The lanes
  make the squad read as disciplined. They don't protect anyone.

**Callouts:** a squad's moves are said out loud ("moving up", "hold"). F.E.A.R.'s soldiers called
out what they'd just decided to do, which made their coordination readable. Until barks exist,
callouts are `DebugLog.Info` lines and a gesture where one fits.

### 7. Rooms and behaviour maps: activities, firing ranges, armories and alarms

*(User: "Unique behaviour maps per room or floor. If officers are already shooting in a firing
range, more shooting shouldn't alert officers and shooting from a radius around that room
shouldn't trigger officers. If an alarm goes off, officers should be able to turn it off, but
after multiple times, they get suspicious.")* This is Hitman's behaviour maps, which tune
reactions area by area. A bar crowd shrugs off a fight that would panic a fashion show.

**Rooms** are placed in the scene: one or more rectangles (`GridArea`) with a name and a behaviour
map.

- A room can be as big as a floor, so a map "per floor" is a room covering the floor.
- Rooms can sit inside bigger rooms, and the smallest room holding a point is the one that counts
  there. A point in no room uses the level's default map.
- Rooms also tell searches "the area Ruby moved to" (piece 8), and widen speech to the whole room
  (piece 2).
- Two rooms that overlap without one holding the other make a point's map ambiguous. That's
  warned about when the scene loads, never silently resolved.

**Behaviour maps** are assets (ScriptableObjects). For each kind of stimulus, a map says what it
means here: its priority, its response (ignore, Searching for Problem, Searching for Attack, switch
off), and when that applies (always, or only while the room is in use).

- The level's default map is piece 3's table. A room's map overrides only the rows it sets, and
  the rest come from the default.
- A map can set a **Mask Radius**: what it ignores is also ignored when it comes from within that
  distance outside the room.
- **Sight is never masked.** An officer who sees Ruby, or a body, reacts wherever he is.

**Armories:** a map that ignores loose guns *(user)*.

**Activity spots** *(user: idle officers are "cleaning guns, watching TV, sitting at desks")*:

- An activity spot is a placed object: where to stand or sit, which way to face, and an activity
  (Clean Gun, Watch TV, Sit At Desk, Shoot At Range, and more later). Shoot At Range also has a
  target.
- An idle officer's **post** can be an activity spot, set in the Inspector. He does its activity
  while he's there. A patrol point can sit on one too, and an officer pausing there does it for
  the pause.
- He faces the spot's way, so his cone looks where the activity does: at the TV, at the desk. That
  is what makes sneaking past a busy officer possible. The activity doesn't change his senses
  otherwise *(my call)*.
- He aggros, joins jobs and reacts as usual. A stimulus ends the activity at once.
- Each activity is a slot in the officer's `CharacterAnimationSet`, so a clip is an edit to the set
  and a rebuild, never code. Until a slot has a clip, he plays the Rest idle there, and a warning
  names the empty slot once when play starts. That's a visible stand-in, not a silent one.
- How a patrol point or a post is tied to a spot is the builder's call, with one constraint:
  `PatrolRoute` lives in `Ruby.Pathing`, which mustn't learn about activities.

**Firing ranges** *(user)*:

- A range is a room with Shoot At Range spots. An officer at one fires at its target.
- The range is **in use** while anyone is shooting there, and for In Use Time (default 10 s, *my
  call*) after the last shot.
- While it's in use, its map ignores gunshots from inside it and from within its Mask Radius
  (default 8 m, *my call*). So Ruby can fire near a busy range without alerting anyone who doesn't
  see her. When nobody is practising, a shot there is a shot.

**Alarms** *(user)*:

- An alarm is a placed object. Set off, it rings (a loud sound of its own kind) until someone
  switches it off.
- Ruby sets it off by hitting it: a bullet, something thrown at Notice Speed or faster, or the
  whip yanking it *(my call)*. That makes it a lure.
- **Switch off** is its default response. The nearest free officer goes to it, switches it off (a
  stand-in pause of Switch Off Time beside it), and goes back to what he was doing. Others who hear
  it leave it to him, because it's a job with one member.
- **Suspicion:** each alarm counts how often it has gone off. From the Suspicious After count
  (default 3) on, the officer who switches it off is suspicious. He calls in whoever is near, and
  they search around it, Searching for Problem with company *(user: "after multiple times, they get
  suspicious"; the count and the response are my calls)*. The count never resets.

**Editor:** each room is drawn as an outline labelled with its name and map, with its Mask Radius.
Activity spots are drawn with their facing and activity name (and a line to a range target), each
idle officer has a line to his post, and in play each alarm shows its count.

### 8. Clearing hiding spots

- **The area Ruby moved to** is the room (piece 7) holding the spot the search starts from *(user:
  "if Ruby ended in a room, officers will check the whole room")*. With no room there, the team
  searches the open ground around it.
- **Hiding spots** are a list on each room: places where Ruby could be out of sight from the
  room's open floor, behind sofas, desks, tables and crates *(user)*. A **Find Hiding Spots** button in the room's
  Inspector fills the list from the PathGrid: open cells beside something that blocks sight that
  can't be seen from the room's middle. The designer then adds, moves or deletes spots with
  Scene-view handles. *(My call: generated, then hand-edited.)*
- **Clearing:** one officer, the **pointman**, goes in at the lead's gesture and checks the spots
  one by one, nearest next, by walking to where he can see each one. The others **hang back**
  inside the room's entrance, covering it *(user: "sending in one officer with a gesture while
  other officers sit back")*. A spot is cleared once he has seen it.
- Every officer on the team still senses as usual, so Ruby breaking from a spot in a cover man's
  cone is seen.
- **Moved up (user, 2026-11-06):** the part that needs no rooms comes before piece 6: slow walking,
  scanning as they go rather than stopping to turn, and going to places they can't currently see.
  It is decision 16 in `docs/plans/2026-11-02-enemy-group-jobs-part-2.md`. The pointman, the cover
  men, room-by-room clearing and designer-edited spots stay here.

### 9. Breakables: light fixtures and props (much later)

*(User: "Lights and props are together. Lights mean light fixtures. Lights are going to get ripped
off the ceiling, and officers will react to rooms that have their lights ripped off or props
broken in the area (lamps, tables, chairs). That will be much later.")*

- **Light fixtures** can be ripped off the ceiling, and **props** (lamps, tables, chairs) can be
  broken. Neither exists yet: the scene has one light, and no prop can break.
- A room with a light ripped out, or broken props in it, is a stimulus (Searching for Problem) for
  officers who see it or are in it. It's also what a checker can find (piece 5).
- Designed in full at its check-in. Open until then: how a fixture is ripped down (the whip is the
  obvious verb), what breaks a prop, and whether a dark room shortens officers' sight.

### 10. Pathing: corners and curves

*(User: "avoid corners of walls and have more realistic walk pathing by smoothing it and curving
it. Hitman has this.")*

What the player sees today: paths are pulled tight, so officers pass wall corners with their body
touching them and turn sharply at each bend.

- **Corner margin:** officers keep Corner Margin (default 0.5 m) clear of walls and corners
  wherever there's room. In a gap too narrow for it, they keep to the gap's middle.
- **Curves:** an officer starts turning before a bend and comes out of it on the new heading. At
  speed, a sharp corner becomes a short arc, never a pivot on the spot. His facing follows his
  curved path.
- It applies to every walk: patrol routes, searches, and the shared chase field.
- It must not cut into blocked cells, slow a chase noticeably, or break arrival and stuck
  detection.
- Aggro's own facing (facing Ruby while holding or backing away) is unchanged.
- **Method** *(builder's call; a sketch)*: a clearance field from the bake (each walkable cell's
  distance to the nearest blocked one); a cost for cells near walls in A* and the floods; string
  pulling that keeps the margin; and steering toward a point a short way ahead along the path,
  with a limit on how fast he turns. It lives in `Ruby.Pathing`, which stays game-agnostic.

## Code shape

- **`Ruby.Core`** gains the stimulus bus (`Stimuli.Emit(...)` with a kind, place, origin and
  loudness) and origin tracking for bodies set moving. Weapons, Grapple and Core emit into it;
  nothing in Core knows about officers. The kinds are an enum with explicit numbers, because
  behaviour map assets save them, and a new kind takes a new number.
- **Behaviour maps** are ScriptableObject assets in `Ruby.Game`, like `WeightTable` and
  `ImpactRules` in Core: one default for the level, and one per room that needs its own.
- **`Ruby.Game`** holds the senses, memory, shared pool, group jobs, rooms and alarms, in a new folder
  `Assets/Scripts/Squad/` (covered by `Ruby.Game.asmdef`, since it has no asmdef of its own), and
  the state changes in `States/`. CLAUDE.md's assembly table is updated to say so. They could be
  lifted into their own assembly later, behind interfaces, if another game wants them. They don't
  need to be now.
- **`Ruby.Pathing`** gets piece 10, and the extra path cost for crossing a squadmate's line of
  fire (piece 6) as a game-agnostic cost hook.
- **The state machine** stays as it is. A main state may run a small `StateMachine` of its own for
  its steps (walk to source → look → gesture) where there are more than two. The smaller state
  (Normal/Aggressive, Problem/Attack) is a mode it's entered with.
- **Scene-placed things** (rooms, activity spots, alarms, hiding spots, Aggressive routes) use Scene-view handles like patrol routes
  do. The user does the Inspector and scene work. No hand-edited `.unity` or `.prefab` YAML.
- Conventions as CLAUDE.md: no namespaces, serialized private fields with read properties and
  tooltips, tuning on the controller rather than the state, comments that say why, gizmos for
  anything spatial, `DebugLog.Info` for chatter, warnings for real problems.

## Decisions

- **State machine, not behaviour trees** (user). Hitman uses trees, but no crowds means none are
  needed, and the main states plus smaller states map onto the machine directly.
- **A shared pool that updates periodically** (user) carries news across the level, so it's stale
  by up to one update. **Speech across a room** (user) carries it at once to the officers in
  earshot. Officers in the room with Ruby know; officers elsewhere know what the pool last said.
- **The first aggro put every patrolling officer on Aggressive** (user), without pulling anyone
  toward Ruby. **Removed for now (user, 2026-10-30):** only an officer who has seen Ruby or been
  after her goes aggressive, and a level-wide alert is to be explored later. Part B of piece 5
  (patrol together) was written to start groups "once the level is on alert", so it needs its
  own trigger when it is planned.
- **Aggro chases what he knows,** not her real position, so breaking sight drops a fight. *(My
  reading of the user's goal; see piece 2.)*
- **The open-jobs list is live,** not stale, so two officers never start the same job. *(My
  call.)*
- **The cone is for noticing, not for keeping contact.** *(My call; see piece 1.)*
- **A fight has a main squad of four and an unlimited standby squad** that fills its gaps (user).
- **Formations come from scored, claimed firing spots** and a lane check before every shot, not
  from drawn shapes. This is how Killzone and Uncharted do it. *(My call.)*
- **Origins travel along the chain of motion,** so a knock-on leads back to the thrower. *(My
  design for the user's rule.)*
- **Sound travels along the PathGrid,** not straight through walls. *(From v2's plan.)*
- **Reactions are data: behaviour maps per room or floor** (user), so a scene can make gunfire
  normal on a firing range or loose guns normal in an armory without code. A floor is a room
  covering the floor. *(That last part is my call.)*
- **Alarms are lures with a memory.** Officers switch one off, until it has gone off too often.
  *(User; the count of three is my call.)*
- **Aggressive (formerly Cautious) is a mode of Patrol,** not v2's separate `CautiousState`.

## Verification

There are no automated tests. Each piece is verified by `python tools/compile_check.py` ending
`compile-check: OK`, and then a play-test in `SampleScene` that walks the rows of **What the player
sees** that the piece covers. A row is only ticked once the user has played it. Each piece ends
with `CLAUDE.md` and the design manual (`docs/manual/design-manual.html`) brought up to date.

## Progress

- [x] 1. The pie-shaped view
- [x] 2. Knowledge: memory and the shared pool
- [x] 3. Senses and what they weigh
- [x] 4. Smaller states inside the main ones
- [x] 5. Group jobs
  - Part A (check and search together) is built, to `docs/plans/2026-10-30-enemy-group-jobs.md`
    Tasks 1 and 2 and `docs/plans/2026-11-02-enemy-group-jobs-part-2.md`. Part B, **Patrol
    together** (leaders, groups of two or three, followers breaking off and rejoining), is not
    built, and still needs its trigger now the level alert is gone. Decision 16 of the part 2 plan,
    searching like people (slow walking, scanning as they go, going to places in the search area
    he can't see), comes next as a small piece before 6, because it doesn't need rooms. Its
    room-by-room clearing and hiding spots stay in pieces 7 and 8.
- [ ] 6. Fighting together: squads and fire lanes
- [ ] 7. Rooms and behaviour maps: activities, firing ranges, armories and alarms
- [ ] 8. Clearing hiding spots
- [ ] 9. Breakables: light fixtures and props (much later)
- [x] 10. Pathing: corners and curves

2 and 3 are built together, since a pool with nothing feeding it can't be played. 4 is built apart
from its group cases (an officer sent to Patrol Aggressive because a search team was full, and
searching as a team), which wait for piece 5's jobs. 9 comes much later. 10 can go anywhere in the
order.

## Open questions

- **Breakables** (piece 9, much later): how a fixture is ripped down, what breaks a prop, and
  whether a dark room shortens sight. Settled at piece 9's check-in.
- **Aggro drops too easily around crates** *(user, 2026-11-03; after the refactor)*: Ruby
  running round crates breaks line of sight, and 1.5 s of Aggro Time later he gives up. The
  user's tuning is raising Aggro Time on the prefab to about 4 s. If that isn't enough, the
  change is to have him **follow where she was heading** after losing sight, not only run to
  where she was last seen. Recorded as decision 11 of
  `docs/plans/2026-11-02-enemy-group-jobs-part-2.md`.

Everything else is decided. The *(my call)* and *(my reading)* marks show where the decision was
mine rather than the user's.
