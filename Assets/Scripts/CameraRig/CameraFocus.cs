using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// The top-down camera rig. A proxy point follows the player, leaning toward the cursor; the
/// camera hangs above it. This script places both itself and the camera every LateUpdate, on
/// purpose: two scripts would race over which moves first.
///
/// Normal play leans a little toward the cursor. Peek throws the view out to the frame's edge in
/// the cursor's direction and zooms out to Peek Height. How far the view may lean is derived from
/// the camera every frame, never serialized, so no zoom or aspect can push the player off screen.
/// </summary>
public class CameraFocus : MonoBehaviour
{
    [Tooltip("What the camera follows: any component implementing ICameraTarget (the PlayerController). Empty = the first one found.")]
    [FormerlySerializedAs("_player")]
    [SerializeField] private MonoBehaviour _target;
    [SerializeField] private Camera _camera;

    [Header("Lead")]
    [Tooltip("Normal play: how far the view leans toward the cursor, as a fraction of the cursor's distance.")]
    [SerializeField] private float _leadFraction = 0.08f;
    [Tooltip("Follow smoothing in normal play, roughly the seconds the view takes to catch up.")]
    [SerializeField] private float _damping = 0.25f;
    [Tooltip("Smoothing while peeking. Lower than Damping, so Shift throws the view out fast.")]
    [SerializeField] private float _peekDamping = 0.08f;

    [Header("Height")]
    [Tooltip("Camera height above the player. An orthographic camera shows its own Size at this height.")]
    [SerializeField] private float _camHeight = 20f;
    [Tooltip("Height while peeking. Peek Height / Cam Height is the zoom-out, for either projection.")]
    [SerializeField] private float _peekHeight = 28f;
    [Tooltip("Smoothing of the zoom in and out.")]
    [SerializeField] private float _heightDamping = 0.4f;

    [Header("Framing")]
    [Tooltip("How far in from the screen edge the player is always kept, as a fraction of half the screen.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float _playerMargin = 0.25f;

    private ICameraTarget _follow;
    private Vector3 _followVelocity;
    private float _currentHeight;
    private float _heightVelocity;
    // Kept while the cursor sits right on the player, so a peek never snaps to "no direction".
    private Vector3 _peekDirection = Vector3.forward;
    // The orthographic Size authored on the camera, at Cam Height. Height scales it from here.
    private float _baseOrthoSize;

    private void Awake()
    {
        _follow = _target as ICameraTarget ?? FindTarget();
        if (_camera == null) _camera = FindAnyObjectByType<Camera>();
        if (_follow == null || _camera == null)
        {
            Debug.LogError($"{name} needs an ICameraTarget and a camera — camera rig disabled.", this);
            enabled = false;
            return;
        }

        _baseOrthoSize = _camera.orthographicSize;
        _currentHeight = _camHeight;
        transform.position = _follow.Position;
    }

    private void OnValidate()
    {
        if (_target != null && _target is not ICameraTarget)
        {
            Debug.LogWarning($"{_target.name}'s {_target.GetType().Name} isn't an ICameraTarget — cleared.", this);
            _target = null;
        }
    }

    private static ICameraTarget FindTarget()
    {
        foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>())
            if (behaviour is ICameraTarget target) return target;
        return null;
    }

    private void LateUpdate()
    {
        bool peeking = _follow.WantsPeek;

        _currentHeight = Mathf.SmoothDamp(_currentHeight, peeking ? _peekHeight : _camHeight, ref _heightVelocity, _heightDamping);

        Vector3 target = _follow.Position + Lead(peeking);
        transform.position = Vector3.SmoothDamp(transform.position, target, ref _followVelocity, peeking ? _peekDamping : _damping);

        PlaceCamera();
    }

    private Vector3 Lead(bool peeking)
    {
        Vector3 toAim = _follow.AimPoint - _follow.Position;
        toAim.y = 0f;
        return peeking ? PeekLead(toAim) : ClampToFrame(toAim * _leadFraction);
    }

    private void PlaceCamera()
    {
        _camera.transform.position = transform.position + Vector3.up * _currentHeight;
        // Moving an orthographic camera up doesn't zoom it, so height drives its Size instead.
        if (_camera.orthographic) _camera.orthographicSize = OrthoSizeAt(_currentHeight);
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
    // can lean each way while the player stays on screen.
    private Vector2 LeadLimits(float height)
    {
        float halfHeight = _camera.orthographic
            ? OrthoSizeAt(height)
            : height * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float keep = 1f - _playerMargin;
        return new Vector2(halfHeight * _camera.aspect * keep, halfHeight * keep);
    }

    // Orthographic Size (half the view's height) at a given camera height: the authored Size at
    // Cam Height, scaled by height. Before Awake (editor gizmos) the camera's own Size is the base.
    private float OrthoSizeAt(float height)
    {
        float baseSize = _baseOrthoSize > 0f ? _baseOrthoSize : _camera.orthographicSize;
        return _camHeight > 0f ? baseSize * height / _camHeight : baseSize;
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
        // Before Awake (edit mode) the target is only the serialized reference.
        ICameraTarget target = _follow ?? _target as ICameraTarget;
        if (target == null || _camera == null) return;

        Gizmos.color = Color.green;
        Gizmos.DrawLine(target.Position, transform.position);
        Gizmos.DrawWireSphere(transform.position, 0.3f);

        // The furthest the view can lean: a rectangle around the player.
        ScreenAxes(out Vector3 right, out Vector3 up);
        Vector2 limits = LeadLimits(_currentHeight > 0f ? _currentHeight : _camHeight);
        Vector3 centre = target.Position;
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
