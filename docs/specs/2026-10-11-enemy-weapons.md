# Enemy weapons: officers' pistols

Status: approved design 2026-10-11, not built. It supersedes section 7 (Weapons) of
`2026-09-29-enemy-states-v2.md`. Melee from that section is deferred to a later pass.

## Intent

Officers get a real attack. Today it's a placeholder that is switched off: Attack Range is 0 on the
Enemy prefab, and `EnemyController.Attack()` only logs. Each officer carries a **pistol**, using the
model `Assets/Models/Weapons/pistol.fbx` from the Models merge. They shoot Ruby in Hotline Miami
style: shots are fatal, so dodging and cover matter. Officers can lose their pistol, and a disarmed
officer goes looking for one.

- j-quote: "Ruby's pistol has unique properties on her bullets. She has a pistol but it's not the
  same as the enemies and the enemies can drop theirs, Ruby cannot."
- j-quote: "We will use the pistol as guns for enemies."

**Ruby's revolver is out of scope.** It is a different gun with its own bullet behaviour, still to
be designed. Its `BulletController` deals no damage today, and that stays as it is until its own
pass.

## Decisions

The user chose each of these:

- **One shot kills Ruby.** Damage is 30 against her 30 health, set on the gun so it's easy to soften.
  Rejected: two- or three-shot kills.
