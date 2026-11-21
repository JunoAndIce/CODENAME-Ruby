# Enemy AI: searching, group jobs and distraction

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do. The rest of the enemy AI (ownership, sight, Aggro, fights, patrol, rooms, knowledge, senses, the behaviour map) is in `enemy-ai.md`.

## Searching, jobs and distraction

- **Beats round a search** (spec `docs/specs/2026-11-20-smooth-transitions.md`, `EnemyBeat` under Beats in
  `enemy-ai.md`). Entering from Idle or Patrol, they stop and turn to what they are going to check, seen or
  heard, for Notice Time before the first step; after a lost fight they hold the gun on the newest place they
  know of Ruby for Lose Time. The stand-down, alone (`SearchStep.StandDown`, which now ends as soon as the
  beat has played) or on a job (once each time `JobStep.StandDown` comes; the job's clock times it with the
  same Stand Down Time), is the Stand Down beat. The search's `Tick` and `FixedTick` wait behind any beat,
  which replaced the old distraction pause. Anything else reaching Searching warns and starts no beat.

- **Searching** (`SearchingState`). **One way in:** whoever sends them there asks first, with
  `EnemyController.RequestSearch(SearchRequest)` (an `Incident` plus whether they walk there), and
  `SearchingState.Enter` takes the request. `BeginSearch` is ask-then-`SetState`, for Idle and
  Patrol; `AggroState.Exit` asks alone, because the table makes that transition, and only when
  `LostPlayer` is what's sending them on. Entering with no request logs an **error** and searches
  where they stand at the lowest priority: a visible guard, not a silent default. (`Stage` and
  `LastKnownPlayerPosition` are gone: a caller that forgot to stage the target searched at some
  remembered spot without a word.) One already searching takes `Retarget` instead, which has no
  `Enter`.
  `Investigate(Incident, walkThere = true)` lives in `EnemyReactions` (the senses call it there
  directly). It first upgrades a SearchForProblem to
  **SearchForAttack** when they know of Ruby (`EnemyMemory.KnowsRed`: they have seen her or been told) or the newest news of her (their memory or the pool) is at
  most Attack Near Time (15 s) old and within Attack Near Radius (20 m) of the point **on the same
  floor** (floors are independent), then makes them wary if it's an attack. It ignores Grabbed,
  Ragdoll, Dead, Rearm and Aggro. From Idle or Patrol it asks `Intel.Handle` to decide (see **Group
  jobs**); a glance, or a scene with no `Intel`, begins a search of their own. Already searching, a
  serious thing brings their whole job, and on a running Search anything else is a distraction whatever its
  priority (see **A member's own stimulus** under **Group jobs**). On a Check, anything at their own job's
  place goes to the job first. Otherwise (a Check member, or a search with no running job) it moves them only for a *strictly
  higher* priority (through `Intel.Handle`, as from Idle; a glance, or no `Intel`, `Retarget`s their own
  search): an equal or lower one never does, which is what stops flicker, but it is
  **noticed** (a distraction, below). A search from Aggro has
  priority 6 (`BehaviourMap.FromAggroPriority`, one above the top of the scale), above every map
  row, so a sound can't swap their target while they've lost a player, though it still distracts them (a
  serious thing is the exception: it brings their whole job, see **Group jobs**). The state runs
  **steps**, `Walk`, `Look`, `StandDown` and `Done`, and a retarget starts them over. On a job the
  job's clock runs the steps and they play their part (see **Group jobs**); what follows is a search
  of their own:
  - **Walk:** to an *area* of Search Reach (1.5 m) round the spot, at Search Speed, with the same
    area move patrol uses. Officers of equal weight block each other, so two sent to one spot used
    to leave the second pushing at the first: the area move arrives once progress stalls inside it.
    No path at all warns. Search Travel Limit (20 s) ends a walk that never arrives.
  - **Look:** `LookAround` turns their body about Y, Look Sweep Angle (70°) each side of where they
    faced, in a whole number of sweeps of about Look Sweep Period (2 s) that fit the look, so they
    end facing the way they began. A search for an **attack** looks for Search Duration (10 s,
    from arrival), which stands in for searching the area. A search for a **problem** looks for
    Look Time (1.5 s).
  - **StandDown:** problem only. They hold Stand Down Time (1 s) and log "all clear" (the gesture,
    until there is an animation), then are `Finished`.
  - The table sends a finished search to Patrol when `HasActiveRoute`, else Idle. With `walkThere`
    false they face the point and look from where they stand: that is the glance.
  - **A search's disturbances end with it:** `SearchingState.Exit` clears their distraction count and
    takes them off their job.
- **Group jobs** (`JobBoard` and `GroupJob`, in `Enemies/`). A few enemies dealing with one thing
  together. `Intel` owns the board, ticks it every frame (`Advance`) and forwards what the enemies
  call (`Handle`, `Leave`, `NoteArrived`, `CheckerOf`...). A job **never moves anyone**: each member's
  state machine reads it (`EnemyController.Job`) and plays their part in `SearchingState`, so the
  machine stays the one place movement is decided. A job has a kind, a place, a reason (problem or
  attack), a priority, a step, and members up to Job Cap (3, on `Intel`; the lead counts).
  - **Starting and matching.** `Intel.Handle` is the one decision for an enemy who senses
    something they may act on. A job **covers** it (`Covers`) when the thing is within Job Match Radius
    of any of its places (10 m, **on the same floor**; a grown search has one place per absorbed patch), or, for a Search, the thing is inside a room its search area marks
    (`SearchArea.InRoom`, on that room's floor), however far from the place: something in an office a
    team is searching is that team's, not a second job's. **A Check matches by distance only**, since it
    searches no room: counting its marked rooms swallowed things into a job that never looked at them.
    A covering job already has it: one whose marked room holds the thing comes
    before one matched by Job Match Radius alone, however much nearer that one's place, and otherwise
    the nearest place wins. **A serious thing on a running Search goes the way a member's notice of it
    does** (`JobBoard.TakeSerious`, from `Handle` as from `Mobilise`; see **Serious things bring the whole
    job**), so what the team does doesn't depend on whether a member or a passer-by heard it first: a
    passer-by's used to move the Search through `Upgrade`, shrinking a grown area to one circle and
    throwing away what was seen, while a member's kept everything. **A Search stays put for a thing inside
    a room it marks** or within a metre of its place (`GroupJob.Upgrade`): it raises the reason and priority
    where it is, without restarting, because it clears that room whole either way. **Any other move of a
    Search keeps everything it has** (user, 2026-11-11: a move of a grown Search keeps its rooms and what's
    been seen): **a Search moves only for a serious thing, a merge or a Check's conversion**, never for an
    ordinary thing at any priority (user, 2026-11-11). A non-member's ordinary thing at a running Search
    (`JobBoard.JoinRunningSearch`) raises its reason and priority (`RaisePriority` moves nothing) and
    grows it there, as a member's notice does, and they join if it is recruiting. A serious thing or a
    merge moves its source there within the area
    (`GroupJob.MoveSourceTo`, growing a patch at the spot first), keeping every patch, room and the
    coverage, and the team walks to the new source; only a gunshot inside the area makes them look again.
    A spot the area can't take in (nothing open round it) leaves it where it is, raised, with a warning.
    **A Check doesn't keep anything:** it is one look at one spot and searches no room, so
    a more serious thing more than a metre off moves it there, its area rebuilt round the new place, and
    starts it over (as a Search, if the thing makes one), or the louder spot would never be visited and the
    team would say all clear. `Point` and the area's source always move together. A more serious thing upgrades that job; **a find (a body
    or a loose gun) or anything at Alarm Speed, or an enemy seen flying or on the chain, turns a Check into a Search whatever its
    priority** (`Incident.MakesSearch`), because the check is over: a flying enemy's Search is at the
    throw's origin, a find's where it lies, and a slower flying gun or crate stays a Check. A stronger reason (an attack, on a problem job) raises it at the same
    priority. They join if it is still recruiting (a Check until it looks, a Search until it's done,
    never past the cap), taking themselves off any job they were on. If it can't take them, an enemy
    off any job leaves it to the job; one already on a job **stays on their own**, and the thing is a
    distraction for them, because dropping them left them searching with no job. With nothing
    matching they start a job and **leads** it: whatever makes a Search starts one, anything else a Check.
  - **Steps.** A Check goes Call, Gather, Go, Look, StandDown, Done; a Search goes Call, Go, Search,
    Done. A lead with nobody to call skips Call, and a Check of one skips Gather too. **Call:** the
    lead reacts alone for React Time (0.5 s), then calls the free enemies within Speech Range along
    the floor (`FindCallees`), nearest first, one at a time: they turn to one, who is added only then
    and turns to face them, for Call Time (0.6 s) each. Nobody they call is on the job before the lead
    has reacted, so a recruit doesn't turn the moment the sound is heard (an enemy who hears it
    themselves joins at once, since they are reacting to the sound and not to a call). Each is checked again as they've
    called (they may have gone to Aggro, been grabbed or taken by another job in the second since),
    and a stale name isn't added. **Gather:** the team faces the spot for Gather Time (0.75 s).
    **Go:** a Check waits for the checker to arrive, a Search for every member (see below).
  - **One clock, one outcome.** The job, not its members, decides when a step ends, so a Check's team looks,
    gestures and finishes on the same frame (a Search ends differently, below): members each timing their own look is how one said all
    clear while another walked off aggressive. A step's length (Look Time or Stand Down Time, from
    the lead's tuning) is **fixed when the step begins** (`StepLength`), so a lead
    who leaves mid-step can't stretch or cut it. Go and a Search's search have no length and wait
    explicitly: inferring it from a zero duration froze every step at Stand Down Time 0. A job that empties during `Advance`
    is removed after the walk, so none misses a tick. A job's reason **only rises**: whatever one
    member knows is known to the team, and an attack makes every member wary at once.
  - **A Search clears its area, and ends one by one** (`SearchingState.Roam`, `SearchCoverage`, `SearchArea`). **The circle marks what is searched; it isn't the area.** `GroupJob.Area` floods from the place inside Search Area Radius (`PathGrid.CollectGround`, with the test passed in, so Pathing knows nothing of rooms): every room holding no rooms (`Intel.LeafRoomAt`) the flood reaches is searched **whole**, out to its walls, and ground it reaches in no such room (a corridor, a floor-sized room's open floor) is searched as it lies. A flood never crosses a wall, so a room counts only when the circle reaches it over open ground, through a doorway inside the circle: an office behind a wall whose door is outside the circle isn't searched, and a search in a corridor between two offices with their doors in the circle clears the corridor and both. The area floods **once when the job is made**, again only when a Check moves (a Search's move keeps its area), and once per patch it grows by, never per step or per member; coverage is copied from it. **The area can grow** (`SearchArea.Absorb`, `GroupJob.GrowAt`): it is a set of patches, each a place and its circle, and absorbing a spot floods that spot's circle once and adds the rooms and ground it reaches that the area hasn't got (a room it already marks adds nothing), while `Place` stays the current source the team walks to. Everything that reads the area reads the whole: `Contains` is any patch's circle or any marked room, and the rooms and ground are every patch's. New points join the coverage when it exists and start Search Minimum Time over (`SearchCoverage.AddGround`); a coverage built later copies the whole area. A spot whose circle adds nothing is still kept as a patch, with a log line, so it counts as one of the job's places for matching; one within a metre of a patch already there floods nothing; and one on another floor, on no grid or with nothing open round it is refused with a warning, since a search never spans floors. A Search is never rebuilt (a Check converted during a signal's beat still counts as a Check when the beat ends: see **Serious things bring the whole job**): every move of one (a serious thing or a merge; an ordinary thing only grows it) moves its source within its area and keeps all of it and its coverage (`GroupJob.MoveSourceTo`, `SearchArea.MoveSourceTo`, `SearchArea.TryGetPatchAt`; see **Starting and matching** and **Serious things bring the whole job**). A merge no longer has to put the older's area back after the move, since nothing drops it. With `Intel` selected in Play, each patch's circle is drawn faintly. **In the search area** means, everywhere it is tested (gunshots; `JobBoard.InSearchArea`, forwarded on `Intel`, which is `SearchArea.Contains`): within any patch's circle (the first is the place's Search Area Radius) on the area's floor, or inside one of the rooms it marks, on that room's floor, however far from the place. It is always read through the area, never as a distance to `job.Point`, because that misses absorbed patches and the marked rooms, which reach as far as their walls. **Members walk to the source, then spread out** (user: "walk to the source of the problem/attack THEN start searching", so a search reads as going to the problem and working outward; starting from wherever they stood in the area had a gunshot just outside an office searched from inside it). Arriving is not being in the search area: a member arrives within Search Start Radius (2 m, on `Intel`) of the source (`SearchingState.WalkToSource`), at once with a clear line to it, or when the walk says so, which counts stalling anywhere inside that circle, so several sent to one source stop where the first ones stand instead of pushing at them. The source is the job's place when it can be reached; when it has no way in (a blocked cell, behind a prop), the nearest of the source patch's own ground that can be reached (`SearchArea.SourceGround`: the points its own flood reached, not the grown area's other patches), since that was flooded from the place and is joined to it, on its side of every wall; and only failing that, the nearest reachable ground with a clear eye-height line to the place (`JobBoard.TryGetSearchStart`, through `GridNavigator.FindGroundNear`; the crow-flies nearest alone could be a corridor behind a thin wall, and start the team in the wrong room). It is found once per place by the first member who asks, from one flood, and kept on the job, but only the place itself or the source patch's own ground, which are joined to the place, so anyone who can reach the place can reach them. The last resort, ground in sight that only they can reach, is found from their own reachable region and kept for them alone (`SearchingState`), so a teammate cut off from it asks again rather than being sent there and dropped. A member on another floor from the job is taken off it before they ask (a flood reads cells by X and Z only, so it can't tell floors apart). Roaming then claims the unseen point nearest them, so the search spreads out from the source. The search proper waits for every member to arrive, and one who can't reach the source (no ground near it they can reach, no path, or Search Travel Limit) is taken off the job and goes back to patrol. **What's left is shared by the team:** the area's points about a metre apart, cleared when anyone has one in view with a clear line, the same test as for Ruby. Each member heads for the nearest point nobody else has claimed **by their walk** (one flood from them per pick, out to three times the area's span plus 5 m; a point farther by the crow than the best walk so far isn't read): by the crow, a point through a wall won and they walked round the wall to it past the one beside them. A point their walk doesn't reach still ranks, after every reached one and by the crow, with a log line, so one nobody can reach is still claimed and its walk drops it, rather than holding the search open for good, at Search Creep Factor (0.75) of Search Speed, with their body and view swinging Walk Sweep Angle (45) across their way (`EnemyMovement.SweepTo`) instead of stopping to turn, and moves on as soon as it's seen, so they walk only far enough to see round the crate. **No time cap** (user: the max time must not break the search before it completes): the job finishes ("has looked everywhere") once every point is cleared **and** Search Minimum Time (5 s, on `Intel`) has passed since the coverage was built (the first member reaching the source, or reaching a source the job moved to), without waiting for a member still walking in, who leaves from where they are. **Re-checking:** with nothing free to clear before then, a member walks to a random spot of the area's ground at least a quarter of its span from them (the farthest of a few draws, away from others' claims) and sweeps it at clearing speed (`SearchCoverage.TryClaimRecheck`), so a team that saw an open room at a glance still walks and sweeps for the minimum; only with no such spot does they look about where they stand, for a random share of Look Time. **A point nobody can clear is dropped with a warning** naming the point, the member and why (`SearchCoverage.Drop`): they have no path to it, run out Search Travel Limit, or arrive and still can't see it (a stalled walk gets one direct look first, so a point in plain sight from where they stopped is cleared instead). **A blocked walk only defers its point** (`SearchCoverage.Defer`): something in the way, a crate being pushed or a teammate, says nothing about the point, so its claim is released and `TryClaimNearest` prefers less-blocked points, sending them on to others; a point blocked three times in all, by anyone, is dropped with a warning saying so. (Counting a block as unclearable, with a give-up after three failures, once dropped a whole 201-point search because a pushed crate stood in a lone member's way.) Nobody gives up roaming: every walk clears, defers or drops one point, and a point is deferred at most three times, so a search can't be held open for good. A re-check walk that fails drops nothing: they look about, then pick again. **No ground:** a place on no grid, or a member on another floor from it, can't be walked to, so each member is taken off the job with a warning as unable to reach the source, before any coverage exists, and the job closes with its last member. A place on the grid with nothing open round it to clear, once members have reached its source, warns that the team looks about for Search Minimum Time and then stands down, which it does. Search Duration plays no part in a group Search: it is only the length of a search of their own (a glance, or a scene with no `Intel`). A gunshot in the area throws away what they've seen, so they look again, and the new coverage starts the minimum over; any serious thing then brings the whole team to its spot. When it ends each still walking to an unseen point finishes that walk, and one re-checking or only looking about stops at once, since a Done job takes no new member or sound. With `Intel` selected in Play, each Search's marked rooms are outlined in its colour and its unseen points are dots. A Check doesn't do any of this: it stays one quick look.
  - **The checker and followers.** In a Check one member goes to the spot and looks: the nearest to
    it (flat) when the team sets off, chosen once and kept (`CheckerOf` only reads the job), unless a
    nearer enemy joins in the first moments of Go. If they leave in Go, the nearest of the rest takes
    over at once; during the look, the job goes back to Go so someone else walks over and looks it
    over themselves. A Search sends everyone. The others follow them at Search Speed x Search Creep
    Factor (the same factor clearing uses), stopping within Follow Distance (3 m) and facing the spot, with `ChaseTo`: **the
    floor's shared chase field toward the checker**, which re-floods only when they change cell,
    throttled, and which every follower reads, so a team of three costs one flood. (A field of each
    follower's own re-flooded every quarter metre the goal moved: up to sixteen floods a second per
    follower.) A follower's walk isn't limited by Search Travel Limit; the checker's is.
  - **A member's own stimulus.** A serious thing goes to `Mobilise` first (see **Serious things bring the
    whole job**). **On a running Search anything else is ordinary at any priority** (user, 2026-11-11;
    `EnemyReactions.Investigate`): it is a distraction (see **Sending one to check** and **Distraction**),
    so they, or the first of the team to notice it, goes to check it, the search grows there, and it counts
    toward their own limit, however much it outranks the search. A fast crate seen by a member of a lesser
    Search used to upgrade or move the job, or take them off it to lead a new one, and nobody counted it.
    An attack-reason thing still raises the job's reason for everyone where it now reaches (`RaiseJobAt`,
    after the search has grown there), since whatever one member knows the team knows; that neither moves
    nor restarts the job, and its priority doesn't rise. **On a Check**, a thing at its place goes to the
    job before anything else (`HandleAtOwnJob`, `RaiseJobAt`): it upgrades or converts the job for everyone
    (a find, a flying enemy or anything at Alarm Speed still turns it into a Search), or raises it to an
    attack for everyone, and only what the job didn't take is a distraction for them; one elsewhere that
    outranks their search goes to the squad (`Handle`). At the job's place they never latch `Wary` alone,
    because everyone on the job goes wary together; away from it, what they sense is their own and they do. A
    glance at the job's place can raise its reason, but never converts, moves or restarts it.
  - **Filling and merging** (every Intel Interval, one pass each). **Fill:** each open job still
    recruiting with room has its lead look again for free enemies within Speech Range along the floor
    (`FindCallees`, one flood per job, read at once) and call them in one at a time as at the start, so
    one who joins a team already walking is called and not snapped in; skipped while a call is queued,
    and checked cheaply first so a level with nobody free costs no flood. **Merge:** two open jobs become one
    when the older covers the younger's place (within Job Match Radius of any of its places, or in a room the older marks
    when it is a Search) **or** both are Searches whose areas mark a room in common
    (`SearchArea.SharedRoom`, two short lists compared in place), so two teams never search one office
    apart. A Check's marked rooms never count, for a merge or for an extra's spot: it matches by distance only. A merge that moves the place goes through
    `GroupJob.Upgrade`: an older Search moves its source there and keeps its whole area and what it has
    seen, and an older Check always moves to a higher-priority younger's place and starts over there, its
    area built round it. **The older stays**: its members first, then the
    younger's up to the cap, joined at once with no call, with the higher reason and priority, a
    Search if either is, and the place of the higher priority (a tie stays put, so a merge doesn't move a
    team for nothing; and an older Search keeps its place when the younger's lies in a room it marks,
    as for any upgrade). **A merge keeps both areas** (`GroupJob.KeepArea`, `SearchArea.Absorb(SearchArea)`):
    the older's own area is never dropped (a Search's move keeps it), and the younger's, when it was a
    Search, is absorbed into the merged one after the upgrade, so neither place nor its rooms is lost. The
    younger's was flooded when built, so this copies its rooms and ground and floods nothing (an older
    Search moved to the younger's place has grown a patch there first, which floods that circle once more:
    absorbing first would save it, but would count the younger's rooms in the stay-put test and keep the
    team off the higher priority's place). A Check's area isn't kept, since a Check searches no room. One pair merges per interval. **Extras** who don't fit: on a problem they are
    dismissed to patrol (`SearchingState.Dismiss`); on an attack each starts a Search of their own at a
    spot Job Match Radius + 2 m to 2.5 times it from the merged place (three tries, never a spot another
    open job covers, nor one whose own search area shares a room with an open job's, so it can't merge
    straight back; the area built to test that is the one the new job keeps, so it floods once), else is
    dismissed with a log line.
  - **Joining from Aggro** (`JobBoard.JoinFromAggro`, from `SearchingState.Enter` on a request marked
    `AfterLostFight`). One who starts a search from Aggro, after a lost fight or on getting up from a
    grab, joins or starts a job at the spot they knew. Where a job covers that spot (`Covers`) they
    make a Check, or a job not yet an attack, a Search for an attack for everyone, **once per area**
    (`EnemyMemory.NoteRaisedAt`: again only when their search is that far from where they last raised one,
    so the flip from Aggro to Searching and back can't raise it each time), and join it if it has room;
    a full or past-recruiting job dismisses them to Patrol Aggressive, since they are wary by then. With no
    job there they lead a Search for an attack and call in the free officers. A lost-fight team past
    the cap goes to Patrol Aggressive and starts no job of its own. **The job then grows to where they saw
    her heading** (`SearchRequest.Guess`, the guess `AggroState.Exit` read from `EnemyAwareness.TryGetGuess`;
    `GroupJob.GrowAt`, "where ... saw Ruby heading"), so the search covers where she went as well as where she
    vanished. With no heading the guess is the vanish point and nothing extra is grown; dismissed or refused,
    they are on no job and grow nothing.
  - **Individual memory and telling** (`JobBoard.JoinMember`, the one place an officer is put on a
    job, so every route in counts and tells). Each time they are put on a job they weren't on (started,
    joined, called, filled, merged out; a retarget, a distraction and a glance don't count) their
    `TimesSentToLook` rises, and from Sent To Look Threshold (3, on the officer) on, every job they are
    put on becomes a **Search for an attack** at its own place, for everyone on it (`>=`, through
    `JobBoard.Upgrade`, so a Check turns into a Search by the usual path, another team's Check that
    calls them in included), so two thuds ending all clear make the third, and every later look, a
    search for an attack. **Telling:** if any member has seen Ruby themselves, every member who doesn't
    know learns the newest of the seers' facts as **Told** (`WasToldOfRed`; the newer by time wins),
    before anyone moves, and a seer on the job raises it to an attack. **Only one who saw her tells**
    (`HasSeenRed`, never `WasToldOfRed`), so being told can't chain: a told officer who could tell
    would carry one sighting down every chain of jobs through the level, the removed level alert by
    another door.
  - **Serious things bring the whole job** (`JobBoard.Serious`, `JobBoard.Mobilise`, forwarded on `Intel`; spec piece 7, "Distractions on a search"), whatever the priority, for anyone searching on a running job, before anything else in `Investigate`: priority let a gunshot pass as a distraction, and a team about to finish walked off from it. **The same for a non-member:** a serious thing an idle, patrolling or lone searching enemy brings to the squad (`Handle`) that a running Search covers is taken by that job the same way (`JobBoard.TakeSerious`, the part of `Mobilise` after its no-job case), and they join it if it has room, so the team does the same whoever heard it first; a lone searcher's gunshot keeps its kind and source on its way to `Handle`, so it reads as serious there. **Serious** is a gunshot or an enemy seen killed (`Incident.Mobilises`), an enemy seen flying, alive or dead (a moving thing whose `Incident.Source` is an enemy; the senses take each throw once per Origin Lifetime, so it counts once per throw), or a body the squad hasn't booked (a dead enemy the published pool doesn't know; once it is booked, one who sees it only glances, and a glance carries no source, so a known corpse never calls the team back). A thrown crate or gun, a thud, the whip, a loose gun and a living enemy standing on the chain are ordinary (**Sending one to check**). A living enemy held on the chain and standing still, seen by a job member, shows exactly where Ruby is, so it says something of her (`EnemySenses.Notice`'s `showsRuby`): one goes to check it (or the first to notice), the search grows there, and it counts toward their limit; a held corpse, a lying corpse and a loose gun stay finds that say nothing of her. One with no running job, a lone searcher or a member finishing their last walk, still joins or leads a Search at a gunshot or a killing, as before; anything else serious off a job is handled as it always was. **The job rises at once:** an attack for every member, and at least the thing's priority. A gunshot inside the area (`InSearchArea`) throws away what was seen so they look again (once per Distraction Gap, starting Search Minimum Time over), because Ruby is right there; a body throws away nothing. **A Search's area grows at the spot only when the job takes the thing** (a signal or a move: `GrowAt`, once per spot, since a patch already there floods nothing), so one left over inside a Distraction Gap floods nothing and restarts nothing. **Then they all go there:** the spot becomes the job's new source and the job keeps everything it had, the old area plus the new (`GroupJob.MoveSourceTo`: the spot's patch becomes `SearchArea`'s source through `MoveSourceTo`, and `Point` follows it, so `Point` is always one of the area's places and the walk-in point, keyed on `Point`, and `SourceGround` are worked out for the new source). The coverage is kept and claims released, and every member walks to the new source and spreads out from it as from any source; Search Minimum Time starts over when the first of them reaches it (`GroupJob.AwaitingSource`, `SearchCoverage.RestartMinimum`), and until then the search can't end from Go, or a team whose area was all seen finished while still walking to the body. A Check becomes a Search there instead (`Upgrade`, which moves it when the spot is more than a metre off), since a Check searches no room and has no area worth keeping. Calling or gathering, the job moves at once; otherwise the team **signals** first (`JobStep.Signal`: stopped and facing it for React Time, members out on a check stopped with them) and goes when the beat ends; in Signal a newer serious thing becomes where they go. In Signal the beat survives an upgrade or restart of the job (`GroupJob.Restart` and `GroupJob.MoveSourceTo` leave Signal running, and `GoToSignalledThing` leaves it when the beat ends), so a non-member's `Handle` converting a Check, a merge moving a Search, or a join from Aggro, can't drop the pending move. **A Check converted during its beat** (the third look, a non-member's find, a join from Aggro) is still made a Search at the thing when the beat ends (`GroupJob.SignalledAsCheck`, recorded at `BeginSignal`; `MoveTo`'s `asCheck`), its area built there, rather than growing the Check's own circle and keeping its rooms as a running Search would; nothing is grown for it during the beat. Its Search is built at the signalled thing, so anything the Check took in during that beat (a merge's absorbed area, growth) is dropped: a known, narrow limit. A Search already calling, walking to or searching round that very source carries on (no re-signal), though a gunshot inside the area still makes it look again, once per Distraction Gap. **No yanking:** within the noticer's Distraction Gap of the job's last serious move (`GroupJob.LastSeriousMoveAt`), it doesn't move again, so a firefight, a tumbling body or a second sighting of a find raises the job but grows nothing and is only a distraction for them; the rest who heard the same shot in the same frame, and a killing followed by the body it left in one scan, find it done (`LastSeriousAt`). **Another open job's ground doesn't stop it** (user, 2026-11-11): the team that noticed it goes, and the next Intel Interval's merge folds the two jobs, as a gunshot always did; only ordinary things stay that team's. **A thing off the job's floor** is refused with a warning (floors are independent), and so, with a log line, is a spot the area can't take in (nothing open round it). **Every enemy who notices a serious thing counts it toward their own distraction limit first** (`EnemyReactions.CountDistraction`, before `Mobilise`, taken by the job or not, by the usual rules: Distraction Gap, Distraction Memory, Threshold), so each of a team who hear a shot is one closer to going after Ruby; one who reaches the threshold goes to Aggro and the job doesn't then handle them as a member. **Only the provoked one reacts** (user, 2026-11-11): what tipped them over, serious or ordinary, never reaches their job, so a serious thing only they noticed brings nobody else and grows nothing, and the team carries on; a log line says so (`CountDistraction`: "isn't told of"). What `Mobilise` leaves over only turns them, with no second count. Nobody scouts a serious thing (`Noticed` refuses it). A signal isn't merged, because a merge would lose the thing.
  - **Sending one to check** (`JobBoard.Noticed`, `GroupJob.SendToCheck`/`TryGetCheck`/`EndCheck`; spec piece 7, "Distractions on a search"). A member's fresh distraction, after their own count has taken it, goes to the job, the one entry for it; on a running Search that is every ordinary thing a member notices, whatever its priority (see **A member's own stimulus**). **One member notices it: they go to check it alone.** **Two or more notice the same thing: the first goes at once, and the rest turn and carry on.** The same thing is the same source object (`Incident.Source`, set by every sense site that builds an `Incident`) and stimulus kind (`Incident.Kind`: the crate seen moving and the crate heard thudding are two, but MovingObject and FastObject count as one seen-moving kind, so one crate seen at two speeds by two staggered scans is one thing), or, for a sound with no source, the same grid cell, noticed by a member of the same job within 0.5 s (`GroupJob.FindNoticing`, a short list pruned on every look); farther apart they are two noticings and each who can goes. If the first to notice couldn't go, the next who notices it goes in their place, and so does the next when the one who went has left the job since (`GroupJob.Remove` clears their name from its noticings, or the next would defer to someone gone). **Several members can be out at once**, each on their own thing (decision 4: a map of member to check point on `GroupJob`, in place of the old single scout), and one already out is sent on to the newer thing. Inside the search area as much as outside: they walk to where it happened either way. **The search grows when it is noticed** (`GroupJob.GrowAt`), not when they arrive, and stays grown if they can't reach it, so the team clears it whoever gets there; a new thing grows it once, the same thing never again, and a spot within a metre of a patch already there floods nothing (`SearchArea.Growth.AlreadyThere`), so a run of distractions on one spot floods once. A spot whose circle adds nothing is still kept as a patch (`Growth.PatchOnly`, with a log line), so it counts as one of the job's places for matching. **On a Check** nothing grows (it has no area to clear): the noticer looks and comes back. Who may go: the job must be under way (a Search going or searching, a Check going or looking) and they mustn't be a Check's checker, who has its own spot to reach; otherwise they turn, with a log line saying why. A serious thing (`Mobilise`'s; what reaches here of one is only what `Mobilise` left over), a body or gun lying there and anything else that says nothing of Ruby send nobody and grow nothing here, and neither does a spot off the job's floor, which warns (a point `WorldToCell` can't place on the job's own floor warns separately). **An ordinary thing another open job has** (that job's `Covers`, or its area's `Contains`: `JobBoard.AnotherJobHas`) is that team's on every path, not only when it outranks their search (a serious thing ignores this): they turn toward it and it counts for them, but their job doesn't grow there and nobody is sent, or the next Intel Interval would merge the two jobs. A Search in Go isn't kept from letting a member go check for want of someone to wait on: it finishes from Go anyway, and a member who hasn't arrived only keeps the step at Go a little longer. **The walk** (`SearchingState.CheckForJob`, per member, from `TryGetCheck`): they set off at once, with no Distraction Time pause, walk there at Search Speed, look for Look Time and go back to their part in the job, which on a Search means roaming the grown search (one who hadn't reached the source yet walks there again with a fresh Search Travel Limit; the check isn't their walk in, so it doesn't count against it). While out they clear what they see of the team's coverage. Their claim on the coverage is released when they are sent, so nobody is kept off their point meanwhile. **Nobody is left between two systems:** a check ends when they finish it (`JobBoard.CheckDone`), when they leave the job (Aggro, a grab, a fall, death, dropped, merged away: `GroupJob.Remove`), when the job starts over (`Restart`, which re-follows everyone, so a check is lost with the place it was for), when a serious thing stops the team for its beat (`BeginSignal`) or moves the source (`MoveSourceTo`, which re-follows everyone into the walk there), and at once when the job ends. A team signalling still grows for an ordinary thing, since the serious move it is about to make keeps the whole area. A member out checking isn't chosen as a Check's checker unless everyone is.
  - **Membership invariants,** checked where membership changes. **One job per enemy:** `GroupJob.Add`
    refuses, with an error, an enemy already on another, so every route onto a job takes them off the
    last first (`JobBoard.Leave`). **An enemy on a job is in Searching:** `JoinMember` errors
    otherwise. It is checked there and not at `Add` or `Leave`, because `FollowJob` is what puts them
    in Searching and `Leave` runs inside `SearchingState.Exit`, while the machine is still changing
    state. `JobBoard` is the only caller of `Add` and `Remove`. `Leave` is reached from
    `SearchingState.Exit` (Aggro, a grab, a fall, a death or the search ending) and
    `EnemyController.OnDestroy`, and a job with nobody in it closes. `SearchingState` also warns when
    they are off their job and still searching, which nothing should leave them, and `CheckerOf` errors once
    per job for a Check in Go or Look with no checker. Each is a guard, not a fallback.
