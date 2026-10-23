# Enemy animations: officers on a humanoid model

Status: approved 2026-10-21, built 2026-10-22 on branch `enemy-animations` (plan
`docs/plans/2026-10-21-enemy-animations.md`). **Changed in play-testing** at the end records where the
build differs from this design. Limp ragdolls for officers are the next branch,
not this one.

## Intent

Officers stop being capsules. Each one wears Kevin Iglesias's `HumanM_Model` (already in the
project, under `Assets/Kevin Iglesias/Human Animations/`) and moves, aims and reacts the way a police
officer would. The pistol sits in his hand. The animation side is built as its own reusable piece
that the game only *tells what the character is doing*; the piece decides how that looks, so real
animations can be dropped in later without code changes. Ruby moves onto the same piece.

What the user wants players to see, in their words:

- "From patrolling, they walk with the gun at a resting position. [...] the pistol will be held down
  walking crouched like officers do in raids."
- "When spotted they stand upright to shoot or chase Ruby to move into range. They will have a
  running animation that moves the gun down like safety protocols in police training."
- "When grabbed they will move to a constricted animation [...] Grabbed enemies will try to wiggle
  but will be held tight." The gun should "pop out", a small pop, not a big fling.
- "When thrown, they will move to ragdoll immediately. If they don't die, they will have a getup
  animation and move to the next state they need to be in."
- Standing and shooting while Ruby circles: the "upper body should turn to a certain point but then
  the legs should follow."
- "We will eventually add animations for these": two-handed weapons get a two-handed rest.

**Most of these animations don't exist yet.** The pack has idles, 8-direction walk, run and sprint,
turn-in-place, a hand-grip pose (`Human@ObjectGripHands01`) and avatar masks. It has no pistol
stances, no pinned pose, no wriggle, no get-up and no aim poses. So the core of this branch is a set
of **named slots**, one per moment, each filled now with the nearest stand-in from the pack.

## Goals, non-goals, anti-goals

Goals:
- Officers show the right body for each moment (table below), with the gun in the right hand.
- The aim twist: upper body turns toward Ruby up to a limit, then the legs turn in place to follow.
- The game code reports what a character is doing; it never names a clip, state or blend tree.
- A real animation replaces a stand-in by editing one asset, with no code change.
- Ruby runs on the same piece, with the same body turn as officers, while her controls stay as
  instant as today.

Non-goals (later branches):
- Limp ragdolls for officers (the next branch). See **Not a falling animation** below.
- Hand-made aim poses (an aim matrix, as Valve's games and Unreal's Aim Offsets do). The code twist
  is the stand-in, built so the poses can replace it.
- Two-handed weapons, a pickup animation, Ruby's own get-up, footstep sounds.

Anti-goals (a failure even if it technically works):
- **An animation moving or slowing a character.** No root motion. The game code moves every body,
  as now; animation follows the body. Movement and aiming stay instant.
- Game code that knows clip, state or parameter names, or a system assembly that references
  `Ruby.Game`.
- Ruby's aim, shots or whip responding any later than today. Only her legs may lag.
- Hand-editing `.controller`, `.prefab` or `.unity` YAML.

## Decisions

The user chose each of these:

- **Officers wear `HumanM_Model`**, the pistol held on its right-hand prop bone, `B-handProp.R`. Both
  pack models have `B-handProp.L` and `.R`, bones made for holding props.
- **Moments and how they look** (stand-ins in the slot table below):

  | Moment | Body |
  | --- | --- |
  | Patrolling, searching, idle at a post | **Rest**: crouched raid walk, pistol held low |
  | Ruby in sight and in range (firing), whether he's chasing, holding distance or backing away | **Aim**: upright, upper body twisted toward Ruby, legs walking in any direction |
  | Closing in beyond range or out of sight (or running unarmed) | **Run**: gun pointed down |
  | Grabbed by the chain, or spun as a top | **Restrained**: pinned arms, wriggling |
  | Thrown, knocked down, or dead | Animation **frozen** in its current pose (this branch) |
  | Getting up after surviving a fall | **Get up**, then on to the next state |
  | No gun | The pack's ordinary walk and run |

- **The aim twist is code**, Unity's built-in Humanoid look-at on the spine, not baked poses. Past a
  limit (about 60–90°) the legs turn in place, using the pack's Turn Left and Turn Right, until he
  faces Ruby again. Hand-made aim poses replace the code twist later; the code then passes the angle
  to the poses instead of turning the spine.
