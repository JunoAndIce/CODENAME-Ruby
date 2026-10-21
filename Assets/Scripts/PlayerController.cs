using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CapsuleCollider))]
public class PlayerController : MonoBehaviour, IGrappleUser, ICameraTarget, IKnockdown, IKnockback, IAnimatedCharacter
{
    public float _moveSpeed = 15f;
    public int _bulletCount = 6;

    [SerializeField] private float _gamepadAimDistance = 6f;
    [Tooltip("How fast a push (a bullet's knockback) fades, in m/s per second. 20 fades a 4 m/s push in 0.2 s.")]
    [SerializeField, Min(1f)] private float _knockbackDeceleration = 20f;

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
    private bool _peekHeld;
    private bool _triggerPressed;
    private GrappleController _grapple;
    private Health _health;
    private IRagdollBody _ragdoll;
    private bool _downed;        // knocked down: physics owns the body until she gets up, or for good if dead

    public Vector3 AimPoint => _aimPoint;
    public bool TriggerPressed => _triggerPressed;
    /// <summary>False while knocked down, or while a throw charges, when left click is the throw rather than a shot.</summary>
    public bool CanShoot => !_downed && (_grapple == null || !_grapple.BlocksGun);
    /// <summary>False while knocked down: she can't move, aim, shoot or use the chain.</summary>
    public bool CanAct => !_downed;

    Vector3 ICameraTarget.Position => transform.position;
    bool ICameraTarget.WantsPeek => _peekHeld;

    Rigidbody IAnimatedCharacter.Body => _myRigidbody;
    // Only while a direction is pressed: reported as always moving, her legs would never lag behind
    // her aim while she stands still.
    float IAnimatedCharacter.MoveSpeed => _moveVelocity.sqrMagnitude > 0f ? _moveSpeed : 0f;
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
    }

    /// <summary>
    /// Knocked down by a hit (ImpactDamage): she ragdolls at the velocity the hit left her with and
    /// loses control. Alive, she gets up once settled; dead, she stays down.
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

    // Gets her up once the ragdoll settles, unless the hit (or a later one while down) killed her.
    private void UpdateDowned()
    {
        if (_health != null && _health.IsDead || !_ragdoll.IsSettled) return;

        _ragdoll.Recover();
        _downed = false;
        _knockback = Vector3.zero;
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
        if (_downed) UpdateDowned();
        else MovePlayer();
    }

    void MovePlayer()
    {
        _moveVelocity = new Vector3(_moveInput.x, 0f, _moveInput.y) * _moveSpeed;

        if (_playerInput.currentControlScheme == "Gamepad")
        {
            if (_lookInput.sqrMagnitude > 0.1f)
            {
                float angle = Mathf.Atan2(_lookInput.x, _lookInput.y) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, angle, 0f);
            }
            _aimPoint = transform.position + transform.forward * _gamepadAimDistance;
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
        transform.LookAt(_aimPoint);
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
    }

    public void LimitMove(Vector3 outward, float slackSpeed, float dragFraction)
    {
        _hasMoveLimit = true;
        _limitOutward = outward;
        _limitSlackSpeed = slackSpeed;
        _limitDragFraction = dragFraction;
    }
}
