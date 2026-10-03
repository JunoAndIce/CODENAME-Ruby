using UnityEngine;


public class CameraFocus : MonoBehaviour
{
    [SerializeField] private PlayerController _player;
    [SerializeField] private Camera _camera;

    [Header("Lead")]
    [Tooltip("Normal play: how far the view leans toward the cursor, as a fraction of the cursor's distance.")]
    [SerializeField] private float _leadFraction = 0.08f;
    [SerializeField] private float _damping = 0.25f;
    [Tooltip("Smoothing while peeking. Lower than Damping, so Shift throws the view out fast.")]
    [SerializeField] private float _peekDamping = 0.08f;

    [Header("Height")]
    [SerializeField] private float _camHeight = 20f;
    [SerializeField] private float _peekHeight = 28f;
    [SerializeField] private float _heightDamping = 0.4f;

    [Header("Framing")]
    [Tooltip("How far in from the screen edge the player is always kept, as a fraction of half the screen.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float _playerMargin = 0.25f;

    private Vector3 _velocity;
    private float _currentHeight;
    private float _heightVelocity;
    // Kept while the cursor sits right on the player, so a peek never snaps to "no direction".
    private Vector3 _peekDirection = Vector3.forward;

   // Current height of the camera after Awake, call this value to always get the current height
    public float Height => _currentHeight;

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

        Vector3 toAim = _player.AimPoint - _player.transform.position;
        toAim.y = 0f;
        Vector3 lead = peeking ? PeekLead(toAim) : ClampToFrame(toAim * _leadFraction);

        transform.position = Vector3.SmoothDamp(transform.position, _player.transform.position + lead, ref _velocity,
            peeking ? _peekDamping : _damping);

        if (_camera != null)
            _camera.transform.position = transform.position + Vector3.up * _currentHeight;
    }

    // Peek: always out to the frame's edge in the cursor's direction. Only the direction counts,
    // never the distance, so the view can't be held near centre — a cursor right by the player
    // swings it to whichever way the cursor sits.
    private Vector3 PeekLead(Vector3 toAim)
    {
        if (toAim.sqrMagnitude > 0.0001f) _peekDirection = toAim.normalized;

        ScreenAxes(out Vector3 right, out Vector3 up);
        Vector2 limits = LeadLimits(_currentHeight);
        float x = Mathf.Abs(Vector3.Dot(_peekDirection, right));
        float y = Mathf.Abs(Vector3.Dot(_peekDirection, up));
        // Stretch the direction until it meets the nearer of the frame's sides.
        float reach = Mathf.Min(x > 0.0001f ? limits.x / x : float.MaxValue, y > 0.0001f ? limits.y / y : float.MaxValue);
        return _peekDirection * reach;
    }

    // Normal lead, kept inside the frame: sideways by half the screen's width, up and down by half
    // its height. A single radius would have to use the smaller one and cut sideways reach short.
    private Vector3 ClampToFrame(Vector3 lead)
    {
        ScreenAxes(out Vector3 right, out Vector3 up);
        Vector2 limits = LeadLimits(_currentHeight);
        float x = Mathf.Clamp(Vector3.Dot(lead, right), -limits.x, limits.x);
        float y = Mathf.Clamp(Vector3.Dot(lead, up), -limits.y, limits.y);
        return right * x + up * y;
    }

    // Half the visible floor along screen right and up, less Player Margin: the furthest the view
    // can lean each way while the player stays on screen. Derived from the FOV and height, never
    // serialized, so a different height or aspect can't push the player off screen.
    private Vector2 LeadLimits(float height)
    {
        if (_camera == null) return Vector2.zero;

        // Orthographic: the view's size is set directly and height doesn't zoom.
        float halfHeight = _camera.orthographic
            ? _camera.orthographicSize
            : height * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float keep = 1f - _playerMargin;
        return new Vector2(halfHeight * _camera.aspect * keep, halfHeight * keep);
    }

    // The screen's right and up, laid flat on the floor. Read from the camera rather than assumed
    // to be world X/Z, so a camera turned or swayed about its view axis still frames correctly.
    private void ScreenAxes(out Vector3 right, out Vector3 up)
    {
        right = Vector3.ProjectOnPlane(_camera.transform.right, Vector3.up).normalized;
        up = Vector3.ProjectOnPlane(_camera.transform.up, Vector3.up).normalized;
    }

    private void OnDrawGizmosSelected()
    {
        if (_player == null || _camera == null) return;

        Gizmos.color = Color.green;
        Gizmos.DrawLine(_player.transform.position, transform.position);
        Gizmos.DrawWireSphere(transform.position, 0.3f);

        // The furthest the view can lean: a rectangle around the player.
        ScreenAxes(out Vector3 right, out Vector3 up);
        Vector2 limits = LeadLimits(_currentHeight > 0f ? _currentHeight : _camHeight);
        Vector3 centre = _player.transform.position;
        Vector3 a = centre + right * limits.x + up * limits.y;
        Vector3 b = centre - right * limits.x + up * limits.y;
        Vector3 c = centre - right * limits.x - up * limits.y;
        Vector3 d = centre + right * limits.x - up * limits.y;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine(b, c);
        Gizmos.DrawLine(c, d);
        Gizmos.DrawLine(d, a);
    }
}
