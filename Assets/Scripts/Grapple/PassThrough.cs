using UnityEngine;

/// <summary>
/// Switches collisions off between two sets of colliders, optionally for a set time. The tether
/// uses it while a host is parked; the grapple keeps it on briefly after a throw, so a host thrown
/// from behind the player passes through them instead of hitting them.
/// </summary>
class PassThrough
{
    Collider[] _a;
    Collider[] _b;
    float _timer = float.PositiveInfinity;

    public bool IsOn => _a != null;

    /// <summary>Ignore collisions between the two sets until End, or for `seconds` when given.</summary>
    public void Begin(Collider[] a, Collider[] b, float seconds = float.PositiveInfinity)
    {
        End();
        _a = a;
        _b = b;
        _timer = seconds;
        Set(true);
    }

    public void Tick(float deltaTime)
    {
        if (!IsOn) return;
        _timer -= deltaTime;
        if (_timer <= 0f) End();
    }

    public void End()
    {
        if (!IsOn) return;
        Set(false);
        _a = _b = null;
    }

    void Set(bool ignore)
    {
        foreach (Collider a in _a)
            foreach (Collider b in _b)
                if (a != null && b != null) Physics.IgnoreCollision(a, b, ignore);   // a dead host's colliders are gone
    }
}
