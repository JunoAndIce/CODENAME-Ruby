# Animation

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Animation

Spec: `docs/specs/2026-10-20-enemy-animations.md`. `Ruby.Animation` animates any character without
the game naming a clip, state or parameter. The game implements **`IAnimatedCharacter`** (its body,
the speed it's trying to move at, a stance, a weapon and a target), and **`CharacterAnimator`**, on
the model beside its Animator, polls it every frame, the way the tether polls `IGrappleUser`. Its
`Update` runs after the game's scripts (`DefaultExecutionOrder(100)`): `PlayerController` turns
Ruby in its own `Update`, and placing the model first let that turn carry it off for a frame.

- **Stances** (`AnimStance`, numbered, saved): Rest (weapon at rest: patrolling, searching, idle),
  Aim (firing at Ruby, moving or not; Ruby whenever she's on her feet, since she always faces the
  cursor), Run (closing in out of range or out of sight, re-arming), Restrained (held on the
  chain, alive or dead), Frozen (thrown, knocked down, dead: the Animator pauses at speed 0, never disabled,
  because Ruby's ragdoll switches hers off and on itself) and GettingUp. `EnemyController.Stance`
  maps its state; `PlayerController` reports Aim or Frozen.
- **Slots, not clips:** a **`CharacterAnimationSet`** asset per character holds a clip per moment:
  nine-direction sets for Rest, Run and Aim (unarmed and pistol), turn-in-place left and right,
  Restrained, Get Up, and a grip pose with its hand mask. **Build Controller**
  (`CharacterControllerBuilder`) generates the Animator Controller from it: the parameters (named in
  `CharacterAnimator`'s constants, so they can't drift), a free-form directional blend tree per
  filled slot, the Aim idle cell as a turn blend, Any State transitions on stance and weapon, the IK
  pass, and a Grip layer masked to the gun hand. Rebuilding refills the same asset (same GUID) and
  destroys its old sub-assets. Most slots hold stand-ins from Kevin Iglesias's pack until real
  animations exist; swapping one is an edit to the set and a rebuild, never code.
- **Beats** (`AnimBeat`; the game side is `EnemyBeat`, see `enemy-ai.md`). Four slots, Draw, Notice, Lose and
  Stand Down, and an Upper Body Mask. Build Controller puts Notice, Lose and Stand Down on a full-body
  **Beat** layer (they plant the feet) and the Draw on a **Beat Upper** layer under the mask, so the legs
  coast to a stop beneath it; a Draw with no mask is left out with a warning, as the grip is without its
  mask. Both sit under the Grip layer, so the hand still closes round the gun. Each state is **scrubbed**
  by the `BeatTime` parameter, the beat's progress from `IAnimatedCharacter.BeatProgress`, not played, so a
  clip of any length fills the tuned time. `CharacterAnimator` opens a layer while the running beat has a
  state on it. `IAnimatedCharacter.Beat` and `BeatProgress` have default bodies (none), so Ruby implements
  nothing. **An empty slot is a rule, not a fault:** the stance holds while the officer turns; the set's
  Inspector lists its empty slots and the officer's lists the beats their built controller lacks
  (`BeatSlots`), and with `RUBY_DEBUG_LOGS` a beat with no clip says so once.
- **The legs follow the body.** The body turns at once, as the game wants; the model keeps its own
  facing while upright. Standing, the head and chest twist toward the aim (Humanoid look-at, chest
  weight 0.8, eased over 0.1 s) up to Twist Limit. Past it the chest drags the legs along rather
  than stopping to wait for them, so the upper body and the gun arm always reach the aim, and the
  feet step round the rest of the way at Turn Speed. Moving, the legs swing to the aim (or the
  body's facing) at Turn Speed. A turn-around past 150 degrees goes the gun hand's way, so the arm
  never crosses the chest. Held, tumbling or getting up, the model is left alone: a ragdolled
  character's live bones hang under it.
- **Turn clips must be feet only, so the turn slots are empty.** Kevin Iglesias's `Turn01` clips
  bake their turn into the pose. Blended in at whatever phase the aim state was in, they turned the
  hips on top of the model's own turn and snapped it away when the feet settled, and the legs
  jumped (in a frame, at Ruby's blend damping of 0). A turn on the spot needs a foot shuffle that
  doesn't turn the body.
- **Root motion is discarded** (an empty `OnAnimatorMove`), whatever the Animator's Apply Root
  Motion says: where the model stands is its body's business, and how it faces is
  `CharacterAnimator`'s.
- **The aim is a level heading, never a point,** from the body to the target, held within 1.5 m and
  eased over 0.08 s. The cursor's spot at Ruby's root height pitched her head and spine down into
  her body; close to her a slight mouse move is a big turn, and the camera's lean keeps the point
  under the cursor drifting. Only the pose uses it: her body, shots and whip still turn instantly.
- **The gun arm** (Humanoid hand IK, a stand-in for pistol-holding animations): aiming, the gun
  hand reaches out from its shoulder along the aim; armed but not aiming, it holds the gun at Carry
  Point, a low-ready spot in front of the body, because the pack's unarmed walk swings it into the
  leg. Both points are rebuilt each frame from where the body is, and only the blend, the weight and
  the arm's direction (as an angle round the shoulder) are eased: easing a world position left the
  hand trailing a running Ruby and cut through her chest on a turn. The Grip layer closes the gun
  hand while armed, driven from code because an empty "open" state would write the hand's default pose.
- **A shot snaps the arm onto the target.** `GunController` calls `CharacterAnimator.SnapToTarget`
  in `Update`, which puts the heading, the head and chest and the gun arm exactly on the cursor in
  that frame's pose, and spawns the bullet in `LateUpdate`, after the Animator has posed her. Every
  shot leaves the barrel and flies along it to the cursor, on the click. Between shots the arm keeps
  its easing: a shot from wherever the trailing muzzle had got to left from her side or behind her,
  and the player couldn't tell where a bullet would come from or go. A wait for the arm, and a fast
  spin when the cursor crossed her, were tried first and dropped: clicks felt late. The arm points
  along the aim from its shoulder, so close in a bullet runs a few degrees inward of the barrel.
- **Settings:** officers Twist Limit 70, Turn Speed 360, right hand; Ruby 60, 720, left hand (her
  pistol is in her left hand).
