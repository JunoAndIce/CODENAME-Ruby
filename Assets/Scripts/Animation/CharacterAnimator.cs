using UnityEngine;

/// <summary>
/// Plays a character's animations from what its IAnimatedCharacter reports. It sits on the model,
/// beside the Animator, and finds the character above it. It writes parameters, and turns the
/// model and its spine and gun arm; the clips and states come from the character's
/// CharacterAnimationSet, built into its controller.
///
/// The model's facing is its own. The body turns at once, as the game wants, and the model's legs
/// follow: standing, the upper body twists toward the target up to Twist Limit, then the feet step
/// round; moving, the legs swing to the body's facing at Turn Speed.
/// </summary>
// After the game's own scripts each frame: PlayerController turns Ruby's body in its Update, and
// placing the model first let that turn carry the model a frame's worth off, then snap it back,
// a twitch that grew with the speed of the mouse.
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(Animator))]
public class CharacterAnimator : MonoBehaviour
{
    // The names the controller builder writes and this reads, kept in one place so they can't drift.
    public const string MoveXParameter = "MoveX";
    public const string MoveZParameter = "MoveZ";
    public const string TurnParameter = "Turn";
    public const string StanceParameter = "Stance";
    public const string WeaponParameter = "Weapon";
    public const string GripLayer = "Grip";

    // A floor for the speed the blend divides by, so a character standing still reads as still.
    private const float MinMoveSpeed = 0.01f;
    // How long the grip takes to close or open, in seconds.
    private const float GripBlendTime = 0.15f;
    // A turn on the spot ends this close to the target, in degrees: an exact match would never settle.
    private const float SettleAngle = 5f;
    // The time constant the gun arm eases with, in seconds, so starting to aim swings it up.
    private const float ArmBlendTime = 0.1f;
    // How much of the arm's length the aimed hand reaches, keeping the elbow slightly bent.
    private const float ArmReachShare = 0.95f;
    // Nearer than this, the heading holds. Close to the character, a small move of the target is a
    // big turn (10 cm at half a metre is about 11 degrees), and set the chest twisting, the feet
    // stepping and the arm sweeping at every slight move of the mouse.
    private const float MinAimDistance = 1.5f;
    // The heading eases over this long, in seconds. The camera leans toward the cursor, so the point
    // under it keeps drifting a moment after the mouse stops, and the pose would twitch with it.
    private const float HeadingBlendTime = 0.08f;
    // How far ahead the head and chest look along the heading: far enough to read as level.
    private const float LookDistance = 10f;
    // Head height to look from when the model has no head bone, in metres above its feet.
    private const float FallbackEyeHeight = 1.6f;
    // Past this far round, a turn goes the gun hand's way, so the arm leads the turn instead of
    // being dragged across the chest.
    private const float TurnAroundAngle = 150f;

    private static readonly int MoveXId = Animator.StringToHash(MoveXParameter);
    private static readonly int MoveZId = Animator.StringToHash(MoveZParameter);
    private static readonly int TurnId = Animator.StringToHash(TurnParameter);
    private static readonly int StanceId = Animator.StringToHash(StanceParameter);
    private static readonly int WeaponId = Animator.StringToHash(WeaponParameter);

    [Tooltip("Smoothing on the blend parameters, in seconds. Stops a sharp change of direction snapping between clips.")]
    [SerializeField, Min(0f)] private float _damping = 0.1f;

    [Header("Turning")]
    [Tooltip("How far the upper body twists toward the target, in degrees, before the feet step round. 0 keeps the legs with the body.")]
    [SerializeField, Range(0f, 180f)] private float _twistLimit = 70f;
    [Tooltip("How fast the legs turn to catch up, in degrees per second.")]
    [SerializeField, Min(0f)] private float _turnSpeed = 360f;
    [Tooltip("How much of the twist the chest takes.")]
    [SerializeField, Range(0f, 1f)] private float _bodyLookWeight = 0.8f;
    [SerializeField, Range(0f, 1f)] private float _headLookWeight = 1f;

