# Sight through the cone only

**Status:** design, awaiting the user's approval. Roadmap step 2 (`docs/architecture/project-history.md`).

## Intent

The user, 2026-11-19: "Enemies vision will only be from the cone from now on. This means body noticing
radius, loose gun noticing radius, fast object radius, all get removed and detection is only handled
through the cone the enemies have." Then, asked whether Close Radius goes too: "Close radius does not go."

Officers should only see what is in front of them. Today a thrown body is noticed all round out to 20 m,
and a corpse out to 15 m in the cone or 2 m behind them. That makes them feel like they have eyes in
the back of their head, which works against sneaking up and against reading where they are looking.

## What changes

- **One test for every visual notice of a thing:** inside the cone (View Angle, centred on their
  facing), within View Range, with a clear line. That covers corpses, loose guns, moving and fast
  objects, an enemy on the chain, an enemy seen killed, and an enemy seen thrown.
- **Removed:**
  - **Motion Radius** (prefab 20 m, all round). Moving bodies are swept out to View Range and must
    be in the cone.
  - **Body Notice Range** (prefab 15 m). Corpses use View Range.
  - **The close circle's part in noticing things.** A corpse or gun right behind them goes unseen.
- **Kept:**
  - **Close Radius, for Ruby only** (the user's call). She can't stand right behind an officer
    unnoticed.
  - **Hearing and word of mouth.** A thrown body they don't see still makes an Impact sound when it
    lands, so they hear it.
  - **Aggro's all-round contact with Ruby** once they have seen her. That's tracking her, not
    noticing her.
- **Ranges move to View Range** (prefab value).
  - Corpses: from 15 m down to View Range.
  - Moving bodies: from 20 m all round to View Range in the cone.
  - Guns already use View Range.

## Approaches

There is one sensible way:
- `EnemyVision` gets a cone-only test (`InCone`) beside `InView`, which stays the cone plus the
  close circle and is used only for Ruby.
- Every `EnemySenses` scan calls `InCone`.
- The motion sweep's radius becomes View Range.

Keeping the radii but setting them to View Range was dropped. It leaves dead tuning in the Inspector
that someone could raise again, against the rule.

## Calls of mine (the user can correct these)

- **Search coverage keeps the close circle** (`SearchCoverage`). Coverage counts floor a searcher has
  looked at as covered, and ground right beside their path is passed by, not noticed. Cone-only
  coverage would make searchers stop and turn to clear spots a step away, and searches would run longer.
- **A tumbling enemy in the cone still counts at any speed**, as today: one knocked down or killed may
  hardly move.

## Left to the builder

- Remove the two fields from `EnemyController`, its gizmo spheres, and every mention in tooltips and
  docs (`enemy-ai.md`, the 0.2 spec's tables).
- No `FormerlySerializedAs` is needed, since the values are dropped, not moved. The prefab keeps
  stale keys until it is next saved, which is harmless.

## Verification

- Compile check.
- In play, with a scripted check, confirm each of these with an officer facing away and then facing
  it:
  - a corpse 1 m behind
  - a gun 1 m behind
  - a body thrown past behind at Alarm Speed (heard when it lands, not seen)
  - Ruby 1 m behind (seen: close circle)
