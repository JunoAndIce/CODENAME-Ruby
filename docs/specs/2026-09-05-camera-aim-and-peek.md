# Camera — Cursor Lead, Drag, and Shift-to-Peek

**Date:** 2026-09-05
**Branch:** `movement-camera`
**Status:** Designed, not implemented

## Context

The Cinemachine rig is in place: `CinemachineBrain` on Main Camera, a `CinemachineCamera`
with `CinemachineFollow`, `BindingMode: 4` (World Space) and `FollowOffset (0, 18, 0)`,
rotation fixed at `(90, 0, 0)` with no Rotation Control and no input axis controller. It
follows the player rigidly and the player cannot rotate it.

What's missing is feel. Hotline Miami's camera does two things this one doesn't:

1. **Leads toward the cursor**, with lag — the view drifts where you're aiming rather than
   centring on the player, and it catches up rather than snapping.
2. **Extends further on Shift**, letting you scout ahead of where you stand, while never
   losing sight of yourself.

The data needed for the first already exists and is thrown away: `PlayerController.MovePlayer()`
raycasts the cursor onto the ground plane as `pointToLook` and uses it only for `LookAt`.

## Design

### 1. Expose the aim point

`PlayerController` gains `_aimPoint` and `public Vector3 AimPoint => _aimPoint`, assigned in
**both** control-scheme branches so the camera never has to know which is active:

- **Mouse** — the existing `pointToLook`, flattened to the player's Y.
- **Gamepad** — no cursor exists, so synthesise one: `transform.position + transform.forward *
  _gamepadAimDistance`. Constant range, rotating with the stick. This is the standard
  twin-stick answer, and it is genuinely different from the mouse feel.

Three things will consume `AimPoint`: the camera lead, slice 2's hover indicator, and an aim
reticle if one is ever drawn. That's the argument for promoting it out of a local.

### 2. Focus proxy provides lead and drag

The `CinemachineCamera` stops following the Player and follows a **Camera Focus** empty
GameObject instead. A new `CameraFocus` component positions it each `LateUpdate`:

```csharp
Vector3 anchor = _player.transform.position;

Vector3 lead = (_player.AimPoint - anchor) * _leadFraction;
lead.y = 0f;
lead = Vector3.ClampMagnitude(lead, MaxLead);

transform.position = Vector3.SmoothDamp(transform.position, anchor + lead, ref _velocity, _damping);
```

`_damping` **is** the drag — the focus lags the cursor and catches up, so whipping the mouse
does not snap the camera.

A proxy rather than a Cinemachine extension because it composes: the grab camera's
`CinemachineTargetGroup` can take either the proxy or the player as a member without new code,
and it never has to be reconciled with Group Framing.

**Set `CinemachineFollow`'s Damping to 0.** Two layers of smoothing — here and in Cinemachine —
feel mushy. The focus owns the lag; the rig tracks it rigidly.

### 3. Shift to peek

Add a **`Peek`** action bound to `<Keyboard>/leftShift`. Do **not** reuse the existing `Sprint`
action, which is already on Left Shift — sprinting is a plausible future mechanic and sharing
one action makes them impossible to separate later. Two actions can share a binding.

Holding Peek moves two values toward their extended targets, both smoothed so the transition
is a glide rather than a jump:

| Value | Normal | Peeking |
|---|---|---|
| `_leadFraction` | ~0.35 | ~0.8 |
| `CinemachineFollow.FollowOffset.y` | 18 | ~26 |

Raising the camera is what makes the peek safe, which is the next section.

### 4. Keeping the player in view, by construction

Do not serialise a magic `_maxLead`. Derive it from what the camera can actually see:

```csharp
float halfHeight = followOffset.y * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
float MaxLead    = halfHeight * (1f - _playerMargin);
```

At the current `FollowOffset.y` of 18 and a 60° lens, `halfHeight` is **10.4** units, so a
`_playerMargin` of 0.25 caps the lead at **7.8** — the player can never be pushed closer than
a quarter of the half-frame to the trailing edge.

This is why peek raises the camera rather than only extending the lead: a higher camera has a
larger `halfHeight`, which raises `MaxLead` in the same proportion. **The extra look distance
is paid for by a wider frame**, so the player stays visible without a separate clamp,
regardless of lens, height, or aspect. Vertical is the binding constraint since it is the
smaller of the two screen extents.

### 5. Interaction with the grab lock

While `PlayerHoldingState` is active the group camera takes priority and frames player plus
grabbed enemy. Peek should be **suppressed** during a hold — extending the lead there fights
Group Framing, which is already solving the same problem. Simplest rule: `CameraFocus` ignores
the peek input whenever `Player.ActionState == PlayerState.Holding`.

## Files

| File | Change |
|---|---|
| `Assets/Scripts/PlayerController.cs` | `_aimPoint` + `AimPoint`, set in both branches; `OnPeek` callback setting `_peekHeld` |
| `Assets/Scripts/CameraFocus.cs` | **new** — the proxy: lead, drag, peek, derived `MaxLead` |
| `Assets/InputSystem_Actions.inputactions` | new `Peek` button action on `<Keyboard>/leftShift` |

`CameraFocus` needs a reference to the `CinemachineCamera`'s `CinemachineFollow` to drive
`FollowOffset.y` during peek.

## Editor steps

1. Create an empty GameObject **Camera Focus**, add `CameraFocus`, assign the Player and the
   `CinemachineFollow` component.
2. Point the `CinemachineCamera`'s Tracking Target at **Camera Focus** instead of the Player.
3. Set `CinemachineFollow` **Damping to 0** on all axes.
4. Wire the new `Peek` action to `PlayerController.OnPeek` on the PlayerInput component.

## Verification

Manual, in Play mode:

- **Lead** — move the cursor to a screen corner; the view drifts that way and the player sits
  off-centre, but well inside the frame.
- **Drag** — flick the cursor across the screen; the camera glides after it rather than
  snapping, and settles without overshoot.
- **Peek** — hold Shift; the camera rises and pushes further toward the cursor. The player
  stays visible near the trailing edge at all times, at any cursor distance.
- **Release** — the camera returns smoothly, not in a jump.
- **Margin holds at the extremes** — peek with the cursor at maximum range, in all four
  diagonal directions. The player must never leave frame; if they do, `_playerMargin` is too
  small or `MaxLead` is not being recomputed against the peeked height.
- **Gamepad** — lead follows the right stick at fixed distance and behaves sanely on peek.
- **During a grab** — Shift does nothing; the group camera frames player and enemy.
- **No regression** — the camera angle never changes, and mouse aiming still lands where the
  cursor is (`PlayerController`'s ground raycast depends on the fixed rotation).

## Related

[2026-09-03-chain-grab-foundation.md](2026-09-03-chain-grab-foundation.md) — records the
hand-rolled camera decision that Cinemachine has now replaced. That section is stale and
should be updated to point here.