- **A visible bullet with no wind-up.** It fires as soon as the cooldown allows, and at 30 m/s (twice
  Ruby's walk) a sidestep can dodge it.
  - Rejected: an aim wind-up with a tell, and an instant hit after a wind-up.
  - If playtests find first-sight shots unfair, the lever is a reaction delay, not a wind-up.
- **Shooting on the move.** Armed officers keep chasing and fire whenever they can, which follows the
  two-axis rule: attacking happens alongside the hunt, not as a state. Rejected: stopping to shoot
  (today's placeholder) and holding a preferred distance.
- **Officers drop their pistol** when the chain grabs them, when knocked down (any knockdown,
  including from spinning tops), and when they die. Not chosen: whipping the gun out of a hand, so a
  held gun has no grab point.
- **A disarmed officer goes and gets a gun.** It heads for the nearest free dropped pistol it can
  reach and picks it up. With none in reach it keeps chasing unarmed, with no attack until melee
  exists. Rejected: staying unarmed for good, and fleeing.
- **A dropped pistol is a grabbable prop.** Ruby can whip it, reel it, throw it, or yank it away so
  an officer can't re-arm. She can never pick it up or fire it. Rejected: scenery only.
- **What stops a bullet:** walls, Immovable objects, crates and props, Ruby, and **whatever Ruby
  holds on the chain**, including a held officer or corpse, so she can swing a body as a shield.
  **Free officers and resting corpses don't stop bullets**, so there's no friendly fire. Only being
  on the chain makes an officer, alive or dead, stop bullets. A knocked-down officer or a tumbling
  corpse lets them through too.
- **Weapons are their own assembly**, `Ruby.Weapons`, like Pathing, Grapple and CameraRig.
  - Rejected: weapons inside `Ruby.Game`, which would tie them to this game's enemy.
  - Rejected: physical Rigidbody bullets using the impact-damage formula. A light bullet at 30 m/s
    does almost nothing under it, anything at 25 m/s or more counts as a knockdown, and the bullets
    would shove props. Bullets keep their own rule instead: fixed damage on hit.

## Pieces

### `Ruby.Weapons` (`Assets/Scripts/Weapons/`, references `Ruby.Core` only)

It knows nothing about officers, the chain or Ruby.

- **`Gun`** (MonoBehaviour on the pistol prefab):
  - **Tuning:** Damage, Bullet Speed, Fire Cooldown, Range, Spread (± degrees), Bullet Lifetime, the
    bullet prefab, and a Muzzle transform.
  - **Two modes.** Held: parented to a hand, Rigidbody kinematic, colliders off. Dropped: unparented,
    Rigidbody dynamic, colliders on, carrying the velocity it was dropped with.
  - **Dropped-only objects:** a list it switches on when dropped and off when held. This is how the
    grab point exists only on the floor, without Weapons referencing Grapple.
  - **A static list of dropped guns,** so holders can find one, and `IsResting` (moving slower than
    about 1 m/s).
  - It sets its body kinematic itself after `Weight`'s Awake, which forces non-Immovable bodies to
    be dynamic.
- **`GunHolder`** (MonoBehaviour on a character), a hand that can hold one gun:
  - **Hand transform** and a starting gun.
  - **`IsArmed`**, **`PickUp(gun)`** and **`Drop(velocity)`**.
  - **`TryFireAt(point)`:** checks range and cooldown, turns the held gun to face the point (flat),
    and fires.
  - **`FindLooseGun(radius, isFree)`:** the nearest dropped, resting gun within the radius that
    passes a check the caller supplies.
- **`Bullet`** (pooled through `ObjectPoolManager`):
  - **Flight:** moves at the gun's speed. Each physics step it sweeps a 0.05 m sphere from its last
    position to its next one, ignoring triggers, so a fast bullet can't skip through a thin wall.
  - **Hits, nearest first:**
    - its own shooter: ignored;
    - a body whose `IBulletTarget.StopsBullets` is false: passed through, and the sweep continues;
    - anything else: the bullet stops there.
  - **Damage:** on stopping, a `Health` on the hit body takes the damage. If that kills it, its
    `IKnockdown` is called.
  - It returns to the pool when it stops, or after its lifetime runs out.
  - It never pushes anything.
- **`IBulletTarget`:** `bool StopsBullets { get; }`. Anything that doesn't implement it is solid.

### `Ruby.Game`

- **`EnemyController`:**
  - Gets a `GunHolder`, plus a Rearm Radius (default 10 m).
  - Implements `IBulletTarget` as `StopsBullets => IsHeld`, which covers a live officer or a corpse
    on the chain.
  - Drops the gun with the body's velocity in `EnterGrabbed` and `KnockDown`. `DeadState.Enter`
    drops it if the officer is still armed.
  - **The placeholder goes:** `Attack()`, `_attackRange` and `_attackCooldown`, with their gizmo.
    The gun owns range and cooldown. Range moves to a gizmo drawn from the held gun.
- **`AggroState`:**
  - Always chases along the shared field, as now.
  - Each physics step it also calls `GunHolder.TryFireAt(player)` when armed, Ruby is in sight, and
    she isn't down (`PlayerController.CanAct`). Nobody shoots a body.
  - The body faces its direction of travel, and the held gun turns toward Ruby: officers run and gun.
- **`RearmState`** (new AI state, `EnemyState.Rearm`). It *is* a state, because fetching a gun
  replaces the hunt:
  - **Enter:** chooses the nearest **free** gun within Rearm Radius. Free means dropped, resting,
    not on Ruby's chain (the gun's `GrappleNode.IsOccupied`, checked here on the game side) and not
    already claimed by another officer.
  - **Walk:** to it with `MoveToArea` at chase speed. Within 1 m it calls `PickUp`.
  - **Losing the gun:** if it starts moving, is taken, or comes back Unreachable, the officer drops
    that target and chooses again.
  - **Exposes:** `Armed`, and `GaveUp` (no free gun left).
- **Transitions,** in `BuildTransitions`:
  - Idle, Patrol, Searching or Aggro → Rearm, when unarmed and a free gun is within Rearm Radius.
  - Rearm → Aggro, on `Armed` or `GaveUp`. It always hands back to Aggro: an officer only loses
    its gun to Ruby, so it knows she's around. Aggro Time still sends it searching if she's gone.
  - Grabbed, Ragdoll and Dead interrupt it as they do every AI state.

### Assets: Inspector work

Prefabs and scenes are YAML, so the user builds these:

1. **Pistol prefab:**
   - `pistol.fbx`, scaled to an officer's hand.
   - A Rigidbody, a Box Collider, a `Weight` (Custom, 1 kg) and `Gun`.
   - A **Muzzle** child.
   - A **GrappleNode** child, **left inactive** and listed in the Gun's dropped-only objects. A node
     caches its body and grabbable the first time it wakes, so one that woke under an officer would
     make grabbing the dropped gun grab the officer.
2. **EnemyBullet prefab:** a small visible mesh plus `Bullet`. No collider or Rigidbody, since it
   sweeps.
3. **Enemy prefab:**
   - A **Hand** child at gun height.
   - A `GunHolder` on the root, with a Pistol instance under the Hand as its starting gun.
   - The pistol has no active collider while held, following "characters collide with their root
     capsule only".
4. **Main Camera:** remove the missing-script component left by Cinemachine's removal.

## Defaults

| Setting | Where | Default | Why |
|---|---|---|---|
| Damage | Gun | 30 | Ruby has 30 health: one shot kills. |
| Bullet Speed | Gun | 30 m/s | Twice Ruby's 15 m/s walk, so it's dodgeable from mid-range. |
| Fire Cooldown | Gun | 0.8 s | Steady pressure without a wall of bullets. |
| Range | Gun | 10 m | Inside the 12.5 m Alert Radius: officers spot Ruby, then close in while firing. |
| Spread | Gun | ±2° | Enough that a moving Ruby isn't always hit. |
| Bullet Lifetime | Gun | 2 s | Strays return to the pool after 60 m. |
| Dropped weight | Pistol's Weight | Custom 1 kg | A full throw (about 118 m/s) does about 15 damage: it knocks an officer down, and so disarms them, but doesn't kill one at 20 health. |
| Rearm Radius | EnemyController | 10 m | Officers go for nearby guns, not ones across the map. |

## Build order

One step at a time, with a check-in after each:

1. **`Ruby.Weapons`** (Gun, Bullet, GunHolder, IBulletTarget, and the assembly file referenced by
   `Ruby.Game`), plus armed officers shooting on the move in Aggro. The placeholder attack goes.
   *Play-test:* officers shoot while chasing, walls and crates block shots, and one hit drops Ruby.
2. **Drops,** and the dropped gun as a grabbable, throwable prop. *Play-test:* grabbing, knocking
   down or killing an officer drops the gun, and a held officer soaks a bullet.
3. **`RearmState`.** *Play-test:* a disarmed officer fetches the nearest gun and gives up if Ruby
   yanks it away.
4. **Docs:** CLAUDE.md (the assembly and interface tables, enemy AI), this spec's status, the
   enemy-states spec's progress list, and the manual's weapons sections and PDF.

Verification is the compile check (`python tools/compile_check.py`, which checks the new assembly
on its own) plus playing the scene. There are no automated tests.

## Out of scope

- Ruby's revolver bullets.
- Melee weapons.
- Gunshots alerting enemies, which waits for the sound system.
- Ammo and reloading for officers.
- Muzzle flash and other visual polish.
- The police boss.
