using UnityEngine;


public class CameraFocus : MonoBehaviour
{
    [SerializeField] private PlayerController _player;
    [SerializeField] private Camera _camera;

    [Header("Lead")]
    [SerializeField] private float _leadFraction = 0.35f;
    [SerializeField] private float _peekLeadFraction = 0.8f;
    [SerializeField] private float _damping = 0.25f;

    [Header("Height")]
    [SerializeField] private float _camHeight = 20f;
    [SerializeField] private float _peekHeight = 28f;
    [SerializeField] private float _heightDamping = 0.4f;

    [Header("Framing")]
    [Range(0f, 0.5f)]
    [SerializeField] private float _playerMargin = 0.25f;

    private Vector3 _velocity;
    private float _currentHeight;
    private float _heightVelocity;

   // Current height of the camera after Awake, call this value to always get the current height
    public float Height => _currentHeight;

    public float MaxLead
    {
        get
        {
            if (_camera == null) return 0f;

            // Vertical is the smaller screen extent, so it is the binding constraint.
            float halfHeight = _currentHeight * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return halfHeight * (1f - _playerMargin);
        }
    }

    private void Awake()
    {
        if (_player == null) _player = FindAnyObjectByType<PlayerController>();
        if (_camera == null) _camera = FindAnyObjectByType<Camera>();
 
        _currentHeight = _camHeight;
        
        transform.position = _player.transform.position;
    }

    private void LateUpdate()
    {
        if (_player == null) return;

        // Peeking is only allowed when the player is not holding onto another enemy or object and when the peek button is held.
        bool peeking = _player.PeekHeld && _player.ActionState != PlayerState.Holding;


        // SmoothDamp to the current height depending on if the player is peeking.
        _currentHeight = Mathf.SmoothDamp(_currentHeight, peeking ? _peekHeight : _camHeight, ref _heightVelocity, _heightDamping);

        Vector3 leadCam = (_player.AimPoint - _player.transform.position) * (peeking ? _peekLeadFraction : _leadFraction);
        leadCam.y = 0f;
        
        leadCam = Vector3.ClampMagnitude(leadCam, MaxLead);

        transform.position = Vector3.SmoothDamp(transform.position, _player.transform.position + leadCam, ref _velocity, _damping);

        if (_camera != null)
            _camera.transform.position = transform.position + Vector3.up * _currentHeight;
    }

    private void OnDrawGizmosSelected()
    {
        if (_player == null) return;

        Gizmos.color = Color.green;
        Gizmos.DrawLine(_player.transform.position, transform.position);
        Gizmos.DrawWireSphere(transform.position, 0.3f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(_player.transform.position, MaxLead);
    }
}
