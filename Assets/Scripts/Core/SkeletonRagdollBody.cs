using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ragdoll for any Humanoid model, found through its Avatar: the Ragdoll Wizard's bodies under the Hips
/// bone. Standing, the bones are kinematic and only the root's capsule collides; ragdolling, the bones
/// share the body's weight and the root rides a joint at the pelvis.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SkeletonRagdollBody : MonoBehaviour, IRagdollBody
{
    // The root's share of the weight while it rides the pelvis: enough to simulate, too little to tug the body.
    private const float RootShare = 0.02f;
    // How high above the pelvis the ground search starts, so a pelvis resting on the floor still finds it.
    private const float GroundProbeLift = 0.2f;
    private const float GroundProbeDepth = 3f;

    [Tooltip("Every bone must stay slower than this, in m/s, for Settle Time before the body counts as settled.")]
    [SerializeField, Min(0f)] private float _settleSpeed = 0.4f;
    [Tooltip("Every bone must also turn slower than this, in rad/s.")]
    [SerializeField, Min(0f)] private float _settleAngularSpeed = 0.5f;
    [SerializeField, Min(0f)] private float _settleTime = 0.75f;
    [Tooltip("The share of a hit (a bullet's push) that lands on the limb it struck, turning the body. The rest moves the whole body, so no single joint takes all of it.")]
    [SerializeField, Range(0f, 1f)] private float _hitLocalShare = 0.3f;

    private Rigidbody _root;
    private Weight _weight;
    private float _standingMass;
    private Animator _animator;
    private Rigidbody _pelvis;
    private Rigidbody[] _bones;
    private float[] _shares;
    private CollisionDetectionMode[] _boneModes;
    private Collider[] _boneColliders;
    private Collider[] _standing;
    private bool[] _standingEnabled;
    private float _rootAboveFeet;
    private RigidbodyInterpolation _rootInterpolation;
    private RigidbodyConstraints _rootConstraints;
    private ConfigurableJoint _carry;

    private bool _ready;
    private bool _live;          // the bones own the body: ragdolling, held or at rest
    private bool _ragdolling;
    private bool _resting;
    private float _slowTimer;

    public bool IsSettled => _ragdolling && _slowTimer >= _settleTime;
    public bool IsRagdolling => _ragdolling;

    private float TotalMass => _weight != null ? _weight.TotalMassKg : _standingMass;

    // Start, not Awake: the Animator resolves its Avatar's bones in its own Awake.
    private void Start()
    {
        _root = GetComponent<Rigidbody>();
        TryGetComponent(out _weight);
        _standingMass = _root.mass;
        _animator = GetComponentInChildren<Animator>();

        if (!FindStanding())
        {
            Debug.LogError($"{name}: SkeletonRagdollBody belongs on the character's root, beside its capsule, not on the model. Move it there and remove the Rigidbody it added here.", this);
            return;
        }

        Transform hips = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
        if (hips == null)
        {
            Debug.LogError($"{name}: SkeletonRagdollBody can't find the model's Hips bone. It needs a model in its children imported with Rig: Humanoid and Optimize Game Objects off, so its bones exist as GameObjects.", this);
            return;
        }
        if (!hips.TryGetComponent(out _pelvis))
        {
            Debug.LogError($"{name}: the Avatar's Hips bone ({hips.name}) has no Rigidbody. Run the Ragdoll Wizard with {hips.name} as its Pelvis.", this);
            return;
        }

        if (!FindBones(hips)) return;

        _rootInterpolation = _root.interpolation;
        _rootConstraints = _root.constraints;
        _ready = true;

        // Parked whatever the prefab says: live bones would fight the Animator and shove the capsule.
        SetBones(false);
    }

    // The pelvis and every jointed body under it: a gun held under a hand bone is a Rigidbody, not a bone.
    private bool FindBones(Transform hips)
    {
        var bones = new List<Rigidbody>();
        foreach (Rigidbody body in hips.GetComponentsInChildren<Rigidbody>(true))
            if (body == _pelvis || body.TryGetComponent(out Joint _)) bones.Add(body);
        _bones = bones.ToArray();

        float sum = 0f;
        foreach (Rigidbody bone in _bones) sum += bone.mass;
        if (sum <= 0f)
        {
            Debug.LogError($"{name}: the ragdoll's bones have no mass. Rerun the Ragdoll Wizard.", this);
            return false;
        }

        _shares = new float[_bones.Length];
        _boneModes = new CollisionDetectionMode[_bones.Length];
        var colliders = new List<Collider>();
        for (int i = 0; i < _bones.Length; i++)
        {
            _shares[i] = _bones[i].mass / sum;
            _boneModes[i] = _bones[i].collisionDetectionMode;
            foreach (Collider collider in _bones[i].GetComponents<Collider>())
                if (!collider.isTrigger) colliders.Add(collider);
        }
        _boneColliders = colliders.ToArray();
        return true;
    }

    // The root's own solid colliders (its capsule), and how far its origin stands above their feet.
    private bool FindStanding()
    {
        var standing = new List<Collider>();
        float feet = transform.position.y;
        foreach (Collider collider in GetComponents<Collider>())
        {
            if (collider.isTrigger) continue;
            standing.Add(collider);
            if (collider.enabled) feet = Mathf.Min(feet, collider.bounds.min.y);
        }
        _standing = standing.ToArray();
        _standingEnabled = new bool[_standing.Length];
        for (int i = 0; i < _standing.Length; i++) _standingEnabled[i] = _standing[i].enabled;
        _rootAboveFeet = transform.position.y - feet;
        return _standing.Length > 0;
    }

    public void Ragdoll(Vector3 launchVelocity)
    {
        if (!_ready) return;
        GoLive();
        _ragdolling = true;
        _slowTimer = 0f;
        SetVelocity(launchVelocity);
    }

    // All of it on the struck limb would tear it from its joint; spread evenly, a shoulder shot wouldn't turn her.
    public void ApplyHit(Vector3 impulse, Vector3 point)
    {
        if (!_ready || !_ragdolling) return;

        // The same change on every body, root included, carries the shared part's momentum exactly.
        Vector3 shared = impulse * (1f - _hitLocalShare) / TotalMass;
        _root.linearVelocity += shared;
        foreach (Rigidbody bone in _bones) bone.linearVelocity += shared;
        NearestBone(point).AddForceAtPosition(impulse * _hitLocalShare, point, ForceMode.Impulse);
    }

    // The limb whose surface is nearest the point: the bullet stopped on her capsule or a limb, not inside a bone.
    private Rigidbody NearestBone(Vector3 point)
    {
        Rigidbody nearest = _pelvis;
        float best = float.MaxValue;
        foreach (Collider collider in _boneColliders)
        {
            float distance = (collider.ClosestPoint(point) - point).sqrMagnitude;
            if (distance >= best) continue;
            best = distance;
            nearest = collider.attachedRigidbody;
        }
        return nearest;
    }

    // Interim: stood up in one piece, because the chain still pulls the root and sizes its hold from
    // the root's capsule, and a limp body is neither. The limp hold, pulled by the chest with the
    // arms bound to it, replaces this (enemy ragdoll plan, Task 3).
    public void Hold() => Recover();

    public void Rest()
    {
        if (!_ready || !_live) return;
        _resting = true;
        _ragdolling = false;

        _root.linearVelocity = Vector3.zero;
        _root.constraints = RigidbodyConstraints.FreezeAll;
        for (int i = 0; i < _bones.Length; i++)
        {
            _bones[i].linearVelocity = Vector3.zero;
            _bones[i].angularVelocity = Vector3.zero;
            _bones[i].collisionDetectionMode = _boneModes[i];
            _bones[i].isKinematic = true;
        }
        foreach (Collider collider in _boneColliders) collider.isTrigger = true;
    }

    public void Recover()
    {
        if (!_ready) return;
        Wake();
        _ragdolling = false;
        _slowTimer = 0f;
        if (!_live) return;
        _live = false;

        Carry(false);
        SetBones(false);

        // She gets up where her body lies, on whatever is under her pelvis.
        Vector3 pelvis = _pelvis.position;
        float ground = Physics.Raycast(pelvis + Vector3.up * GroundProbeLift, Vector3.down, out RaycastHit hit,
                                       GroundProbeLift + GroundProbeDepth, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.point.y
            : pelvis.y;
        Vector3 standAt = new(pelvis.x, ground + _rootAboveFeet, pelvis.z);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        Quaternion upright = Quaternion.LookRotation(forward.normalized, Vector3.up);

        transform.SetPositionAndRotation(standAt, upright);
        _root.position = standAt;
        _root.rotation = upright;
        _root.linearVelocity = Vector3.zero;
        _root.angularVelocity = Vector3.zero;
        _root.mass = TotalMass;
        _root.interpolation = _rootInterpolation;
        for (int i = 0; i < _standing.Length; i++) _standing[i].enabled = _standingEnabled[i];

        _animator.enabled = true;
        _animator.Update(0f);   // poses the bones now, so the mesh doesn't flash its ragdoll pose for a frame
    }

    private void FixedUpdate()
    {
        if (!_ragdolling) return;

        // A bouncing body passes through near-zero speed at the top of each bounce, so slowness must last.
        _slowTimer = AllSlow() ? _slowTimer + Time.fixedDeltaTime : 0f;
    }

    // Every bone, not just the pelvis: a limb still swinging means the body hasn't settled.
    private bool AllSlow()
    {
        float linear = _settleSpeed * _settleSpeed;
        float angular = _settleAngularSpeed * _settleAngularSpeed;
        foreach (Rigidbody bone in _bones)
            if (bone.linearVelocity.sqrMagnitude > linear || bone.angularVelocity.sqrMagnitude > angular) return false;
        return true;
    }

    // The bones take the body over from the Animator and the root's capsule.
    private void GoLive()
    {
        Wake();
        if (_live) return;
        _live = true;

        _animator.enabled = false;
        foreach (Collider collider in _standing) collider.enabled = false;
        SetBones(true);

        float total = TotalMass;
        for (int i = 0; i < _bones.Length; i++) _bones[i].mass = total * (1f - RootShare) * _shares[i];
        _root.mass = total * RootShare;
        // An interpolated root moves the bones under it between physics steps, shaking the whole body.
        _root.interpolation = RigidbodyInterpolation.None;
        Carry(true);
    }

    // Back from rest: solid and simulated again. Every way of moving the body starts here.
    private void Wake()
    {
        if (!_resting) return;
        _resting = false;
        foreach (Collider collider in _boneColliders) collider.isTrigger = false;
        _root.constraints = _rootConstraints;
        SetBones(true);
    }

    private void SetBones(bool live)
    {
        for (int i = 0; i < _bones.Length; i++)
        {
            Rigidbody bone = _bones[i];
            if (live)
            {
                bone.isKinematic = false;
                // A knocked-down body is fast enough to tunnel through a wall on discrete detection.
                bone.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            else
            {
                bone.collisionDetectionMode = _boneModes[i];
                bone.isKinematic = true;
            }
        }
        foreach (Collider collider in _boneColliders) collider.enabled = live;
    }

    private void SetVelocity(Vector3 velocity)
    {
        _root.linearVelocity = velocity;
        foreach (Rigidbody bone in _bones) bone.linearVelocity = velocity;
    }

    // A joint, not moving the root after the pelvis: the chain pulls an enemy by its root, and the body must follow.
    private void Carry(bool on)
    {
        if (_carry == null)
        {
            _carry = gameObject.AddComponent<ConfigurableJoint>();
            _carry.connectedBody = _pelvis;
            // Unity starts a new joint's anchor off the origin, which held the root a metre under the pelvis.
            _carry.anchor = Vector3.zero;
            _carry.autoConfigureConnectedAnchor = false;
            _carry.connectedAnchor = Vector3.zero;
            _carry.angularXMotion = ConfigurableJointMotion.Free;
            _carry.angularYMotion = ConfigurableJointMotion.Free;
            _carry.angularZMotion = ConfigurableJointMotion.Free;
        }

        // On the pelvis itself: kept at its standing offset, a body lying down would carry the root under the floor.
        if (on) _root.position = _pelvis.position;
        ConfigurableJointMotion motion = on ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Free;
        _carry.xMotion = motion;
        _carry.yMotion = motion;
        _carry.zMotion = motion;
    }
}
