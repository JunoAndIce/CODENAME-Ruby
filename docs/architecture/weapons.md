# Weapons and pooling

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Weapons

Spec: `docs/specs/2026-10-11-enemy-weapons.md`. Officers carry pistols (`Gun`, held by a
`GunHolder`). Ruby's revolver (`GunController`, `BulletController`) is a different gun and isn't
part of this system. For now it wears the officers' pistol model: a bare mesh (`EnemyPistol`, with
no `Gun`, Rigidbody or collider, which would make it a loose prop and drop it) under her left hand
bone, `Wrist_L`, so it follows her animation standing and her forearm when she's down. Its `Muzzle`
child is the Fire Point and carries the `GunController`, which finds the `PlayerController` above
it. Shots fly flat from the muzzle toward `AimPoint`, never along the muzzle's own facing: that
swings and tilts with her animated hand, and a bullet flown along it would hit the floor. The pull
snaps her arm onto the cursor first, so the muzzle is on the line when the bullet leaves (see
**Animation**).

- **Bullets** (`Bullet`, pooled) sweep a 0.05 m sphere along each physics step, so a fast bullet
  can't skip through a thin wall. They stop at the nearest thing that stops bullets: anything
  solid, unless its `IBulletTarget` says no. Enemies stop them only while held on the chain (a
  shield), so there's no friendly fire. A bullet does the gun's fixed Damage (30, so one shot kills
  Ruby) and knocks down what it kills. Then it lands the gun's Knockback (240 kg·m/s) on whatever
  takes it (`IKnockback`, only Ruby): after the knockdown, so a killing shot hits her ragdoll, at
  the limb it struck (`IRagdollBody.ApplyHit`), and a shot she survives shoves her about 4 m/s.
  Enemies take no push, so a held shield doesn't budge. It isn't impact damage: under that formula
  a light bullet at the pistol's 40 m/s would do almost nothing.
- **Held vs loose:**
  - A held gun is kinematic with its colliders off (characters collide with their root capsule
    only), and its **interpolation off**. Interpolation writes the body's own physics history over
    its transform each frame, so a held gun drifted off the hand.
  - A loose gun is a prop (class Small, 20 kg, so a full throw kills an officer). Its grab point is a child listed in Dropped Only, switched on only
    while loose, so the chain takes guns off the floor but never out of a hand.
  - `GrappleNode` resolves its Host and Grabbable on every enable, not once on wake. A gun's grab
    point first wakes in an officer's hand, and resolving it there made grabbing the dropped gun
    grab the officer.
- **The holder:**
  - `GunHolder` finds its starting gun anywhere under the character. `Gun` counts itself held
    whenever a holder is above it, and the two must agree, or a held gun is never fired.
  - It records the starting gun's pose relative to the Hand, so a gun picked up later sits exactly
    where the starting one did.
  - It warns when the starting gun isn't under the Hand, when the Hand holds a Rigidbody with no
    Gun, and when the Hand's scale isn't uniform. Aiming turns a gun off the Hand's axes, and under
    a non-uniformly scaled Hand Unity shears it.
  - The officer's Hand is their model's right hand bone, `B-hand.R`, with the pistol a direct child
    of it, posed in the fist. Hand and pistol parent must be the same bone: the pistol first sat on
    the prop bone under it, `B-handProp.R`, while the Hand pointed at `B-hand.R`. Pose it in Play
    mode and paste the values into the prefab: Prefab Mode shows the T-pose, where a spot that looks
    like the palm is up the forearm.
  - `AimAt` only records the target; the gun is turned toward it in `LateUpdate`, after animation,
    because a turn made in `FixedUpdate` was overwritten as the animated hand moved. `Lower` releases
    it to follow the hand again.
- **Drops:** `KnockDown` and `DeadState.Enter` drop the gun with the body's velocity, so a knockdown
  sends it off at the hit's speed. `EnterGrabbed` pops it out instead: a small hop, Grab Pop Up and
  Grab Pop Out away from their body, to sell the grab.
  - The holder lowers it first, and the gun restores its true world scale (read at Awake) once it
    leaves the hand, so a drop mid-aim can't keep a shear as a stretched scale.
  - It's dropped from about a metre in front of the officer, so `Gun.KeepOutOfWalls` casts from the
    holder's centre to the gun's far end and pulls it back toward the officer until it clears
    anything solid. Otherwise one dropped facing a wall started inside it.
  - A loose gun uses continuous collision detection, since a knockdown drop is fast enough to pass
    through a wall in one step; a held one goes back to discrete before turning kinematic.

#### Pooling

`ObjectPoolManager` is a static API backed by an instance that must exist in the scene (the "Pooler"
object) — its `Awake` creates the dictionaries, so calling `SpawnObject` before it runs throws.
Pooled objects **return themselves** (`BulletController` on lifetime expiry). Enemies are *not*
pooled: a dead one stays as a corpse, so pooling them would need `Health` to reset and a corpse to
be cleared.
