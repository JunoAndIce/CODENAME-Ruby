using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float _moveSpeed;
    public int _bulletCount;
    public GunController _gun;
    private Rigidbody _myRigidbody;
    private Camera _mainCamera;
    private PlayerInput _playerInput;
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private Vector3 _moveVelocity;
    private Vector3 _aimPoint;
    public Vector3 AimPoint => _aimPoint;
    private bool _peekHeld;
    public bool PeekHeld => _peekHeld;
    private bool _triggerPressed = false;
    public bool TriggerPressed => _triggerPressed;

    [SerializeField] private float _gamepadAimDistance = 6f;
    // Action axis only. Locomotion runs every frame regardless of which state is active.
    private readonly StateMachine _actions = new();

    public PlayerFreeState Free { get; private set; }
    public PlayerThrowingState Throwing { get; private set; }
    public PlayerHoldingState Holding { get; private set; }

    public PlayerState ActionState =>
        _actions.Current is PlayerStateBase s ? s.Id : PlayerState.Free;

    void Awake()
    {
        Free = new PlayerFreeState(this);
        Throwing = new PlayerThrowingState(this);
        Holding = new PlayerHoldingState(this);
    }

    void Start()
    {
        _myRigidbody = GetComponent<Rigidbody>();
        _mainCamera = FindAnyObjectByType<Camera>();
        _playerInput = GetComponent<PlayerInput>();

        _actions.SetState(Free);
    }

    void At(IState from, IState to, IPredicate condition)
        => _actions.AddTransition(from, to, condition);

    void AnyTo(PlayerStateBase to, IPredicate condition)
        => _actions.AddAnyTransition(to, condition);

    public void EnterHold() => _actions.SetState(Holding);

    public void ExitHold() => _actions.SetState(Free);

    public void OnMove(InputAction.CallbackContext context)
    {
        _moveInput = context.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        _lookInput = context.ReadValue<Vector2>();
    }

    public void OnFire(InputAction.CallbackContext context)
    {
        // Input only records trigger position; GunController reads it and owns the cadence.
        if (context.performed) _triggerPressed = true;
        if (context.canceled) _triggerPressed = false;
    }

    public void OnAim(InputAction.CallbackContext context)
    {
        if (context.performed) _peekHeld = true;
        if (context.canceled) _peekHeld = false;
    }

    public void OnThrow(InputAction.CallbackContext context)
    {
        if (!context.performed) return;
        if (ActionState != PlayerState.Free) return;   // chain is already out

        _actions.SetState(Throwing);
    }

    void Update()
    {
        MovePlayer();
        _actions.Tick();
    }

    void MovePlayer()
    {
        // Handle Movement
        Vector3 moveDirection = new(_moveInput.x, 0f, _moveInput.y);
        _moveVelocity = moveDirection * _moveSpeed;


        if (_playerInput.currentControlScheme == "Gamepad")
        {
            if (_lookInput.sqrMagnitude > 0.1f)
            {
                float angle = Mathf.Atan2(_lookInput.x, _lookInput.y) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, angle, 0f);
            }
            _aimPoint = transform.position + transform.forward * _gamepadAimDistance;
        }
        else
        {
            Ray cameraRay = _mainCamera.ScreenPointToRay(Input.mousePosition);
            Plane groundPlane = new(Vector3.up, Vector3.zero);

            if (groundPlane.Raycast(cameraRay, out float rayLength))
            {
                Vector3 pointToLook = cameraRay.GetPoint(rayLength);
                Debug.DrawLine(cameraRay.origin, pointToLook, Color.red);

                _aimPoint = new Vector3(pointToLook.x, transform.position.y, pointToLook.z);
                transform.LookAt(_aimPoint);
            }
        }
    }


    void FixedUpdate()
    {
        _myRigidbody.linearVelocity = _moveVelocity;
        _actions.FixedTick();
    }
}
