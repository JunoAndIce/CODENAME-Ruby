using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CapsuleCollider))]
public class PlayerController : MonoBehaviour, IGrappleUser, ICameraTarget, IKnockdown, IKnockback, IAnimatedCharacter
{
    public float _moveSpeed = 15f;
    public int _bulletCount = 6;

    [Tooltip("Seconds from standing to full Move Speed, and to turn round. Short, so the ramp reads as weight rather than lag. 0 is instant.")]
    [SerializeField, Min(0f)] private float _speedUpTime = 0.12f;
    [Tooltip("Seconds from full Move Speed to standing once the stick or keys are let go. 0 is instant.")]
    [SerializeField, Min(0f)] private float _slowDownTime = 0.08f;

    [Header("Aim")]
    [Tooltip("Gamepad, holding Aim (L2): the reticle leaves its ring at max chain length and the stick pushes it around like a cursor, at up to this speed in m/s at full tilt. Let go of the stick and it snaps back to the ring.")]
    [SerializeField, Min(0f)] private float _gamepadFreeAimSpeed = 30f;
    [Tooltip("Gamepad, holding Aim (L2): how far from Ruby the free reticle may go, in metres. Keep it beyond the grapple's Max Chain Length, the ring the reticle sits on otherwise.")]
    [SerializeField, Min(0f)] private float _gamepadFreeAimReach = 16f;
    [Tooltip("Radius of the reticle ring, in metres.")]
    [SerializeField, Min(0.01f)] private float _reticleRadius = 0.3f;

    [Tooltip("How fast a push (a bullet's knockback) fades, in m/s per second. 20 fades a 4 m/s push in 0.2 s.")]
    [SerializeField, Min(1f)] private float _knockbackDeceleration = 20f;

    [Header("Revive")]
    [Tooltip("Where Ruby comes back after she dies: place it where her feet should stand. Left empty, she comes back where she stood when the level started.")]
    [SerializeField] private Transform _revivePoint;
    [Tooltip("Seconds after she dies before she's back on her feet at the Revive Point, in control again.")]
    [SerializeField, Min(0f)] private float _reviveDelay = 2f;

    // The control scheme's name in the input actions asset: a rename there must be matched here.
    private const string GamepadScheme = "Gamepad";
    // The right stick must be pushed past this (squared tilt) to move the aim.
    private const float LookDeadzoneSqr = 0.1f;
    // Free aim snaps back only once the stick has rested this long: flicking it from one side to the
    // other passes through the centre for a frame or two, and that isn't letting go.
    private const float SnapBackDelay = 0.1f;
    // Closer than this the reticle gives no clear direction, so she keeps facing the way she was.
    private const float MinFacingDistance = 0.5f;

    private Rigidbody _myRigidbody;
    private CapsuleCollider _capsule;
    private Camera _mainCamera;
    private PlayerInput _playerInput;
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private Vector3 _moveVelocity;
    private Vector3 _knockback;  // a push carried on top of the move, fading on its own
    private bool _hasMoveLimit;  // set by the grapple each step it applies, used once
    private Vector3 _limitOutward;
    private float _limitSlackSpeed;
    private float _limitDragFraction;
    private Vector3 _aimPoint;
    // Gamepad: the reticle relative to her, so it moves with her and stays put when the stick rests.
    private Vector3 _reticleOffset;
    private float _stickRestFor;  // seconds the right stick has been inside its deadzone
    private PlayerReticle _reticle;
    // Where she faces, worked out each frame from the aim and turned onto the body in FixedUpdate.
    // Never written to the Transform: her Rigidbody interpolates, and a Transform write is pushed
    // back into the body at the next physics step along with the interpolated position, which
    // trails the real one. She lost a different slice of her move every step and jittered against
    // the camera (measured: about 3 px of frame-to-frame wobble at full speed, none after).
    private Quaternion _facing;
    private bool _peekHeld;
    private bool _triggerPressed;
    private GrappleController _grapple;
    private Health _health;
    private IRagdollBody _ragdoll;
    private bool _downed;        // knocked down: physics owns the body until she gets up, or revives if dead
    private float _deadFor;      // how long she's been dead, toward Revive Delay
    // Where and how she started the level, for a revive with no Revive Point, and her bullets then.
    private Vector3 _startPosition;
    private Quaternion _startRotation;
    private int _startBullets;
    private float _rootAboveFeet;   // how high her root stands over the bottom of her capsule
    // Her right hand, which the chain is drawn from: the revolver is in her left (her animator's
    // Gun Hand), so the right is free. Found at start; null if the model has no humanoid right hand.
    private Transform _chainHand;

