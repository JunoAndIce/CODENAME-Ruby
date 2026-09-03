using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float _moveSpeed;
    public float _bulletCount;
    public GunController _gun;
    public bool _triggerHeld = false;
    private Rigidbody _myRigidbody;
    private Camera _mainCamera;
    private PlayerInput _playerInput;
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private Vector3 _moveVelocity;

    void Start()
    {
        _myRigidbody = GetComponent<Rigidbody>();
        _mainCamera = FindAnyObjectByType<Camera>();
        _playerInput = GetComponent<PlayerInput>();
    }

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
        if (context.performed)
        {
            if (_triggerHeld == false)
            {
                _gun._isFiring = true;
                _triggerHeld = true;  
            }
        }

        if (context.canceled)
        {
            _gun._isFiring = false;
            _triggerHeld = false; 
        }
    }

    void Update()
    {
        MovePlayer();
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
    }
}