- **Distraction.** `Investigate` while they are in Searching at an equal or lower priority calls
  `Distract`, and so does anything ordinary on a running Search at any priority (see **A member's own
  stimulus**); it returns true (a taken reaction) and never changes their search's target, reason or
  priority. Their own count takes it first, always (below). Then **on a job** a fresh one goes to the job
  (`JobBoard.Noticed`, see **Sending one to check**): it grows the search there, and they, or whichever
  member noticed the same thing first, walks over to check it. **One who doesn't go** (another noticed it
  first, the job isn't under way, they are a Check's checker, a body or gun lying there) turns
  toward it (`SearchingState.Distract`: the Notice beat, `EnemyBeat`) and the search's timers and walk wait
  behind the beat for Distraction Time (1 s), then carry on where they were. So does anyone **off a job** (a glance, or a
  scene with no `Intel`: spec, "Off a job"), and one whose thing belongs to another job, who only turns
  so as not to walk onto that team's ground. A repeat of the last disturbance within Distraction Gap
  only turns them again while they've already looking, and never goes to the job. Each
  officer counts their own (`EnemyMemory.NoteDistraction`): one within Distraction Gap (2 s) of the
  last disturbance of any kind is the same (a tumbling thrown body thuds about ten times, and a firefight
  is a run of shots), and it counts only when it is also that far from the last counted one; each is forgotten after Distraction Memory (12 s). **The count ends with the
  search** (`SearchingState.Exit` clears it): a count left over made the next, unrelated search
  aggro on its first or second one. **What counts is whether Ruby is doing something, not its priority** (`Incident.SaysNothingOfRed`): a low thud during a body search counts, a body or gun lying there doesn't, and they turn to look either way. The count portrays enemies provoked by her throwing things at them, who get tired of it and go after her. **A serious thing counts too** (each who notices it, before the job handles it: see **Serious things bring the whole job**). **Counts stay personal:** each enemy counts only what they themselve noticed, and sending someone to check shares nothing, so two who hear one thud are at one each and the one who went is not at two. At Distraction Threshold
  (3) they have had enough: the count clears, they are taught where **she is at that moment**, once, as a Heard fact, not where the last thing landed, and `Alert()`s into Aggro. What tipped them over isn't passed to their job (only they react; a log line says so).
  **They see her only with their own eyes** until she is in their view cone with a clear line (`EnemyController.ProvokedUnseen`, set by `GoAfterRed` before `Alert`; see **Aggro**): they run at what they were told without giving up on the way, learns no Seen fact, doesn't call her out, track her, fire or take a firing spot, so a provoked enemy who hasn't looked at her can be slipped past from behind and can't spread her position on hearsay. The first time she is in their view they have seen her and every normal rule follows, the call-out that brings the others included. Only the provoked: everyone else in Aggro keeps all-round sight.
  A search from Aggro (priority 6) is outranked by nothing, so a sound only distracts a lost-fight
  searcher, except a serious thing, which brings a searcher's whole job whatever the priority, and a
  gunshot or a killing, which takes a lone searcher to the squad (see **Serious things bring the whole
  job**); what `Mobilise` leaves over (one within Distraction Gap of the job's last
  serious move, one off the job's floor, or a spot the area can't take in) only turns them and sends nobody; it has already been counted for them. Idle and patrolling officers aren't distractible: they go and check.