- **The gun pops out on a grab** with a small hop, not a fling. Knockdowns and deaths keep today's
  rule (the gun leaves at the officer's speed, which after a hit is the hit's speed).
- **Not a falling animation.** In this branch a thrown, knocked-down or dead officer still tumbles
  as his stiff capsule; his animation simply freezes in whatever pose he was in and tumbles with it.
  The next branch makes him truly limp (animation off, bones live, as Ruby does), so no falling clip
  is ever needed.
- **Limp bodies come next**, not here.
- **Chasing and shooting at once:** within range with Ruby in sight, he's in the upright **Aim**
  body while he keeps moving at chase speed and firing, exactly as officers fight today. The
  gun-down **Run** is only for closing in beyond range or when she's out of sight. Rejected:
  stopping to shoot, which would change how officers fight.
- **The officer is `popo.prefab`**, the old `Enemy.prefab` renamed in the editor (it kept its GUID,
  so every officer in `SampleScene` is already a popo). The model nests inside it as visuals only.
  The bare `HumanM_Model` prefab saved as a new `Enemy.prefab` is deleted.
- **Holding the gun** (decided 2026-10-21), in three parts:
  - *Attachment:* the gun is a child of the hand's prop bone, so it follows the hand. Done.
  - *Grip:* the pack's `Human@ObjectGripHands01` hand pose, closed around the gun, on a layer
    masked to the gun hand (the pack's Right Hand mask for officers, Left Hand for Ruby, whose
    pistol is in her left hand). It's laid over the body while the character holds a weapon and
    lifted when unarmed.
  - *Arm:* while aiming, Unity's built-in Humanoid hand IK reaches the gun hand out from its
    shoulder along the aim, so the arm points the gun at the target. Armed but not aiming
    (resting or running), the same IK holds the gun hand at a low-ready spot in front of the body,
    because the pack's unarmed walk and run swing that hand, and the gun in it, into the leg
    (decided 2026-10-21, after play-testing). Both are stand-ins: pistol-holding animations (idle,
    walk, run, aim) dropped into the slots replace them later, the way aim poses replace the
    twist.
- **Ruby moves onto the same piece, and gets the body turn too** (decided 2026-10-21): "she
  should use it. It will make her movements more realistic." Standing still, her chest follows the
  cursor and her feet step round past the limit. Her aim point, shots and whip still turn
  instantly; only her legs catch up. Her shooting pose gets a slot to fill later.

## Calls I made

These are mine, not the user's; each is open to correction.

- **A new assembly, `Ruby.Animation`** (`Assets/Scripts/Animation/`, no references), with an editor
  assembly beside it. It knows nothing about the game, like the other systems.
- **The game reports through an interface the animation assembly declares** (`IAnimatedCharacter`,
  name the builder's), which `EnemyController` and `PlayerController` implement. The animator
  *polls* it every frame, the way the tether polls `IGrappleUser`. Nothing pushes events at it.
- **Slots live in a data asset, and the Animator Controller is generated from it.** One
  `CharacterAnimationSet` asset per character type (Officer, Ruby) lists a clip for every slot. An
  editor command builds that character's Animator Controller from its set: parameters, layers, blend
  trees, transitions. Swapping in a real animation means dropping the clip into the set and
  rebuilding. Rejected: a hand-built base controller with Animator Override Controllers, because an
  override maps by *clip*, not by slot, so two slots sharing one stand-in (Rest and Run might both
  start as the same walk) couldn't later be replaced separately. Hand-building ~50 blend-tree cells
  per character is also the kind of Inspector work that drifts from the code's parameter names.
- **Visual facing is separate from the body's facing.** Today `EnemyController.FaceTarget()` snaps
  the whole officer to face Ruby. Facing is purely visual for officers (sight has no view cone,
  shots go from the muzzle to the target whichever way he faces), so the game code stays as it is,
  and the *model* lags: the animation piece keeps a lower-body yaw on the model child, twists the
  upper body toward the target up to the limit, and turns the lower body to catch up past it. The
  capsule, movement, pathing and sight don't change.
- **The gun is aimed after animation.** With the pistol on an animated hand bone, an aim set in
  `FixedUpdate` is overwritten as the hand moves. `GunHolder.AimAt` records the target, and the gun
  is turned flat toward it in `LateUpdate`, after the animator and the look-at have posed the hand,
  so the gun points where its shots go. `Lower` clears the target.
- **Getting up is a phase of `RagdollState`, not a new state.** Once the tumble settles, the officer
  is stood up (`Recover`) and holds still for **Get Up Time**, reporting "getting up"; the table moves
  him on to Aggro only after that (`RagdollState` exposes a bool, the table reads it, as elsewhere).
  He can't move, aim or shoot during it. A spinning top that winds down is already on its feet and
  goes straight on, with no get-up.
- **A hit at Knockdown Speed during the get-up knocks him down again.** He's on his feet by then,
  so the rule for anyone standing applies, rather than a window where nothing can floor him, a rule
  nothing else in the game has. Today's guards treat all of `RagdollState` as "already down", so
  `KnockDown` (relaunch the tumble), a spinning top's hit (`TakeHit`: shove him as a standing
  officer, not `RagdollBody.Push`) and `Alert` (ignored; he goes to Aggro once up anyway) must check
  the get-up phase.
