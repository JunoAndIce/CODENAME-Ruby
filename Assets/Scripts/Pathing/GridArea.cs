using UnityEngine;

// Values are explicit because Unity saves the number: a new shape takes a new number.
public enum AreaShape { Circle = 0, Rectangle = 1 }

/// <summary>
/// A patch of floor an agent can be sent to: a circle, or a rectangle lined up with X and Z like
/// the grid itself, so it can fill a room or run along a corridor. Height is ignored.
/// </summary>
public readonly struct GridArea
{
    public readonly Vector3 Centre;
    public readonly AreaShape Shape;
    /// <summary>Half the width along X and half the depth along Z; a circle's radius in both.</summary>
    public readonly Vector2 HalfSize;

    private GridArea(Vector3 centre, AreaShape shape, Vector2 halfSize)
    {
        Centre = centre;
        Shape = shape;
        HalfSize = halfSize;
    }

    public static GridArea Circle(Vector3 centre, float radius) => new(centre, AreaShape.Circle, new Vector2(radius, radius));

    /// <summary>size is the full width along X and depth along Z.</summary>
    public static GridArea Rectangle(Vector3 centre, Vector2 size) => new(centre, AreaShape.Rectangle, size * 0.5f);

    /// <summary>The longest straight walk across it: a circle's diameter, a rectangle's longer side.</summary>
    public float Span => 2f * Mathf.Max(HalfSize.x, HalfSize.y);

    public bool Contains(Vector3 point)
    {
        float dx = point.x - Centre.x, dz = point.z - Centre.z;
        if (Shape == AreaShape.Rectangle) return Mathf.Abs(dx) <= HalfSize.x && Mathf.Abs(dz) <= HalfSize.y;
        return dx * dx + dz * dz <= HalfSize.x * HalfSize.x;
    }
}
