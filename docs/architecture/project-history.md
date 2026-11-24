# Specs and build history

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

`docs/specs/` holds dated design documents written before implementation; `docs/design/` holds
system UX notes (the grapple). They carry the reasoning behind decisions and record rejected
alternatives. **Read the relevant spec before extending a system.** Their status headers can lag the
code — the camera spec describes a Cinemachine rig that `CameraFocus` replaced, and the chain-grab
foundation spec's enemy sections are superseded by `2026-09-29-enemy-states-v2.md`.

The original slice plan: 0 enemy foundation → 1 camera rig → 2 targeting indicator → 3 chain grab →
4 charge and swing → 5 impact damage. Slices 0–3 are built (the grab as the whip grapple). Slice 4's
charge is built as the charged throw in `GrappleController`, its swing as the parked spin; the old
`PlayerHoldingState` scaffold and its spec (quantised swing directions, a break-out timer) were
retired. Slice 5, impact damage, is built: enemies and Ruby take it, and dead enemies stay as
corpses. Enemy weapons (pistols, drops, re-arming) are built to `2026-10-11-enemy-weapons.md`.
Ruby ragdolls on her own rig, and officers' bullets knock her back, built to
`docs/plans/2026-10-12-ruby-ragdoll.md`. Officers wear Kevin Iglesias's `HumanM_Model`
(`officer.prefab`, the old `Enemy.prefab` renamed, same GUID), and both they and Ruby are animated
through `Ruby.Animation`, built to `2026-10-20-enemy-animations.md`. Officers ragdoll limp on
their skeletons too, and are tied upright on the chain, built to `2026-10-23-enemy-ragdolls.md`.
Melee and Ruby's revolver are still to come.

Enemy States 0.2 (`2026-10-28-enemy-states-0.2.md`) is built piece by piece: pieces 1 (the view
cone), 2 and 3 (knowledge, the pool, senses and the behaviour map, built to
`docs/plans/2026-10-28-enemy-knowledge-and-senses.md`), 4 (smaller states: search steps, Patrol
Aggressive, the idle post, built to `docs/plans/2026-10-30-enemy-smaller-states.md`) and 10
(corners and curves) are done. **Group jobs (piece 5, part A) are done**: part 1 (word of mouth and
the job in the pool, `docs/plans/2026-10-30-enemy-group-jobs.md` Tasks 1 and 2) and part 2
(`docs/plans/2026-11-02-enemy-group-jobs-part-2.md`). A whole-branch review between them found bugs
and overloaded classes, and `docs/plans/2026-11-02-enemy-ai-fixes-and-refactor.md` fixed them and
split the job engine, the knowledge pool, flood reading, sight and reactions out of `Intel` and
`EnemyController` into the classes under **Enemy AI**. Part 2's tasks and its decisions are built
and described under **Enemy AI**, including decision 16, searching like people (slow walking, scanning
as they go, clearing what they can't see). Piece 6 is built as fights and firing spots, to
`docs/plans/2026-11-06-enemy-squads.md`; its main and standby squads and its fire lanes were built and
taken out. The user
changed decisions 10, 14 and 15 (Task 8b): nobody goes to Aggro unless they see Ruby, or see her drag
or throw an enemy on the chain (and, since the distractions plan, a searcher provoked to their limit goes
after her, blind until she is in their view), and one who sees an enemy killed goes to check where it came from; a grabbed enemy seen standing still is walked over to and searched
around, and a knockdown or death only searches. Piece 5's part B (patrol together: leaders, groups,
breaking off) isn't built, and needs a trigger of its own now the level alert is gone. Piece 7 is
built to `docs/plans/2026-11-09-rooms-and-room-maps.md`: rooms, room maps, searching by room and the
firing range. Its other activity spots and alarms wait for assets. Distractions on a search are built
to `docs/plans/2026-11-11-distractions-on-a-search.md` (sending one to check, serious things bringing the
whole job, the search growing, and the provoked enemy who sees Ruby only with their own eyes). Piece 8,
clearing hiding spots, was removed (user, 2026-11-11).

**Patrolling together (piece 5 part B) is shelved** (user, 2026-11-18: "Enemy scope is big enough"). In its
place, enemies track Ruby's path, built to `docs/specs/2026-11-18-enemy-path-tracking.md` and
`docs/plans/2026-11-18-enemy-path-tracking.md`: out of sight they know where she vanished and which way she was
going, hunt a guess at where she went (one bounded flood per fact), and lose her only after one Lose Sight Time
(4 s), which replaced Aggro Time, Sight Follow Through and Unseen Hold Time; a 0.25 s glimpse grace keeps a
flickering line of sight from twitching them, they call out the vanish, and a lost fight's search grows to
where she was heading. The same plan trimmed the enemy's Inspector from 57 fields to 47 (an audit with the
user).

**The enemy AI engine roadmap** (user, 2026-11-19). The rest of the game is done, and the enemies are its heart:
the aim is tactical officers that feel like GTA crossed with Hitman, careful and positional, and that roll into
every change of state rather than snapping. It is built as separate projects, in this order, each designed and
approved on its own:

1. **Prop-aware pathing**, `docs/specs/2026-11-18-prop-aware-pathing.md`: Careful and Forceful route rules (only
   Aggro and Rearm shove, and only props under 20 kg), a skin round props that never closes a lane, a corridor
   cost that doesn't punish narrow corridors, shortcuts checked against real shapes, a snap that can't cross walls.
   **Built** 2026-11-19 from `docs/plans/2026-11-19-prop-aware-pathing.md`; awaiting the user's play-test.
   The first play-test found a corridor half-blocked by crates sealed to the grid though Ruby walked it: the
   centre-only cell test closed gaps up to 0.95 m. Tight cells (pathing.md) now open any gap from about 0.75 m.
   A scripted test then found officers dragging heavy crates by friction; character capsules now carry
   No Friction (movement-and-weight.md). Then pathing was made about 5x cheaper without changing a route
   (`f9029c8..50b4f39`: the gizmo as one texture, lazy floods, cached moves and step costs, connected
   areas). The user's play-test after all of it: routes look clean and the game doesn't lag.
   **Done** 2026-11-19.
2. **Sight through the cone only**: Motion Radius and Body Notice Range go, and every visual notice (corpses,
   loose guns, moving bodies) uses the cone out to View Range. Close Radius stays, for Ruby only (user's call).
   Design: `docs/specs/2026-11-19-sight-through-the-cone.md`. **Built** 2026-11-19; awaiting the user's play-test.
3. **Smooth transitions**: settle and wind-up pauses between states, with room for end and transition animations.
   Design: `docs/specs/2026-11-20-smooth-transitions.md`; plan: `docs/plans/2026-11-20-smooth-transitions.md`.
   Four beats (draw, notice, lose, stand down), each a tuned time with an animation slot scrubbed to fit; the draw
   is Ruby's window before they may fire. **Built** 2026-11-20; scripted Play checks pass; awaiting the user's
   play-test, and clips for the slots.
4. **Finding cover**: spots hidden from Ruby behind crates, tables and wall corners; firing spots use them.
5. **Using cover**: hide, peek, fire blind over cover, edge closer while Ruby stays put.
6. **Squad tactics by headcount**: pairs, threes, fours and the whole floor. Settled so far: a fight ends only
   once Ruby is outside a radius round where she was last seen (Fight Area Radius, about 10 m) and unseen for
   Lose Sight Time; inside it they know she is around but not where, and hunt her heading. A table only covers
   when it stops bullets (on its side; upright it is a thin top on legs). When she takes cover the fight holds
   and keeps firing, and after Cover Time may send one flanker to a spot that sees round it; a lone officer never
   flanks or rushes; a three never rushes; one or two rush when a flanker is downed or grabbed in a bigger fight.
   Open: what a pair does.
7. **Reading Ruby**: her grabbing, holding someone or hiding behind a wall changes their aggression.

Much later, breakables.

**Paused 2026-11-20** after step 3 (user: "We got a lot where we want it"). Steps 4 and 5 (cover) are skipped; if
the work resumes, step 6 was the recommended next. Before the pause, one more change from the user's video of a
chase's end (`docs/design/Aggro-Searching.mp4`, 34–39 s, officers circling one spot "like sharks"): **covering the
ways out**, `docs/specs/2026-11-20-covering-the-ways-out.md` and `docs/plans/2026-11-20-covering-the-ways-out.md`.
Out of sight the fight now floods once from where Ruby vanished, finds the openings out of it, sends the nearest
hunter down each (two at most) and spreads the rest round it facing outward; and a searcher clears the nearest
point by walking distance. **Built** 2026-11-20; scripted Play checks pass; awaiting the user's play-test.

**Enemy States 0.2 closed 2026-11-20** (user: "Lets close out enemy states for now"). What it built is in the spec's
Progress (`docs/specs/2026-10-28-enemy-states-0.2.md`) and the roadmap above. Left for later: patrolling together
(piece 5 part B), activity spots and alarms (piece 7, waiting for models), breakables (piece 9), and roadmap steps
6 and 7 (steps 4 and 5 skipped). The design manual and its PDF now cover all of it, with the officer section
rewritten and the stats table brought up to the prefab's and test scene's current values.

The design manual (`docs/manual/design-manual.html`, with a PDF beside it) is the designer-facing
account of what is built, and its section on the Path Grid, colliders and navigation is written for
new coders. It held back the enemy states work until that closed (2026-11-20), and now covers it.

**Chain feel, from 2026-11-22** (`docs/specs/2026-11-22-chain-feel.md`): the chain leaves Ruby's right hand (step 1);
pull split into a tap's yank, a hold's steady reel and a double tap's spin (step 2), with the spin starting toward
the reticle (a yanked table tipping onto its side, `Tippable`, was built and then removed on 2026-11-24); and **the clothesline** (step 3, built
2026-11-24, `docs/plans/2026-11-24-clothesline.md`): a taut rope swept across an enemy at Knockdown Speed floors
them along the sweep, and the spin carries on. Step 4, the chain acting at the node point, is shelved (2026-11-24). Next on the list: a
play-test of a spin walked through a crowd.