- **Officers aim only within range.** Today `AggroState.Shoot` turns the gun toward Ruby whenever
  she's in sight, beyond range too. With the gun turned in `LateUpdate`, that would twist it off his
  hand during the gun-down Run. Beyond range he lowers it instead. Where he can fire is unchanged.
- **Ruby's stance is Aim whenever she's on her feet,** since she always faces the cursor; her
  Aim slot holds her current 8-direction run, and her turn slots `HumanF@Turn01_Left/Right`. She
  reports a move speed of 0 with no direction pressed, or the code would treat her as always
  moving and her legs would never lag. She looks at her aim point.
- **Ruby turns snappier than officers:** Twist Limit 60° and Turn Speed 720°/s to start, against
  70° and 360°/s, so her legs keep up with a fast cursor. Both are play-test starting points.
- **The staged `Speed`, `ChainState` and `Fire` parameters and the Hold/Throw upper-body states in
  `PlayerAnimator.controller` aren't carried over.** Nothing writes them; they come back as slots when
  the chain animations are designed.

## Pieces

### `Ruby.Animation` (new, `Assets/Scripts/Animation/`, references nothing)

- **`IAnimatedCharacter`**, implemented by the game. What the animator needs, each frame:
  - the body's velocity (its `Rigidbody`) and the speed it's trying to move at, so moving at its
    intended speed reads as full in the blend and standing still reads as zero;
  - its **stance**: Rest, Aim, Run, Restrained, Frozen, GettingUp;
  - what it holds: None or Pistol (TwoHanded reserved for later);
  - where to look and aim (a world point), or none.
