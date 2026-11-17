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
whole job, the search growing, and the provoked enemy who sees Ruby only with their own eyes). Patrolling
together (piece 5 part B) comes next, and much later breakables. Piece 8, clearing hiding spots, was removed (user, 2026-11-11).

The design manual (`docs/manual/design-manual.html`, with a PDF beside it) is the designer-facing
account of what is built, and its section on the Path Grid, colliders and navigation is written for
new coders. It deliberately says nothing about enemy work still in progress, such as group jobs
(done, but part of the enemy states work): the user's rule is that the manual waits until all of
enemy states is finished.
