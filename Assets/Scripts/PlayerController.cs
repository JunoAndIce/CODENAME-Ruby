using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CapsuleCollider))]
public class PlayerController : MonoBehaviour, IGrappleUser, ICameraTarget
{
    public float _moveSpeed = 15f;
    public int _bulletCount = 6;

    [SerializeField] private float _gamepadAimDistance = 6f;
    [Tooltip("How fast pushes from outside (tether yanks, later knockback) fade. Kept out of Rigidbody damping, which would eat move input too.")]
    [SerializeField] private float _flingDamping = 3f;

    private Rigidbody _myRigidbody;
    private CapsuleCollider _capsule;
    private Camera _mainCamera;
    private PlayerInput _playerInput;
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private Vector3 _moveVelocity;
    private Vector3 _external;   // explicit pushes only, fading at Fling Damping
    private bool _hasMoveLimit;  // set by the grapple each step it applies, used once
    private Vector3 _limitOutward;
    private float _limitSpeed;
    private Vector3 _aimPoint;
    private bool _peekHeld;
    private bool _triggerPressed;

    // The chain axis only: movement and shooting run every frame whatever the state.
    private readonly StateMachine _actions = new();

    public Vector3 AimPoint => _aimPoint;
    public bool TriggerPressed => _triggerPressed;
    public PlayerHoldingState Holding { get; private set; }
    public PlayerState ActionState => _actions.Current is PlayerStateBase s ? s.Id : PlayerState.Free;

    Vector3 ICameraTarget.Position => transform.position;
    // No peeking while holding something: the hold owns the camera.
    bool ICameraTarget.WantsPeek => _peekHeld && ActionState != PlayerState.Holding;

    void Awake() => Holding = new PlayerHoldingState(this);

    void Start()
    {
        _myRigidbody = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        _myRigidbody.linearDamping = 0f;   // see Fling Damping
        _mainCamera = FindAnyObjectByType<Camera>();
        _playerInput = GetComponent<PlayerInput>();
    }

    public void EnterHold() => _actions.SetState(Holding);

    public void ExitHold() => _actions.Clear();

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
        MovePlayer();
        _actions.Tick();
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

        Ray cameraRay = _mainCamera.ScreenPointToRay(Input.mousePosition);
        Plane groundPlane = new(Vector3.up, Vector3.zero);
        if (!groundPlane.Raycast(cameraRay, out float rayLength)) return;

        Vector3 pointToLook = cameraRay.GetPoint(rayLength);
        _aimPoint = new Vector3(pointToLook.x, transform.position.y, pointToLook.z);
        transform.LookAt(_aimPoint);
    }

    void FixedUpdate()
    {
        // Pushes aren't limited by what the player walks into: the solver owns those.
        Vector3 move = PushLimit.Limit(_capsule, _moveVelocity);
        if (_hasMoveLimit)
        {
            float outward = Vector3.Dot(move, _limitOutward);
            if (outward > _limitSpeed) move -= _limitOutward * (outward - _limitSpeed);
            _hasMoveLimit = false;
        }
        VelocityUtil.ApplyAuthoredMove(_myRigidbody, move, ref _external, _flingDamping);
        _actions.FixedTick();
    }

    public void LimitMove(Vector3 outward, float maxOutwardSpeed)
    {
        _hasMoveLimit = true;
        _limitOutward = outward;
        _limitSpeed = Mathf.Max(0f, maxOutwardSpeed);
    }

    /// <summary>A push from outside (the tether, later knockback): lands now, then carries and fades.</summary>
    public void AddExternalVelocity(Vector3 change) => VelocityUtil.AddExternal(_myRigidbody, change, ref _external);
}
