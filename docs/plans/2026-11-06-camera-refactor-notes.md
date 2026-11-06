# Camera Refactor and Upgrade: Notes

Notes for later, not a plan. Nothing here is built. The code and settings below were read from the project on 2026-11-06. **Nothing here was tried in play**, and anything marked *untested* is a likely explanation to check first.

## What the user asked for

> Cam height has the same look at 3, and at 35. If we don't need that value, we need to change the camera to be simpler, but still achieve the same goal and look.

> Keep in mind, shadows are weirdly rendered on the camera. I want to fix this, but I dont have much knowledge on shadows and how the camera works with it.

## What Cam Height does today

The camera is orthographic, so moving it up or down doesn't change the size of anything on screen. That is why 3 and 35 look the same. `CameraFocus` still uses the height in three ways.

1. **It sets the peek zoom.** The view zooms by Peek Height ÷ Cam Height (`OrthoSizeAt`). The scene has 44 ÷ 35, about 1.26x. Changing Cam Height alone changes that: Cam Height 3 with Peek Height 44 would zoom out about 15x on Shift. So 3 and 35 only look the same until you peek.
2. **It keeps the camera above everything.** Anything higher than the camera, or nearer than its near clip plane (0.3 m), isn't drawn. At a height of 3 a tall wall could be cut off. *Untested: wall heights weren't checked.*
3. **It decides where the floor sits in the shadow range.** See below. This may be tied to the shadow problem, so the height isn't purely a leftover.

## A simpler camera

- **Replace Cam Height and Peek Height with one Peek Zoom,** such as 1.26, multiplying the camera's Size directly. Smooth the zoom itself instead of smoothing a height.
- **Keep the camera at one fixed height,** chosen to clear the tallest wall and suit the shadows, as a constant or one field with a tooltip saying what it's for.
- **Consider dropping the perspective branch.** `LeadLimits` handles a perspective camera with its field of view, and `OrthoSizeAt` is skipped for one. The game is orthographic, so this code may not be needed. Keep it only if perspective is still wanted.
- **The look to keep:**
  - The lead toward the cursor, Lead Fraction and its damping.
  - Peek throwing the view to the frame's edge in the cursor's direction, with its faster damping.
  - The player never pushed off screen, with the reach derived from the camera each frame.
  - The snap when the target teleports, such as Ruby's revive.
- **Scene values to carry over by hand:** these are saved in `SampleScene.unity`, and renamed fields lose them unless they use `FormerlySerializedAs`. The new Peek Zoom has no old field to carry from, so set it to the ratio.

Also: `CLAUDE.md` says the Main Camera's Size is 6.25, Hotline Miami 2's framing, but the scene has 8.5. Decide which is right and correct the other.

## Shadows: the short version

A directional light (the sun) makes shadows by drawing the scene from the light's point of view into a texture, the **shadow map**. Each pixel on screen then asks that texture whether it's in shadow. A shadow map is a fixed number of pixels, so the more ground it has to cover, the blockier and shakier the shadows get.

To spend those pixels well, URP limits and splits the area:

- **Shadow Distance** is how far from the camera shadows are drawn at all.
- **Cascades** split that distance into bands, each with its own part of the map. Near bands are sharp and far bands are blurry. This suits a perspective camera, where near things are big on screen.

That logic measures distance **from the camera**. A camera looking straight down at a floor 35 m below has all its ground at about the same distance, so the whole view lands in one band.

## What the settings say (PC quality level, the active one)

| Setting | Value |
| --- | --- |
| Shadow Distance | 50 m |
| Cascades | 4, split at about 6, 15 and 27 m |
| Shadow map | 2048 |
| Depth / Normal Bias | 0.1 / 0.5 |
| Soft shadows | On, high quality |
| Camera height | 35, or 44 peeking |

**Likely causes to check, all untested:**

1. **The floor is in the last, lowest-detail cascade.** At height 35 it is beyond the 27 m split. The whole visible floor gets only one quarter of the shadow map, stretched over a band from 27 to 50 m. That could read as blocky or soft shadows.
2. **Peeking moves the floor toward the end of the range.** At height 44 the floor is close to the 50 m limit, where shadows start fading out. Shadows may change or fade as the view zooms.
3. **Shadows shimmer as the camera moves.** The shadow map is re-fitted to the view every frame, so edges can crawl as the follow camera drifts. Cascades fit more tightly to the camera than one big map does.
4. **The biases are on the large side for small characters.** High Normal Bias can detach or thin a character's shadow, so it looks like it floats or starts away from his feet.
5. **The quality level matters.** Mobile has very different shadow settings: 1 cascade, a 1024 map, bias 1 and no soft shadows. Check which one you're looking at. A build may not use the editor's.

