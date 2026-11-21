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

    [SerializeField] private float _gamepadAimDistance = 6f;
    [Tooltip("How fast a push (a bullet's knockback) fades, in m/s per second. 20 fades a 4 m/s push in 0.2 s.")]
    [SerializeField, Min(1f)] private float _knockbackDeceleration = 20f;

    [Header("Revive")]
    [Tooltip("Where Ruby comes back after she dies: place it where her feet should stand. Left empty, she comes back where she stood when the level started.")]
    [SerializeField] private Transform _revivePoint;
    [Tooltip("Seconds after she dies before she's back on her feet at the Revive Point, in control again.")]
    [SerializeField, Min(0f)] private float _reviveDelay = 2f;

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

    public Vector3 AimPoint => _aimPoint;
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
    }

    /// <summary>
    /// Knocked down by a hit (ImpactDamage): she ragdolls at the velocity the hit left her with and
    /// loses control. Alive, she gets up once settled; dead, she comes back after Revive Delay.
    /// </summary>
    public void KnockDown()
    {
        if (_downed || _ragdoll == null) return;

        _downed = true;
        _moveVelocity = Vector3.zero;
        _knockback = Vector3.zero;
        // The chain lets go before the ragdoll switches her capsule off: letting go gives the
        // capsule its collisions back, which Unity refuses on a disabled collider.
        if (_grapple != null) _grapple.StandDown();
        _ragdoll.Ragdoll(_myRigidbody.linearVelocity);
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

        if (_playerInput.currentControlScheme == "Gamepad")
        {
            if (_lookInput.sqrMagnitude > 0.1f)
            {
                float angle = Mathf.Atan2(_lookInput.x, _lookInput.y) * Mathf.Rad2Deg;
                _facing = Quaternion.Euler(0f, angle, 0f);
            }
            // Along the facing she's turning to, not transform.forward: the body only turns at the
            // next physics step, and the aim shouldn't wait for it.
            _aimPoint = transform.position + _facing * Vector3.forward * _gamepadAimDistance;
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