- **`CharacterAnimator`**, a component on the model's GameObject (beside the `Animator`, so it gets
  `OnAnimatorIK`). It finds its `IAnimatedCharacter` above it. Each frame it:
  - writes the parameters: local move direction (relative to the lower body's facing), stance,
    weapon, and a turn value while turning in place;
  - keeps the lower body's yaw, turning it toward the look point at **Turn Speed** once the twist
    passes **Twist Limit**, and matching the body's facing while moving;
  - applies the look-at in `OnAnimatorIK` (body, head) toward the look point, clamped to the limit;
  - on Frozen, pauses the animator (speed 0) and resumes on leaving it. It never enables or disables
    the `Animator`: Ruby's `SkeletonRagdollBody` already switches hers off when she's downed, and the
    two must not fight.
- **`CharacterAnimationSet`** (ScriptableObject): the slots, grouped as stance × weapon.
  Directional slots hold nine clips (idle plus eight directions).

### `Ruby.Animation.Editor` (new, references `Ruby.Animation`)

- A menu command that builds or rebuilds a character's Animator Controller from its
  `CharacterAnimationSet`, with the IK pass on the layer that needs it. Rebuilding keeps the
  controller asset's GUID, so prefabs that use it stay wired.

### `Ruby.Game`

- **`EnemyController`** implements `IAnimatedCharacter`. Stance comes from its state:

  | State | Stance |
  | --- | --- |
  | Idle, Patrol, Searching | Rest |
  | Aggro, armed, Ruby in sight, in range and on her feet (he's firing) | Aim, looking at Ruby, whether moving or not |
  | Aggro, otherwise moving | Run |
  | Aggro, otherwise standing (holding with Ruby down, cornered) | Rest |
  | Rearm | Run (unarmed) |
  | Grabbed | Restrained |
  | Ragdoll, spinning upright | Restrained |
  | Ragdoll, tumbling | Frozen |
  | Ragdoll, getting up | GettingUp |
  | Dead | Frozen |

  `AggroState` exposes whether he's firing (armed, Ruby in sight, in range and on her feet) so the
  controller can read it. An unarmed officer's Rest and Run use the no-gun slots.
- **`EnemyController.EnterGrabbed`** drops the gun with a small pop: the body's velocity plus **Grab
  Gun Pop** up and outward, away from his body. `GunHolder` and `Gun` already keep it out of walls
  and on continuous collision.
- **`RagdollState`** gains the get-up phase; the transition to Aggro reads it. `KnockDown`,
  `TakeHit` and `Alert` treat the get-up phase as standing (see Calls).
- **`PlayerController`** implements `IAnimatedCharacter` for Ruby (Aim stance on her feet, looking
  at her aim point; Frozen while downed; move speed 0 with no direction pressed). `PlayerAnimator`
  is removed once Ruby runs on `CharacterAnimator`.

### `Ruby.Weapons`

- **`GunHolder`** aims in `LateUpdate` toward the target recorded by `AimAt` (see Calls).

## Slots and stand-ins

All clips are the pack's in-place versions (not the `RootMotion` folders).

| Slot | Officer stand-in (`HumanM@...`) | Ruby (`HumanF@...`) |
| --- | --- | --- |
| Rest, no gun / pistol (9 each) | `Idle01` + `Walk01_*` | not used |
| Run, no gun / pistol (9 each) | `Idle01` + `Run01_*` | not used |
| Aim, pistol (9) | `Idle01` + `Walk01_*` | `Idle01` + `Run01_*` (today's run), until an aim clip exists |
| Turn in place, left / right | `Turn01_Left`, `Turn01_Right` | `Turn01_Left`, `Turn01_Right` |
| Restrained | `Idle01` (pinned and wriggling later) | not used |
| Get up | `Idle01` (a real get-up later) | not used |

The crouched raid walk, low-ready run, upright aim, pinned wriggle and get-up are the clips to make
or buy; each drops into its slot.

## Assets: Inspector work for the user

**Done on 2026-10-21** (commit `7091cbd`):
- The officer is `popo.prefab` (renamed from `Enemy.prefab`, same GUID, so the scene's 18 officers
  and their patrol routes still point at it). Its root keeps the whole officer: capsule, Rigidbody,
  `EnemyController`, `GunHolder`, `Health`, `Weight`, ragdoll, Impact Damage and one grab point
  (`GrappleNode`) at (0, 0.5, 0). The capsule's mesh renderer is off.
- `HumanM_Model` nests directly under the root as `Model`, at (0, −1, 0), visuals only: no
  components added. Its Animator has root motion off and Culling Mode Always Animate, and no
  controller yet.
- The pistol sits on `B-handProp.R` at local (0, 0.044, 0.137), scale (1, 1, 1), and
  `GunHolder`'s Hand is that bone. `Hand` and `HandMesh` are gone.
- `Pistol.prefab` is hand-sized inside the prefab (about 0.3 of its old size), with its mesh,
  collider and muzzle centred on the gun's pivot, which aiming turns around and the chain's grab
  point and a re-arming officer use. Weight stays Small (20 kg).
- Lesson kept for later work: moving an officer's components onto another object makes them new
  components, and the scene's per-officer settings aimed at the old ones are silently dropped.

**Still to do, once the code exists:**
1. On popo's `Model`: assign the generated Officer controller to the Animator, and add
   `CharacterAnimator` with the Officer animation set.
2. The pack's clips: in-place versions, Loop Time on for locomotion, root motion baked into the
   pose. Check, don't assume.
3. `HumanM_Model`'s `.meta` holds no saved bone mapping; if its Rig tab's Configure shows a tick,
   the Avatar is valid.
4. `Red.prefab`: replace `PlayerAnimator` with `CharacterAnimator` on her model, with Ruby's set and
   her generated controller.

## Defaults

| Setting | Where | Default | Why |
| --- | --- | --- | --- |
| Twist Limit | CharacterAnimator | 70° officer, 60° Ruby | Inside the user's 60–90°; Ruby's a little tighter, to keep up with a fast cursor. |
| Turn Speed | CharacterAnimator | 360°/s officer, 720°/s Ruby | An officer's legs catch up in about a quarter second; Ruby's twice as fast. |
| Look-at weights | CharacterAnimator | body 0.8, head 1 | Most of the twist in the chest. |
| Move damping | CharacterAnimator | 0.1 s | As `PlayerAnimator` today. |
| Get Up Time | EnemyController | 1 s | Long enough to read; a real clip sets the final value. |
| Grab Gun Pop | EnemyController | 3 m/s up, 2 m/s out | A small hop clear of his hands. |

## Build order

One step at a time, with the user's play-test after each.

1. `Ruby.Animation`: the interface, `CharacterAnimator`, the animation set and the controller
   builder. Ruby moves onto it, looking identical until the twist arrives: the proof the piece
   works.
2. Officers on `HumanM_Model` (the model and pistol are already in place): stances from state,
   the gun aimed after animation.
3. The aim twist and the legs following.
4. Grab pop, Restrained, the get-up phase, freezing.
5. Docs: CLAUDE.md, the manual, this spec's "Changed in play-testing".

## Out of scope

- Limp ragdolls for officers (next branch).
- Hand-made aim poses, two-handed weapons, a pickup animation, Ruby's get-up, footsteps.
- Any change to how officers move, see, path or fight.

## Changed in play-testing

- **The officer prefab** became `officer.prefab` (renamed twice in the editor, from `Enemy.prefab`
  via `popo.prefab`, same GUID). An attempt to move the officer's components onto the model's root
  made them new components and silently dropped the scene's 320 per-officer settings, patrol
  routes included; popo was restored from the commit and the model added as visuals only.
- **The pistol** is hand-sized inside its prefab, centred on its pivot, and posed in the fist on
  `B-handProp.R`. Prefab Mode shows the T-pose, where a spot that looks like the palm is up the
  forearm, so the pose is set in Play mode and pasted into the prefab.
- **Holding the gun** gained the grip layer (the pack's object-grip pose through a hand mask) and
  the gun arm's IK; the carry hold came from the pack's unarmed walk swinging the gun into the leg.
- **The aim is a level heading, not a point:** the cursor's spot at Ruby's root height pitched her
  head and spine down into her body. The heading holds within 1.5 m of the character and eases over
  0.08 s; a turn-around past 150 degrees goes the gun hand's way and keeps its direction until the
  aim comes back within 90 degrees.
- **The arm's targets** are rebuilt each frame and only the blend, the weight and the arm's angle
  are eased: easing a world position trailed a running Ruby and cut through her chest on a turn.
- **CharacterAnimator runs after the game's scripts** (DefaultExecutionOrder 100), and leaves the
  model alone while not upright: placing it before Ruby's body turned let the turn carry it off for a
  frame, and resetting it on a knockdown would snap her live ragdoll round.
- **Ruby's model:** the School Girl FBX's skeleton root sits 2.42 m off its origin; on `Red` it's
  zeroed so Prefab Mode shows her inside her capsule. Her CharacterAnimator is left-handed, Twist
  Limit 60, Turn Speed 720.
- **Officers' looks:** Enemy.mat colours the model pink.
- **From the final review:** the look-at's weight and yaw ease over 0.1 s, so the head and chest no
  longer snap when a target appears or is lost; root motion is discarded (an empty
  `OnAnimatorMove`), since Ruby's Animator had Apply Root Motion on; and `RagdollState.Standing`
  keeps an officer who has just finished getting up knockable until the table sends him hunting.
- **A shot snaps the arm onto the cursor.** The pull puts Ruby's heading, head, chest and gun arm
  exactly on the cursor in that frame's pose, and the bullet leaves the muzzle in `LateUpdate`, once
  she's posed. Her trailing arm had made it unclear where a bullet would come from or go. Tried and
  dropped first: spawning on her aim line when the muzzle was off (bullets must leave the muzzle), and
  a Hotline Miami turn-around, a 0.08 s spin when the cursor crossed her plus a wait of up to 0.3 s
  for the arm before firing (clicks felt late).
- **Past Twist Limit the chest drags the legs** instead of stopping to wait for the feet, so the
  upper body always reaches the aim. That retired the rule keeping a turn's direction until the aim
  came back within 90 degrees: the aim can no longer get round behind the legs.
- **The turn slots are empty.** The pack's `Turn01` clips bake their turn into the pose; blended in
  at whatever phase the aim state was in, they turned the hips on top of the model's own turn and
  snapped it away when the feet settled, and the legs jumped. A turn on the spot needs a foot-shuffle
  clip that doesn't turn the body.