## Seen in play: shadows sliding onto the bottom wall

*(User, 2026-11-06: "the shadows look like they move onto the bottom wall when I walk towards it at low shadow distance (like 50, 100). At 200 it still happens but is faint." Two clips, the clearer at Shadow Distance 50, Cam Height 15, Peek Height 19.)*

**What the clip shows:**

- A wall's shadow should be a solid band from its foot out to about 2.5 m (3 m tall, sun at about 50 degrees). The outer edge of the band is right and stays still.
- **Near the foot of the bottom wall the shadow is missing,** leaving a strip of lit floor between the band and the wall. The strip's edge is a shallow slant, which doesn't follow the wall or the direction of the sun.
- **Under the camera the shadow is whole,** in a dome shape with a curved edge, reaching down to the wall. The dome follows the camera, not anything in the world.
- So the "moving" shadow is that dome sweeping over the wall as she walks toward it. The real shadow isn't moving: what changes is where it is drawn correctly.

**Best explanation, untested:** the dome is the edge of a cascade. The camera is about 15.7 m above the floor at these settings, and Shadow Distance 50 puts a split at about 15 m, so that split lands almost exactly on the floor. Inside the circle the floor uses one cascade, and outside it the next, and the outer one loses the bottom of the wall's shadow.

**Confirmed in play (user, 2026-11-06):** Cascade Count 1 on `PC_RPAsset` fixes it, with Cam Height 15 and Peek Height 19. With one cascade there is no edge to sweep across the view. Keep it for this camera: nearly everything it sees is about the same distance away, so cascades have nothing to gain. All of the shadow map then goes on one band, instead of a quarter each.

**Shadow Distance to go with it:** it only has to reach the far corners of the view, and anything past that wastes detail. By rough sums for a 16:9 screen, those corners are about 23 m from the camera at height 15 and about 29 m peeking at 19. Shadows fade over the last 10% or so, so about 35 clears them. At the old heights of 35 and 44 the peeking corner is about 50 m, right at the old Shadow Distance, so shadows would have faded in the corners there. *Untested.*

**Still not explained:** why the outer cascade lost the shadow. The strip is roughly a metre wide, which looks too big for bias alone, by a rough estimate. Reading the numbers can't settle this, so experiments 3 and 4 below come first.

## Experiments, in order

Each is a settings change in the Inspector, and takes seconds to undo:

1. **Write down what "weird" looks like first:** a screenshot or short clip, standing still, moving and peeking. Blocky, shimmering, floating and fading each point to a different cause above.
2. **Stand still and peek (Shift) toward the wall.** The camera moves and she doesn't. If the shadow slides anyway, it's tied to the camera and not to anything casting it. Also try peeking away from the wall.
3. **Show the cascades on screen:** Window > Analysis > Rendering Debugger, Lighting tab, Lighting Debug Mode set to Shadow Cascades. Each band gets a colour, so you can see whether an edge crosses the wall as the shadow moves.
4. **On `PC_RPAsset`, set Cascade Count to 1.** The whole map then covers the whole Shadow Distance. If the sliding stops, cascades were the cause.
5. **Lower the camera and the distance together:** for example height 15, Peek Height scaled to keep the same ratio (about 19), and Shadow Distance about 25. Fewer metres means sharper shadows.
6. **Lower Normal Bias** a little at a time, watching for acne, the speckled stripes that appear when it's too low.
7. **Use the Frame Debugger** (Window > Analysis) to see the shadow map being drawn, and how much of it the view uses.

If 5 helps, the fixed camera height in the simpler design should be chosen with it: a low height that clears the tallest wall, with Shadow Distance set just past the floor.

## Things to keep in mind when this is built

- `CameraFocus` keeps positioning both itself and the camera in `LateUpdate`, to avoid two scripts racing, and Cinemachine stays out.
- It's in `Ruby.CameraRig`, which reaches the game only through `ICameraTarget`.
- The authored Size is read at `Awake`, so it's set with Play stopped.
- `CLAUDE.md`'s **Camera** section and the manual need updating when this changes.
