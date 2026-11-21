# Camera

Split out of `CLAUDE.md` to keep the per-prompt context small. Read it before changing this system, and keep it current when you do.

#### Camera

[CameraFocus.cs](Assets/Scripts/CameraRig/CameraFocus.cs) is a proxy GameObject that, in
`LateUpdate`, positions **both itself and the camera** (deliberately, to avoid undefined ordering
between two scripts). It follows an `ICameraTarget`: its Target field, or the first one in the
scene when that's empty. The Main Camera is **orthographic**: Size 6.25 (a 12.5 m tall view) is Hotline Miami 2's
framing, measured from the Police Station, where a character fills about 1/12.5 of the screen.

- Normal play leans slightly toward `AimPoint` (Lead Fraction), smoothed by `SmoothDamp` — that
  damping *is* the drag. A target that jumps more than 5 m in one frame was teleported (Ruby's
  revive), and the view snaps there instead of gliding across the level.
- **Peek (Shift)** throws the view to the **frame's edge in the cursor's direction**: only the
  direction counts, so the view can't be held near centre. It uses its own faster Peek Damping and
  zooms out to Peek Height.
- **Height drives zoom.** For an orthographic camera, Peek Height ÷ Cam Height scales the camera's
  authored Size; raising an orthographic camera alone would change nothing on screen. The authored
  Size is read at Awake, so set it with Play mode stopped.
- **The lead's reach is derived, never serialized**: a rectangle of half the frame's width and height
  (less Player Margin), read from the camera's Size or FOV each frame. No zoom or aspect can push the
  player off screen. Keep it derived.

`CameraFocus` superseded the Cinemachine rig described in
`docs/specs/2026-09-05-camera-aim-and-peek.md`, and the Cinemachine package has been removed. Don't
bring it back for the follow camera: `CameraFocus` owns the camera's position.