    [Header("Gun arm")]
    [Tooltip("The hand that holds the gun: right for officers, left for Ruby.")]
    [SerializeField] private AvatarIKGoal _gunHand = AvatarIKGoal.RightHand;
    [Tooltip("How fully the gun arm reaches out toward the target while aiming. 0 leaves it to the animation.")]
    [SerializeField, Range(0f, 1f)] private float _aimArmWeight = 1f;
    [Tooltip("Where the gun hand rests while armed but not aiming, in the model's own space: right, up from the feet, forward. Keeps the gun out of the leg the unarmed walk would swing it into.")]
    [SerializeField] private Vector3 _carryPoint = new(0.2f, 1f, 0.35f);
    [SerializeField, Range(0f, 1f)] private float _carryWeight = 1f;

    private Animator _animator;
    private IAnimatedCharacter _character;
    private int _gripLayer = -1;

    private bool _facing;      // the model's facing is its own (standing, aiming or running)
    private float _lowerYaw;   // the model's world yaw: where its legs point
    private bool _turning;     // stepping round on the spot
    private float _turnSign;   // which way the current turn on the spot goes: +1 right, -1 left
    private bool _hasTarget;
    private Vector3 _target;
    // Where the character aims, as a level heading, never a point: a point on the floor near her
    // pitched Ruby's head and spine down into her own body, and dragged the gun arm with them.
    private float _aimYaw;
    private bool _aiming;

    private Transform _head;

