# Impact damage (slice 5)

Status: approved and built 2026-10-08 (all three pieces). Supersedes nothing; this is the slice the weight system
(real kilograms) was built for.

## Intent

A Hotline Miami prototype: enemies die quickly, and Ruby's whip turns bodies and props into
weapons. **How fast something hits decides everything**: whether the target is knocked down and how
much it's hurt. One formula covers every hit, so the game stays readable: a swung enemy smashed into
a wall, a thrown crate, a spinning top, and anything fast hitting Ruby.

- j-quote: "there needs a be a certain amount of speed that ragdolls enemies. This speed will also be able to ragdoll the player. If this happens the player usually dies, as the speed of an object that can move the player should be fatal."
- j-quote: "Damage will be dealt by how fast an object hits another. When spinning an enemy around you, collision will detect how fast they're moving and deal damage based on what they run into. [...] If a crate is flying at an enemy, the speed should determine if the enemy even gets ragdolled. Enemies and players alike can get ragdolled by any weight item if its moving at an adequate speed. We only need a consistent damage formula."

## The formula

```text
impact speed = closing speed along the hit: Unity's collision.relativeVelocity, along the contact normal
below Knockdown Speed: nothing happens
otherwise:
    damage    = Damage Scale x impact speed x sqrt(hitter mass / Reference Mass)
    hitter    = the other body; if it is fixed (no body, or kinematic), your own mass
    knockdown = ragdoll, unless held on the chain or already ragdolling
```

- **Speed decides whether a hit counts**, for any weight: a fast tiny crate still knocks you down.
  **Weight scales the damage** gently (square root), so a fast huge crate hurts more.
- **Any fast hit counts, walking included** (decided over "loose bodies only"). Ruby walks at 15
  m/s and enemies chase at about 10, so Knockdown Speed must sit above 25 or bumping hurts.
- **Damage is big from the threshold** (decided over ramping from zero): a hit just over Knockdown
  Speed already does a large share, so "fast enough to knock you down usually kills" holds.
- **Both sides are hurt.** Each character in a hit applies the formula to itself; walls and props
  have no health, so they only deal damage.
- **Once per contact**: `OnCollisionEnter`. A body pinned against a wall isn't damaged every step;
  each fresh bounce is a new hit.
- `relativeVelocity` is measured before the solver absorbs the hit, so no velocity recording is
  needed (the spinning-top exchange still records its own, because it needs each body's velocity).

## Defaults

| Setting | Value | Note |
| --- | --- | --- |
| Knockdown Speed | 26 m/s | above walking + chasing (15 + 10) |
| Damage Scale | 1 | |
| Reference Mass | 60 kg | Ruby's weight |
| Enemy HP | 20 | was 100 |
| Ruby HP | 30 | low: a knockdown-speed hit usually kills her |

Examples: a 50 kg enemy at 26 m/s deals 23.7 (kills an enemy, nearly kills Ruby); a 10 kg crate at
26 m/s deals 10.6, at 60 m/s 24.5; a swung enemy at 40 m/s head-on into a wall takes about 36.
Swings only hurt once Spin Speed exceeds Knockdown Speed.

## Pieces

### Core (knows nothing about this game)

- **`ImpactRules`** (ScriptableObject): Knockdown Speed, Damage Scale, Reference Mass, and the
  formula. One shared asset keeps every hit consistent.
- **`ImpactDamage`** (component; needs Rigidbody, Health): `OnCollisionEnter` measures the impact,
  applies the formula, calls `Health.ApplyDamage`, and on a knockdown calls `IKnockdown.KnockDown()`
  on its own object.
- **`IKnockdown`**: `void KnockDown()`. Whoever owns the body decides what being knocked down means.

### Game

- **Enemy**: `EnemyController` implements `KnockDown`: ragdoll at its current velocity, get up into
  Aggro if alive. Held on the chain or already ragdolling, it takes damage only, so a swung enemy
  keeps swinging. Death uses the existing Dead state (destroys the enemy until corpses exist).
  The spinning-top momentum exchange stays, but no longer always knocks down: the threshold
  decides, so a slow graze just shoves.
- **Ruby**: `PlayerController` implements `KnockDown`: she ragdolls (a `CapsuleRagdollBody`),
  movement, aim, gun and grapple stop, and any held chain lets go (`IGrappleUser.CanAct`). Alive,
  she gets up once settled; dead, she stays down (no restart yet).

## Build order

1. Core pieces, enemy damage and knockdown.
2. Ruby's knockdown and death.
3. Corpses (dead enemies stay as dimmed, grabbable bodies that can be walked over once settled).
   Built: a death mid-swing stays on the chain; a settled corpse is frozen with trigger colliders,
   so nothing collides with it; grabbing wakes it; a corpse takes no more damage.
   - j-quote: "A dead enemy darkens but is still grabbable. If enemies die while being spun by the player, they stay grabbed, but turned darkened. Darkened enemies have no collision while settled on the floor, but can be grabbed."

## Inspector work

Create one ImpactRules asset; add ImpactDamage (rules assigned) to the Enemy prefab and the Player;
add Health (30) and CapsuleRagdollBody to the Player; set the Enemy prefab's Health to 20.