    public Vector3 AimPoint => _aimPoint;
    // Without a hand (logged at start) the chain is drawn from her centre, so it still shows.
    public Vector3 ChainHand => _chainHand != null ? _chainHand.position : _myRigidbody.worldCenterOfMass;
    public bool TriggerPressed => _triggerPressed;
    /// <summary>False while knocked down, or while a throw charges, when left click is the throw rather than a shot.</summary>
    public bool CanShoot => !_downed && (_grapple == null || !_grapple.BlocksGun);
    /// <summary>False while knocked down: she can't move, aim, shoot or use the chain.</summary>
    public bool CanAct => !_downed;

    Vector3 ICameraTarget.Position => transform.position;
    bool ICameraTarget.WantsPeek => _peekHeld;

    Rigidbody IAnimatedCharacter.Body => _myRigidbody;
    // The speed she's trying to reach, from the input rather than the ramped velocity: read off the
    // ramp, her legs would walk on through the slow-down after a release, and reported as always
    // moving, they would never lag behind her aim while she stands still.
    float IAnimatedCharacter.MoveSpeed => Mathf.Min(_moveInput.magnitude, 1f) * _moveSpeed;
    // She always faces the cursor, so on her feet she's always aiming.
    AnimStance IAnimatedCharacter.Stance => _downed ? AnimStance.Frozen : AnimStance.Aim;
    WeaponHold IAnimatedCharacter.Weapon => WeaponHold.Pistol;

    bool IAnimatedCharacter.TryFindTarget(out Vector3 point)
    {
        point = _aimPoint;
        return !_downed;
    }

    void Start()
    {
        _myRigidbody = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        _myRigidbody.linearDamping = 0f;   // damping would eat the move, which is written fresh every step
        _mainCamera = FindAnyObjectByType<Camera>();
        _playerInput = GetComponent<PlayerInput>();
        _grapple = GetComponent<GrappleController>();
        TryGetComponent(out _health);
        if (!TryGetComponent(out _ragdoll))
            Debug.LogError($"{name} has no IRagdollBody (SkeletonRagdollBody) — a knockdown can't ragdoll her.", this);

        _startPosition = transform.position;
        _startRotation = transform.rotation;
        _facing = transform.rotation;
        _startBullets = _bulletCount;
        _rootAboveFeet = transform.position.y - _capsule.bounds.min.y;
        FindChainHand();

        if (_grapple == null)
            Debug.LogError($"{name} has no GrappleController, so the gamepad reticle has no chain length to ring her at: it sits on her until one is added.", this);
        else if (_gamepadFreeAimReach < _grapple.MaxChainLength)
            Debug.LogWarning($"{name}: Gamepad Free Aim Reach ({_gamepadFreeAimReach} m) is inside the grapple's Max Chain Length ({_grapple.MaxChainLength} m), so holding Aim pulls the reticle in rather than letting it out.", this);
        _reticleOffset = Heading(transform.forward) * AimRing;
        _reticle = new PlayerReticle(_reticleRadius, _mainCamera);
        // The reticle is the cursor now. Unity shows the pointer again when Play stops.
        Cursor.visible = false;
    }

    private void OnDisable() => Cursor.visible = true;

    // After Update has worked out the aim, so the reticle is never a frame behind it.
    private void LateUpdate()
    {
        if (_reticle == null) return;
        if (_downed) _reticle.Hide();
        else _reticle.Show(_aimPoint);
    }

    // Looked up on the humanoid rig rather than wired in the Inspector, so a new model needs no
    // setup. A model without one is reported, never quietly drawn from somewhere else.
    private void FindChainHand()
    {
        Animator animator = GetComponentInChildren<Animator>();
        string problem = animator == null ? "no Animator under her"
            : !animator.isHuman ? $"her Animator ({animator.name}) isn't humanoid"
            : null;
        if (problem == null)
        {
            _chainHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (_chainHand == null) problem = $"her Animator ({animator.name}) has no right hand bone mapped";
        }
        if (problem != null)
            Debug.LogError($"{name}: can't find her right hand for the chain ({problem}), so the chain is drawn from her centre.", this);
    }

    /// <summary>
    /// Knocked down by a hit (ImpactDamage): she ragdolls at the velocity the hit left her with and
    /// loses control. Alive, she gets up once settled; dead, she comes back after Revive Delay.
    /// </summary>
    public void KnockDown() => KnockDown(_myRigidbody.linearVelocity);

    /// <summary>
    /// Knocked down as KnockDown(), but ragdolled at `launchVelocity`. Nothing calls it on her today:
    /// the clothesline never hits the one holding the chain.
    /// </summary>
    public void KnockDown(Vector3 launchVelocity)
    {
        if (_downed || _ragdoll == null) return;

        _downed = true;
        _moveVelocity = Vector3.zero;
        _knockback = Vector3.zero;
        // The chain lets go before the ragdoll switches her capsule off: letting go gives the
        // capsule its collisions back, which Unity refuses on a disabled collider.
        if (_grapple != null) _grapple.StandDown();
        _ragdoll.Ragdoll(launchVelocity);
        if (_health != null && _health.IsDead) DebugLog.Info($"{name} is dead.", this);
    }

