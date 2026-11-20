# Smooth transitions

**Status:** approved 2026-11-20. Roadmap step 3 (`docs/architecture/project-history.md`).

## Intent

The user, 2026-11-19: every change of state should flow into the next, with pause times, end animations and
transition animations, never an instant switch. An officer who aggros "rolls into" the state. Officers should
feel like people, careful and positional (GTA crossed with Hitman). One who snaps reads as a robot, even when the
logic is right.

Today nothing rolls. On the frame an officer sees Ruby they are in Aggro with the gun up, firing as soon as the
gun's cooldown allows. A lost fight turns into a search on the frame it is lost. A noise sends them walking on the
frame it is heard. The only beats are the 1 s stand-down gesture (a held pose, waiting for art), the 1 s get-up
and patrol point waits.

## Goals

- **A reaction window when they spot Ruby** (the user's pick, option A of three): turn and draw for Draw Time
  before they may fire. It is her one chance to act first, as in Hotline Miami. It changes combat difficulty, so
  it is tuned.
- **Four beats, each with its own animation slot** (the user's picks):
  1. **Draw**: spotting Ruby.
  2. **Notice**: hearing something, before they go to check it.
  3. **Lose**: losing Ruby, before they start the search.
  4. **Stand down**: a search ends, before they go back to their patrol or post.
- **Room for art without code.** Slots fill from the `CharacterAnimationSet`, as every clip does.

## Non-goals

- Sizing up crates, mates and Ruby's position on entering a fight. That is step 4 (finding cover). The beat part
  is where it will hang, but this step does not build it.
- New clips. None of the four exist in the project (Kevin Iglesias's pack has locomotion, idles, turns, talk and
  a fall). This step builds the slots and the timing.
- Ruby. She has no beats; the game's movement and controls stay snappy.

## Anti-goals

- **Making the officer worse at the fight once it has started.** No second draw mid-fight, no beat on every
  glimpse.
- **A beat that eats a state's own timers.** Searching keeps its full search time; the search waits behind its
  beat, as it already waits behind a distraction.
- **A silent stand-in.** An empty slot is named, not hidden (below).

## Rules the user decided

- **Length is a tuned time on the officer; the clip is sped up or slowed down to fit it.** Difficulty is tuned
  without touching art, and stand-in clips work right away.
  - Draw Time, about 0.5 s to start.
  - Notice Time, about 0.6 s to start.
  - Lose Time, about 0.5 s to start.
  - Stand Down Time, the existing field (1 s). Its tooltip already says "the gesture, until there is an animation
    for it": it becomes the slot's length.
- **An empty slot: hold and turn.** They turn toward the cause and hold their stance for the tuned time. The
  officer's Inspector (and the animation set's) names each empty slot, so it reads as waiting for art, not broken.
- **Interrupts: overrides and Ruby only.** A hit, grab, knockdown or death cuts any beat. Seeing Ruby during a
  notice, lose or stand-down beat cuts straight into the draw. Nothing else cuts a beat.
- **Feet: they plant, except for the draw.** In the notice, lose and stand-down beats they stop and turn. In the
  draw they keep the momentum they had and slow to a stop inside it, so a walking officer doesn't freeze
  mid-stride. That is why the draw plays on the upper body only, the legs blending to a stop under it.

## Approaches

1. **A shared beat part, `EnemyBeat`** (picked by the user). Each officer owns one, beside `EnemyMovement` and
   `EnemySenses`. A state starts a beat (kind, what to face); the part owns the timer, the turn, the feet and the
   slot; the state waits while it runs. Any change of state cancels it, so the interrupts come free from the
   transition table.
2. **Each state times its own lead-in.** Less new code, but four copies of the turn, the feet and the animation
   would drift apart.
3. **Separate transition states (Draw, Listen…) in the machine.** Rejected: a Draw state must name Aggro, which
   the table's rule forbids (states never name other states); `ProvokedUnseen` drops on any way out of Aggro, so
   drawing would count as leaving the hunt; and the table doubles.

## The design

### `EnemyBeat` (a plain class in `Enemies/`, owned by `EnemyController`, like `EnemyMovement`)

- **Start(kind, point to face)** and **Running**, **Kind**, **Progress** (0 to 1), **Cancel()**.
- **The timer** reads the tuned time from `EnemyController` on start. Tuning lives on the controller, as always.
- **Facing:** while it runs it turns them toward the point at Turn Speed.
- **Feet:** notice, lose and stand-down stop them (`Enemy.Stop()` each step); the draw lets their velocity run
  down instead of zeroing it.
- **Cancelled on every change of state:** `EnemyController` cancels it from `StateMachine.Changed`, the one place
  every change passes, the table's and `SetState`'s alike. A beat therefore never outlives the state that started
  it.
- **Logs** each beat's start and end through `DebugLog.Info`.

### Where each beat starts

- **Draw:** on entering Aggro from Idle, Patrol or Searching, facing Ruby (or, for one provoked and still blind to
  her, the newest place they know of her). While it runs Aggro may not fire and doesn't move them; it still tracks
  her, counts Lose Sight Time and keeps its knowledge as it does now.
- **Notice:** on entering Searching from Idle or Patrol, facing the incident's point, before the first step of
  the search (the walk, or the look from where they stand).
- **Lose:** on entering Searching after a lost fight (`SearchRequest.AfterLostFight`), facing where they last
  saw Ruby, with the gun held on that spot.
- **Stand down:** where the gesture is today, both the lone search's `SearchStep.StandDown` and the job's
  `JobStep.StandDown`: the step's hold becomes the beat.

### Animation

- **Four slots** on `CharacterAnimationSet`: Draw (pistol; upper body under a mask), Notice, Lose, Stand Down.
  Build Controller generates their states.
- **`IAnimatedCharacter`** reports the running beat and its length, so `CharacterAnimator` plays the slot at the
  speed that fills it. Ruby reports none.
- **Empty slot:** nothing plays and the stance holds; the turn still happens. Named in an Inspector note on the set
  and on the officer.

## Calls of mine (the user can correct these)

- **The draw only comes from calm** (Idle, Patrol, Searching). Getting up from a fall (it has its own get-up) and
  coming back from Rearm don't draw. Nor does a fresh glimpse mid-fight: the gun is already up. Nor does seeing
  her during the Lose beat, while the gun is still held on her last spot: they go straight back to the fight.
  Once that beat is over, a search lowers the gun to Rest, so seeing her later in the search draws.
- **Word of mouth and being shot draw too.** An officer told about Ruby, or hit from calm, is coming from calm
  (`Alert`): they draw before they can fire.
- **An unarmed officer still takes the spot beat.** Today Aggro hands an unarmed officer to Rearm on the next frame,
  which would cancel it. The Aggro-to-Rearm row waits until the draw is over (there is no gun to draw; it is the
  flinch and turn).
- **Notice covers every start of a search from calm**, seen as well as heard (a corpse, a body on the chain, a job a
  mate calls them to): the same "stop and take it in" beat. The user's word was "hearing"; nothing else would
  start a search from calm without it.
- **A distraction mid-search plays the Notice slot** for its existing Distraction Time. It is the same moment
  (hearing something, turning to it), and the search already waits behind it.

## Left to the builder

- How `CharacterAnimator` layers the slots (an upper-body layer for the draw, full body for the rest), and the
  parameter that carries the speed.
- The gizmo: a line to what a beat is facing, while it runs.
- Updating `enemy-ai.md`, `enemy-searching-and-jobs.md`, `animation.md` and project history.
- Defaults for the new times: the starting values above, to be tuned in play.

## Verification

- Compile check.
- In play, scripted, with an idle officer:
  - Ruby steps into view: no shot before Draw Time; a shot after it.
  - A thrown crate lands nearby: they stop and face it for Notice Time, then walk over.
  - Ruby steps in during that notice beat: it cuts to the draw.
  - Ruby breaks sight for Lose Sight Time: they hold on her last spot for Lose Time, then search.
  - A shot during a beat: the beat ends and the knock-back or knockdown takes over.
