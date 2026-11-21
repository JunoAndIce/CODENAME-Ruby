using UnityEngine;

/// <summary>
/// Turning maths for agents that walk bends as arcs instead of pivoting: vectors, angles and speeds
/// only, so the agent keeps ownership of its body. GridNavigator still just names a point.
/// </summary>
public static class Steering
{
    // Within this of dead ahead, the point is reached by a gentle correction, never a turn that
    // could circle it, so speed is left alone.
    private const float StraightAheadDegrees = 30f;

    // The slowest SpeedToFit will return, as a share of the speed asked for. Zero would freeze an
    // enemy whose point is right behind them at close range: the turn needs them to keep moving. At
    // this share the circle they can orbit has radius share * v / w: 0.056 m at 7 m/s and 0.08 m at
    // 10 m/s for Turn Speed 720, inside GridNavigator's CornerReach (0.1 m), so the floor can't hold
    // them on a corner or an arrival point. A fifth was 0.11 to 0.16 m and orbited corners forever.
    private const float SlowestShare = 0.1f;

    /// <summary>
    /// Turns a flat heading toward a flat direction by at most maxDegrees about Y, and returns the
    /// new unit heading. A zero (or vertical-only) desired direction means no new direction, and
    /// that means no turn: the heading is returned unchanged. That is a rule, not a fallback, so a
    /// caller standing on its point keeps the way it faced. A zero heading has nothing to turn
    /// from, so it takes the desired direction outright.
    /// Done with a signed angle about Y rather than Vector3.RotateTowards: for opposite vectors that
    /// picks an arbitrary axis, and a turn-around could leave the floor plane.
    /// </summary>
    public static Vector3 TurnToward(Vector3 heading, Vector3 desired, float maxDegrees)
    {
        heading.y = 0f;
        desired.y = 0f;
        if (desired.sqrMagnitude < 0.0001f) return heading;
        desired.Normalize();
        if (heading.sqrMagnitude < 0.0001f) return desired;
        heading.Normalize();

        float angle = Vector3.SignedAngle(heading, desired, Vector3.up);
        if (Mathf.Abs(angle) <= maxDegrees) return desired;
        return Quaternion.AngleAxis(Mathf.Sign(angle) * maxDegrees, Vector3.up) * heading;
    }

    /// <summary>
    /// The speed at which a full-rate turn still reaches a point, so a body never orbits it. A turn
    /// at speed v and rate w (radians a second) sweeps a circle of radius v / w. A circle that
    /// starts tangent to the heading and passes through a point angle theta off it, at distance d,
    /// has radius d / (2 sin theta), so v is capped at w * d / (2 sin theta): the point lies on the
    /// circle instead of inside it, where a faster body would circle forever. Theta is measured up
    /// to 90 degrees: a point behind is the hardest case and costs the same as one abeam.
    /// Within 30 degrees of ahead the speed is returned as asked: nothing there can be orbited.
    /// The result never drops below a tenth of the speed asked for, because a cap that reaches
    /// zero (a point straight behind, close up) would freeze the body where it must keep moving to
    /// turn round and arrive.
    /// </summary>
    public static float SpeedToFit(float speed, float turnDegreesPerSecond, float distance, float angleOffHeading)
    {
        if (angleOffHeading <= StraightAheadDegrees) return speed;

        float turnRate = turnDegreesPerSecond * Mathf.Deg2Rad;
        float sine = Mathf.Sin(Mathf.Min(angleOffHeading, 90f) * Mathf.Deg2Rad);
        float fitted = turnRate * Mathf.Max(distance, 0f) / (2f * sine);
        return Mathf.Clamp(fitted, speed * SlowestShare, speed);
    }
}
