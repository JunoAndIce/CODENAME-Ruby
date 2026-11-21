# Covering the ways out, and searching by the walk

Status: design, awaiting approval (2026-11-20).

## Why

At the end of a chase, every officer out of sight of Ruby walks to the same point and looks round there. Everyone
told the same call-out gets the same guess (`EnemyAwareness.TryGetGuess`). When the whole floor joins, a dozen
officers pile onto one spot and jostle for personal space. In the user's video (`docs/design/Aggro-Searching.mp4`,
34–39 s) about 14 of them circle a crate "like sharks" while Ruby is up the corridor. People don't do this. Cops
arriving where a suspect vanished **spread out round the area** and cover where she could have gone.

The guess makes it worse. It is a point about 8 m ahead of where they lost her, along the way she was running. In
the video she turned, so the guess landed somewhere she never walked, and every officer trusted it.

Searches have a smaller version of the same problem. A searcher picks the unseen point nearest them **in a straight
line** (`SearchCoverage.TryClaimNearest`). A point through a wall can win that way and still be a long walk. They walk
round the wall to it, then walk back for the easy spot they were standing beside.

## Goals

- Out of sight, the hunters split up. One takes the way she was heading, and the others take the **other ways out**
  of where she vanished: the other doorways, the corridor going the other way, round the far side of the crates.
  When the guess is wrong, someone is still on the route she actually took.
- With more hunters than ways out, the rest spread over the ground round where she vanished, each on a spot of their
  own, instead of stacking up.
- On arrival they look **outward**, down the route they're covering, not back at the middle.
- A searcher clears the nearest unseen point **by walking distance**.

## Non-goals

- In sight and in the glimpse grace, nothing changes: firing spots, closing in and backing away all stay as they are.
- Lose Sight Time, losing the fight, and the search that follows stay as they are. A lost fight still starts its
  search from the heading guess, and the job's cap of three still sends the rest to Patrol Aggressive.
- No new knowledge. Officers still know her path and never her position. A plan is made only from a fact a member
  knows, and only members who know that fact are put on it.
- Intel Interval isn't changed by this work. It's the user's to tune. Note that it also sets how often jobs fill
  and merge, not only how often the pool publishes.

## Anti-goals

- Officers dashing to far routes past nearer mates, or swapping routes back and forth as call-outs come in. That
  reads as worse than the swarm.
- Anything that makes a chase more expensive per new fact than it is today (one flood per hunter per new fact).

## Approaches considered

- **A. Spread round the guess.** Each hunter claims a different spot round the guess, spaced apart, as firing spots
  do. The smallest change, but everyone still commits to one guess that can be wrong.
- **B. Cover the ways out (chosen by the user, 2026-11-20).** The way she was heading goes to one officer, and the
  other ways out of where she vanished go to the others. A wrong guess still leaves someone on her real route.
- **C. Search sooner.** End the chase earlier into the group search, which already spreads out. The simplest, but
  they stop acting like a chase.

## The design

### The plan: ways out of where she vanished

A **hunt plan** is made from one fact: the vanish point V and her heading. The fight owns it (`Fight`, which
`FightBoard` ticks under `Intel`). It never moves anyone; each member's `AggroState` reads its own slot and walks
there, as with firing spots.

1. **One flood from V out to Guess Distance** (8 m of path, the existing field), through `FloorReach`. The reached
   ground is where she could have got to.
2. **The rim** is the reached cells within about 1.5 m of the flood's edge: where the paths out of the area leave
   it. Walls cut the rim into pieces, so a doorway or a corridor end shows up as its own piece.
3. **Ways out:** the rim is grouped into routes about 3 m wide. In a corridor that gives one route per end. In an
   open room the rim is an arc, cut into directions. Each route is one spot on the rim.
4. **The heading route** is the existing guess (the reached cell furthest along her heading, less half its sideways
   stray). It comes first, and rim routes within a route width of it are merged into it.
5. **Ranking:** the rest are ranked by how well they line up with her heading, so the most likely ways come first.
   With no heading (a sound, or she was standing still), there is no heading route and the routes are ranked by
   distance from V. A plan is capped at six routes.
6. **A dead end:** if the flood never reaches Guess Distance (she vanished into a closed room), there's no rim and no
   routes, and everyone spreads over the reached ground (see below). That's a rule, not a fallback, so it logs.

The plan is made **once per new fact per fight**, never per frame or per member.

### Who goes where

When a plan is made, the fight assigns its members in one pass. Only members whose newest fact is the plan's fact
take part. A member who hasn't heard yet hunts their own guess as today, and joins the plan when the pool or a
call-out tells them.

- **Distances are walking distances.** The fight floods once from each route spot, bounded at about 30 m, and reads
  each member's path metres off it. Collect first, then assign: one scratch flood at a time. That's at most six
  floods per new fact per fight, which is cheaper than today's one per hunter once more than six are hunting.
- **First pass:** routes in rank order, and each takes the **nearest** unassigned member.
- **Second pass:** routes in rank order again, and each takes a second member, at least Spot Spacing (2 m, the
  existing field) from the first. No route takes more than two.
- **The rest spread out** over the plan's reached ground. Each takes the spot nearest them that is Spot Spacing from
  every other slot.
- A member beyond every route's bound is assigned last, in member order, and the log says so.
- **Keeping slots steady:** when a new fact re-plans (the contact holder calls her out every Speak Interval), a
  member keeps the new plan's slot that lies within a route width of their old one, if it's free, before the passes
  run. So a route holder isn't swapped to the other side of the room on every call-out.
- A member leaving the fight (the board's prune) frees their slot. The next re-plan fills it.

### What they do there

- They walk to their slot at Chase Speed, as they walk to the guess today.
- Within Search Reach they **face outward**, along the direction from V to their spot, and sweep with the existing
  looped `LookAround`, centred on that direction rather than wherever they arrived facing.
- Blocked (Stuck Time) or with no way there, they look outward where they stand, as today, and log it.
- Sight back on Ruby ends all of this at once, as today.

### Searching by the walk

`SearchCoverage.TryClaimNearest` ranks unseen points by **path distance from the member**. That means one bounded
flood from the member per claim, sized to cover the search area, read for each candidate. The fewest-blocks-first
rule and the claim spacing stay as they are.

A point the flood doesn't reach can't be walked to from where they stand. It's left for a teammate this pick, not
dropped. If the flood reaches no free point at all, they take the existing "nothing free" path (re-check, or look
about), with a log line.

## Left to the builder

- The exact route grouping (component-then-split, or greedy clustering), as long as a corridor end is one route and
  an open arc gives directions about 3 m apart.
- Whether the route width, rim band, route cap, flood bound and two-per-route are constants or fields. Prefer
  constants: the user trimmed the enemy Inspector deliberately.
- Gizmos: with an enemy selected, draw their slot and the line from V to it. With Intel selected, draw each fight's
  plan (V, the rim routes ranked, the slots).
- Doc updates: Aggro and Fights in `enemy-ai.md`, the search clearing in `enemy-searching-and-jobs.md`, and
  `project-history.md`.

## My calls (for the user to check)

- No more than **two per route**, with the rest spread over the vanish area.
- Assignment by **nearest walking distance in one pass**, not by who heard first.
- Six routes at most, about 3 m wide, from an 8 m flood (the existing Guess Distance).
- An officer at a route spot **looks outward down the route**.