    // Gets her up once the ragdoll settles. Killed (by the hit, or a later one while down), she lies
    // where she fell for Revive Delay, then comes back at the Revive Point.
    private void UpdateDowned()
    {
        if (_health != null && _health.IsDead)
        {
            _deadFor += Time.deltaTime;
            if (_deadFor >= _reviveDelay) Revive();
            return;
        }
        if (!_ragdoll.IsSettled) return;

        _ragdoll.Recover();
        _facing = transform.rotation;   // she gets up facing wherever she fell, not where she aimed before
        _downed = false;
        _knockback = Vector3.zero;
    }

    // An early revive, until there's a proper one: back on her feet at the Revive Point (or where she
    // started the level), at full health and with the bullets she started with. The level is left as
    // it is: officers that were hunting her lose sight of her and search.
    private void Revive()
    {
        // Stood up first, which gives her capsule and Animator back, then moved, bones and all.
        _ragdoll.Recover();
        Vector3 position = _startPosition;
        Quaternion rotation = _startRotation;
        if (_revivePoint != null)
        {
            position = _revivePoint.position + Vector3.up * _rootAboveFeet;
            rotation = Quaternion.Euler(0f, _revivePoint.eulerAngles.y, 0f);
        }
        transform.SetPositionAndRotation(position, rotation);
        _myRigidbody.position = position;
        _myRigidbody.rotation = rotation;
        _myRigidbody.linearVelocity = Vector3.zero;
        _myRigidbody.angularVelocity = Vector3.zero;
        _facing = rotation;

        _health.Revive();
        _bulletCount = _startBullets;
        _moveVelocity = Vector3.zero;
        _knockback = Vector3.zero;
        _deadFor = 0f;
        _downed = false;
        DebugLog.Info($"{name} revived.", this);
    }

    /// <summary>
    /// A push, such as a bullet's. Standing, it rides on top of her move and fades on its own, so she
    /// keeps full control. Down, it lands on the ragdoll where it struck.
    /// </summary>
    public void Knockback(Vector3 impulse, Vector3 point)
    {
        if (_downed)
        {
            _ragdoll?.ApplyHit(impulse, point);
            return;
        }
        _knockback += VelocityUtil.Flatten(impulse) / Weight.MassOf(_myRigidbody);
    }

    // Input callbacks only record intent; the scene binds them by name, so don't rename them.
    public void OnMove(InputAction.CallbackContext context) => _moveInput = context.ReadValue<Vector2>();

    public void OnLook(InputAction.CallbackContext context) => _lookInput = context.ReadValue<Vector2>();

    public void OnFire(InputAction.CallbackContext context)
    {
        if (context.performed) _triggerPressed = true;
        if (context.canceled) _triggerPressed = false;
    }

    public void OnAim(InputAction.CallbackContext context)
    {
        if (context.performed) _peekHeld = true;
        if (context.canceled) _peekHeld = false;
    }

    void Update()
    {
        // Dead on her feet (damage with no knockdown), she still goes down: the revive starts from there.
        if (!_downed && _health != null && _health.IsDead) KnockDown();
        if (_downed) UpdateDowned();
        else MovePlayer();
    }

