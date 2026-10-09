using UnityEngine;

/// <summary>
/// Added to a host while it's tethered: remembers the last wall-like thing it touched since the
/// tether last asked, so a spin can tell a real hit from the floor. A wall-like contact faces
/// sideways; floors face up, so a tumbling box catching its corners on the floor never counts.
/// The player is ignored: a parked host passes through her.
/// </summary>
public class HostContacts : MonoBehaviour
{
    Rigidbody _ignore;
    Collider _wall;

    public void Ignore(Rigidbody body) => _ignore = body;

    /// <summary>The wall-like collider touched since the last call, or null; clears it.</summary>
    public Collider TakeWall()
    {
        Collider wall = _wall;
        _wall = null;
        return wall;
    }

    void OnCollisionEnter(Collision collision) => Record(collision);
    void OnCollisionStay(Collision collision) => Record(collision);

    void Record(Collision collision)
    {
        if (_wall != null || (_ignore != null && collision.rigidbody == _ignore)) return;

        for (int i = 0; i < collision.contactCount; i++)
        {
            if (Mathf.Abs(collision.GetContact(i).normal.y) > 0.5f) continue;   // floor or ceiling
            _wall = collision.collider;
            return;
        }
    }
}