    private Transform _gunShoulder;
    private float _armReach;
    private float _handWeight;
    private float _aimBlend;     // 0 holds the gun at the carry point, 1 reaches it along the aim
    private float _armYaw;       // where the aimed arm points: eased as an angle, round the shoulder

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _character = GetComponentInParent<IAnimatedCharacter>();
        if (_animator == null || _character == null)
        {
            Debug.LogError($"{name}: CharacterAnimator needs {(_animator == null ? "an Animator on this object" : "an IAnimatedCharacter (a PlayerController or EnemyController) on this object or above it")}. It won't animate.", this);
            enabled = false;
            return;
        }
        // A set with no grip builds no grip layer; then there's nothing to close.
        _gripLayer = _animator.GetLayerIndex(GripLayer);
    }

    // Start, not Awake: the Animator resolves its Avatar's bones in its own Awake.
    private void Start()
    {
        if (!_animator.isHuman) return;
        _head = _animator.GetBoneTransform(HumanBodyBones.Head);
        bool left = _gunHand == AvatarIKGoal.LeftHand;
        Transform upper = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Transform lower = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        Transform hand = _animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        if (upper == null || lower == null || hand == null) return;

        _gunShoulder = upper;
        _armReach = (Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position)) * ArmReachShare;
    }

    private void OnValidate()
    {
        if (_gunHand != AvatarIKGoal.LeftHand && _gunHand != AvatarIKGoal.RightHand) _gunHand = AvatarIKGoal.RightHand;
    }

    private void Update()
    {
        AnimStance stance = _character.Stance;
        // Frozen holds the pose the body tumbles in. Speed, never enabled: Ruby's ragdoll switches her
        // Animator off and back on by itself, and the two must not fight.
        _animator.speed = stance == AnimStance.Frozen ? 0f : 1f;
        _animator.SetInteger(StanceId, (int)stance);
        _animator.SetInteger(WeaponId, (int)_character.Weapon);

        // The grip closes the gun hand only while there's a gun in it.
        if (_gripLayer >= 0)
        {
            float grip = _character.Weapon == WeaponHold.None ? 0f : 1f;
            _animator.SetLayerWeight(_gripLayer, Mathf.MoveTowards(_animator.GetLayerWeight(_gripLayer), grip, Time.deltaTime / GripBlendTime));
        }

        _hasTarget = _character.TryFindTarget(out _target);
        UpdateAimHeading();
        UpdateFacing(stance);

        Vector3 velocity = _character.Body.linearVelocity;
        velocity.y = 0f;
        // In this transform's frame, not the body's: the model can face elsewhere than the body,
        // as its legs lag in a turn, and the blend has to read movement relative to the legs.
        Vector3 local = transform.InverseTransformDirection(velocity) / Mathf.Max(_character.MoveSpeed, MinMoveSpeed);
        // Pushed while standing (speed 0), it reads as full, never past it.
        local = Vector3.ClampMagnitude(local, 1f);

        _animator.SetFloat(MoveXId, local.x, _damping, Time.deltaTime);
        _animator.SetFloat(MoveZId, local.z, _damping, Time.deltaTime);
    }

    // The level heading from the body to the target, steadied for the pose only: the body itself
    // still turns at once, so aiming stays as instant as the game makes it. Close in, the heading
    // holds; further out, it eases toward the target, turning TurnDelta's way so a big swing still
    // goes the gun hand's way round. Starting to aim, it starts where it's wanted, or at the body's
    // facing if the target is already on top of the character.
    private void UpdateAimHeading()
    {
        if (!_hasTarget)
        {
            _aiming = false;
            return;
        }

        Vector3 flat = _target - _character.Body.position;
        flat.y = 0f;
        bool far = flat.sqrMagnitude >= MinAimDistance * MinAimDistance;
        float wanted = far ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : _character.Body.transform.eulerAngles.y;

        if (!_aiming)
        {
            _aimYaw = wanted;
            _aiming = true;
            return;
        }
        if (!far) return;

        float t = 1f - Mathf.Exp(-Time.deltaTime / HeadingBlendTime);
        _aimYaw += TurnDelta(_aimYaw, wanted) * t;
    }

    private static bool IsUpright(AnimStance stance) => stance is AnimStance.Rest or AnimStance.Aim or AnimStance.Run;

    // Only on its feet does the model keep its own facing. Held, tumbling or getting up, it's left
    // alone and goes where its body goes: a tumbling capsule takes the model with it, and Ruby's
    // live ragdoll bones hang under it, so turning it then would snap her falling body round.
    private void UpdateFacing(AnimStance stance)
    {
        float rootYaw = _character.Body.transform.eulerAngles.y;
        if (!IsUpright(stance))
        {
            _facing = false;
            _turning = false;
            _animator.SetFloat(TurnId, 0f);
            return;
        }
        // Back on its feet, it carries on from where the model faces, and its legs ease round.
        if (!_facing)
        {
            _facing = true;
            _lowerYaw = transform.eulerAngles.y;
        }

        // Every turn of the legs goes TurnDelta's way, so the legs and the arm always agree on which
        // way round a turn-around goes.
        float step = _turnSpeed * Time.deltaTime;
        float turn = 0f;
        if (_twistLimit <= 0f)
        {
            _lowerYaw = rootYaw;
            _turning = false;
        }
        else if (_character.MoveSpeed > 0f)
        {
            // Aiming, along the steadied heading, not the body's raw facing, which turns with every
            // twitch of the cursor.
            float follow = _aiming ? _aimYaw : rootYaw;
            _lowerYaw += Mathf.Clamp(TurnDelta(_lowerYaw, follow), -step, step);
            _turning = false;
        }
        else if (_aiming)
        {
            float delta = AimDelta();
            if (!_turning && Mathf.Abs(delta) > _twistLimit)
            {
                _turning = true;
                _turnSign = Mathf.Sign(delta);
            }
            if (_turning)
            {
                _lowerYaw += Mathf.Clamp(delta, -step, step);
                turn = Mathf.Sign(delta);
                if (Mathf.Abs(AimDelta()) < SettleAngle) _turning = false;
            }
        }
        else
        {
            _lowerYaw += Mathf.Clamp(TurnDelta(_lowerYaw, rootYaw), -step, step);
            _turning = false;
        }

        transform.rotation = Quaternion.Euler(0f, _lowerYaw, 0f);
        _animator.SetFloat(TurnId, turn, _damping, Time.deltaTime);
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0) return;

        bool upright = IsUpright(_character.Stance);
        bool looking = upright && _aiming && (_bodyLookWeight > 0f || _headLookWeight > 0f);
        if (looking)
        {
            // Level, from head height, along the heading twisted no further than the limit (past
            // it, the feet are already stepping round). Never down at the target itself.
            Vector3 eye = _head != null ? _head.position : transform.position + Vector3.up * FallbackEyeHeight;
            _animator.SetLookAtWeight(1f, _bodyLookWeight, _headLookWeight, 0f, 0f);
            _animator.SetLookAtPosition(eye + Quaternion.Euler(0f, ClampedAimYaw(), 0f) * Vector3.forward * LookDistance);
        }
        else
        {
            _animator.SetLookAtWeight(0f);
        }

        UpdateGunArm(upright);
    }

    // Aiming, the gun arm reaches toward the target; armed but not aiming, it holds the gun low in
    // front of the body. Stand-ins, both, until pistol-holding animations fill the slots.
    //
    // Both points are worked out afresh each frame from where the body is now, and only the blend
    // between them, the weight and the arm's direction are eased. Easing a world position instead
    // left the hand trailing behind a running character, and moved it in a straight line that cut
    // through the chest whenever the aim swung across.
    private void UpdateGunArm(bool upright)
    {
        bool armed = upright && _character.Weapon != WeaponHold.None && _gunShoulder != null;
        bool aim = armed && _character.Stance == AnimStance.Aim && _aiming;
        float wantedWeight = !armed ? 0f : aim ? _aimArmWeight : _carryWeight;
        float wantedBlend = aim ? 1f : 0f;
        float aimYaw = ClampedAimYaw();

        float t = 1f - Mathf.Exp(-Time.deltaTime / ArmBlendTime);
        bool rising = _handWeight <= 0.001f;
        // Rising from nothing, the arm starts where it's wanted rather than sweeping from a stale pose.
        _aimBlend = rising ? wantedBlend : Mathf.Lerp(_aimBlend, wantedBlend, t);
        _armYaw = rising ? aimYaw : Mathf.LerpAngle(_armYaw, aimYaw, t);
        _handWeight = Mathf.Lerp(_handWeight, wantedWeight, t);

        // Level at shoulder height, along the eased heading: the hand sweeps round in front of the
        // body, and points where the character aims whatever the target's distance.
        Vector3 reached = _gunShoulder.position + Quaternion.Euler(0f, _armYaw, 0f) * Vector3.forward * _armReach;
        Vector3 carried = transform.TransformPoint(_carryPoint);

        _animator.SetIKPosition(_gunHand, Vector3.Lerp(carried, reached, _aimBlend));
        _animator.SetIKPositionWeight(_gunHand, _handWeight);
    }

    // The level aim heading, turned no further than Twist Limit off the legs.
    private float ClampedAimYaw() => _lowerYaw + Mathf.Clamp(AimDelta(), -_twistLimit, _twistLimit);

    // How far round the aim lies from the legs. Mid-turn it stays on the side the turn started
    // toward, unless the target has come back within 90 degrees of the legs: a cursor sweeping
    // round behind the character otherwise flipped the turn, and the arm, from side to side.
    private float AimDelta()
    {
        float delta = TurnDelta(_lowerYaw, _aimYaw);
        if (_turning && Mathf.Sign(delta) != _turnSign && Mathf.Abs(delta) > 90f) delta += 360f * _turnSign;
        return delta;
    }

    // How far round to turn from one heading to another, the short way, except a turn-around past
    // TurnAroundAngle goes the gun hand's way: the arm leads the turn instead of crossing the chest.
    private float TurnDelta(float from, float to)
    {
        float delta = Mathf.DeltaAngle(from, to);
        if (_gunHand == AvatarIKGoal.LeftHand && delta > TurnAroundAngle) delta -= 360f;
        else if (_gunHand == AvatarIKGoal.RightHand && delta < -TurnAroundAngle) delta += 360f;
        return delta;
    }
}
