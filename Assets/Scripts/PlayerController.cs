using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float _moveSpeed;
    public int _bulletCount;
    public GunController _gun;
    public bool _triggerHeld = false;
    private Rigidbody _myRigidbody;
    private Camera _mainCamera;
    private PlayerInput _playerInput;
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private Vector3 _moveVelocity;

    // Action axis only. Locomotion runs every frame regardless of which state is active.
    private readonly StateMachine _actions = new();

    public PlayerFreeState Free { get; private set; }
    public PlayerFiringState Firing { get; private set; }
    public PlayerHoldingState Holding { get; private set; }

    public PlayerActionState ActionState =>
        _actions.Current is PlayerStateBase s ? s.Id : PlayerActionState.Free;

    void Awake()
    {
        Free = new PlayerFreeState(this);
        Firing = new PlayerFiringState(this);
        Holding = new PlayerHoldingState(this);

        At(Free, Firing, new FuncPredicate(() => _triggerHeld && _bulletCount > 0));
        At(Firing, Free, new FuncPredicate(() => !_triggerHeld || _bulletCount <= 0));
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
        // Input only records trigger position; PlayerFiringState drives the gun.
        if (context.performed) _triggerHeld = true;
        if (context.canceled) _triggerHeld = false;
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

        // Seamless Rotation
        if (_playerInput.currentControlScheme == "Gamepad")
        {
            if (_lookInput.sqrMagnitude > 0.1f)
            {
                float angle = Mathf.Atan2(_lookInput.x, _lookInput.y) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, angle, 0f);
            }
        }
        else
        {
            Ray cameraRay = _mainCamera.ScreenPointToRay(Input.mousePosition);
            Plane groundPlane = new(Vector3.up, Vector3.zero);

            if (groundPlane.Raycast(cameraRay, out float rayLength))
            {
                Vector3 pointToLook = cameraRay.GetPoint(rayLength);
                Debug.DrawLine(cameraRay.origin, pointToLook, Color.red);

                transform.LookAt(new Vector3(pointToLook.x, transform.position.y, pointToLook.z));
            }
        }
    }


    void FixedUpdate()
    {
        _myRigidbody.linearVelocity = _moveVelocity;
        _actions.FixedTick();
    }
}