    private void OnDrawGizmosSelected()
    {
        if (_revivePoint == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(_revivePoint.position, 0.4f);
        Gizmos.DrawLine(_revivePoint.position, _revivePoint.position + _revivePoint.forward * 0.8f);
    }

    void MovePlayer()
    {
        RampMove();

        if (_playerInput.currentControlScheme == GamepadScheme)
        {
            AimWithStick();
            return;
        }
        // Read through the Input System: the legacy Input class throws once Active Input Handling
        // is set to the Input System alone.
        Mouse mouse = Mouse.current;
        if (mouse == null) return;
        Ray cameraRay = _mainCamera.ScreenPointToRay(mouse.position.ReadValue());
        Plane groundPlane = new(Vector3.up, Vector3.zero);
        if (!groundPlane.Raycast(cameraRay, out float rayLength)) return;

        Vector3 pointToLook = cameraRay.GetPoint(rayLength);
        _aimPoint = new Vector3(pointToLook.x, transform.position.y, pointToLook.z);
        Vector3 toAim = _aimPoint - transform.position;
        if (toAim.sqrMagnitude > 0.0001f) _facing = Quaternion.LookRotation(toAim, Vector3.up);
    }

    // The stick places the reticle around her as Hotline Miami does. Normally it sits on a ring at
    // the grapple's max chain length, the furthest node she can hook, and the stick only turns it
    // round; let go and it stays. Holding Aim (L2, which also peeks the camera out), it leaves the
    // ring and the stick pushes it around like a cursor, out to Gamepad Free Aim Reach; let go of
    // the stick (for Snap Back Delay) or of Aim and it snaps back to the ring, still pointing the
    // same way. Before, the free reticle stayed wherever it was left, so it never came home, and
    // the normal aim snapped in to 1.5 m on release, onto whatever stood next to her. She faces it
    // at once: the body turns at the next physics step (MoveRotation), and the aim shouldn't wait.
    void AimWithStick()
    {
        float ring = AimRing;
        float deadzone = Mathf.Sqrt(LookDeadzoneSqr);
        float tilt = Mathf.Min(_lookInput.magnitude, 1f);
        Vector3 stick = new Vector3(_lookInput.x, 0f, _lookInput.y);
        bool pushed = tilt > deadzone;
        _stickRestFor = pushed ? 0f : _stickRestFor + Time.deltaTime;

        if (_peekHeld)
        {
            if (pushed)
            {
                float push = Mathf.InverseLerp(deadzone, 1f, tilt);
                _reticleOffset += stick.normalized * (_gamepadFreeAimSpeed * push * Time.deltaTime);
                _reticleOffset = Vector3.ClampMagnitude(_reticleOffset, _gamepadFreeAimReach);
            }
            else if (_stickRestFor >= SnapBackDelay) _reticleOffset = Heading(_reticleOffset) * ring;
        }
        else _reticleOffset = (pushed ? stick.normalized : Heading(_reticleOffset)) * ring;

        if (_reticleOffset.sqrMagnitude > MinFacingDistance * MinFacingDistance)
            _facing = Quaternion.LookRotation(_reticleOffset, Vector3.up);
        _aimPoint = transform.position + _reticleOffset;
    }

    // The ring the gamepad reticle sits on: as far as the chain reaches, so it always marks the
    // furthest node she can hook. Without a grapple (reported at start) it sits on her.
    private float AimRing => _grapple != null ? _grapple.MaxChainLength : 0f;

    // The flat direction of `v`, or the way she faces when it has none (a reticle right on her).
    private Vector3 Heading(Vector3 v)
    {
        v = VelocityUtil.Flatten(v);
        return v.sqrMagnitude > 0.0001f ? v.normalized : _facing * Vector3.forward;
    }

    // Her intended velocity moves toward the input at a fixed rate: Speed Up Time from standing to
    // full speed (turning too), Slow Down Time back to standing. It shapes the intent, never the
    // delivered move: the walk limit and PushLimit still clip that in FixedUpdate. Ramping off the
    // clipped speed would make a drag at full rope collapse to a crawl, each step starting from the
    // last step's clipped speed.
    void RampMove()
    {
        Vector3 target = new Vector3(_moveInput.x, 0f, _moveInput.y) * _moveSpeed;
        bool slowing = target.sqrMagnitude < _moveVelocity.sqrMagnitude;
        float time = slowing ? _slowDownTime : _speedUpTime;
        _moveVelocity = time > 0f
            ? Vector3.MoveTowards(_moveVelocity, target, _moveSpeed / time * Time.deltaTime)
            : target;
    }

    void FixedUpdate()
    {
        // Knocked down, the ragdoll owns the body: writing a move would stand her up mid-fall.
        if (_downed)
        {
            _hasMoveLimit = false;
            return;
        }

        // The push rides on the move and passes the same limits, so a wall, a heavy body or the rope
        // stops it like a step she took.
        Vector3 move = PushLimit.Limit(_capsule, _moveVelocity + _knockback);
        _knockback = Vector3.MoveTowards(_knockback, Vector3.zero, _knockbackDeceleration * Time.fixedDeltaTime);
        if (_hasMoveLimit)
        {
            float outward = Vector3.Dot(move, _limitOutward);
            float allowed = Mathf.Max(0f, _limitSlackSpeed + _limitDragFraction * outward);
            if (outward > allowed) move -= _limitOutward * (outward - allowed);
            _hasMoveLimit = false;
        }
        VelocityUtil.SetFlatVelocity(_myRigidbody, move);
        // Through the body, so interpolation turns her smoothly between steps (see _facing).
        _myRigidbody.MoveRotation(_facing);
    }

    public void LimitMove(Vector3 outward, float slackSpeed, float dragFraction)
    {
        _hasMoveLimit = true;
        _limitOutward = outward;
        _limitSlackSpeed = slackSpeed;
        _limitDragFraction = dragFraction;
    }
}
