using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float moveSpeed;
    public float _bulletCount;
    public GunController _revolver;
    public bool triggerHeld = false;
    private Rigidbody myRigidbody;
    private Camera mainCamera;
    private PlayerInput playerInput;
    private Vector2 moveInput;
    private Vector2 lookInput;
    private Vector3 moveVelocity;

    void Start()
    {
        myRigidbody = GetComponent<Rigidbody>();
        mainCamera = FindAnyObjectByType<Camera>();
        playerInput = GetComponent<PlayerInput>();
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        moveInput = context.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        lookInput = context.ReadValue<Vector2>();
    }

    public void OnFire(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            if (triggerHeld == false)
            {
                _revolver.isFiring = true;
                triggerHeld = true;  
            }
        }

        if (context.canceled)
        {
            _revolver.isFiring = false;
            triggerHeld = false; 
        }
    }

    void Update()
    {
        // Handle Movement
        Vector3 moveDirection = new(moveInput.x, 0f, moveInput.y);
        moveVelocity = moveDirection * moveSpeed;

        // Seamless Rotation
        if (playerInput.currentControlScheme == "Gamepad")
        {
            if (lookInput.sqrMagnitude > 0.1f)
            {
                float angle = Mathf.Atan2(lookInput.x, lookInput.y) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, angle, 0f);
            }
        }
        else 
        {
            Ray cameraRay = mainCamera.ScreenPointToRay(Input.mousePosition);
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
        myRigidbody.linearVelocity = moveVelocity;
    }
}
