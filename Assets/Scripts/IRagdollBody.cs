using UnityEngine;


public interface IRagdollBody
{
    Rigidbody Root { get; }
    bool IsSettled { get; }
    void Ragdoll(Vector3 launchVelocity);
    void Recover();
}
