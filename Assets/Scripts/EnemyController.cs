using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Health))]
public class EnemyController : MonoBehaviour, IGrabbable, IPathAgent
{
    // CONFIGURABLE VALUES
    [Header("Movement")]
    [SerializeField] private float _chaseSpeed = 2f;
    [SerializeField] private float _patrolSpeed = 1f;
    [SerializeField] private float _searchSpeed = 1.5f;

    [Header("Awareness")]
    [Tooltip("Detects the player inside this radius, if in sight.")]
    [SerializeField] private float _alertRadius = 12f;
    [Tooltip("Aggro keeps contact inside this radius, if in sight.")]
    [SerializeField] private float _giveUpRadius = 18f;
    [Tooltip("Seconds out of contact (out of sight, or beyond Give Up Radius) before aggro turns to searching.")]
    [SerializeField] private float _aggroTime = 1.5f;
    [Tooltip("Height above the enemy's centre that sight is cast from.")]
    [SerializeField] private float _eyeHeight = 0.5f;
    [SerializeField] private float _searchDuration = 5f;

    [Header("Combat")]
    [SerializeField] private float _attackRange = 2f;
    [Tooltip("Seconds between attacks. Moves to the weapon when weapons exist.")]
    [SerializeField, Min(0.05f)] private float _attackCooldown = 1f;

    [Header("Patrol")]
    [Tooltip("Walked while unaware. Empty = idle in place. Edit with the Scene-view handles when this enemy is selected.")]
    [SerializeField] private PatrolRoute _safeRoute = new();
    [Tooltip("Default wait at each patrol point; a point's own Pause Time overrides it.")]
    [SerializeField, Min(0f)] private float _nodePauseTime = 1.5f;
    [Tooltip("Seconds without getting closer before a patrol move counts as blocked (another enemy on the spot, a prop, a jammed doorway).")]
    [SerializeField, Min(0.1f)] private float _stuckTime = 1.5f;
    [Tooltip("When every patrol point fails in a row, wait this long before trying the route again.")]
    [SerializeField, Min(0f)] private float _routeRetryTime = 3f;

    [Header("References")]
    [SerializeField] private PlayerController _player;

    // ENEMY STATES
    private readonly StateMachine _state = new();
    public Rigidbody Body { get; private set; }
    public Health Health { get; private set; }
    public IRagdollBody RagdollBody { get; private set; }
    public GridNavigator Navigator { get; private set; }
    public IdleState Idle { get; private set; }
    public PatrolState Patrol { get; private set; }
    public SearchingState Searching { get; private set; }
    public AggroState Aggro { get; private set; }
    public GrabbedState Grabbed { get; private set; }
    public RagdollState Ragdoll { get; private set; }
    public DeadState Dead { get; private set; }

    // GETTERS
    public EnemyState State => _state.Current is EnemyStateBase s ? s.Id : EnemyState.Idle;
    public float ChaseSpeed => _chaseSpeed;
    public float PatrolSpeed => _patrolSpeed;
    public float SearchSpeed => _searchSpeed;
    public float AlertRadius => _alertRadius;
    public float GiveUpRadius => _giveUpRadius;
    public float AggroTime => _aggroTime;
    public float SearchDuration => _searchDuration;
    public float AttackRange => _attackRange;
    public float AttackCooldown => _attackCooldown;
    public PatrolRoute SafeRoute => _safeRoute;
    public float NodePauseTime => _nodePauseTime;
    public float StuckTime => _stuckTime;
    public float RouteRetryTime => _routeRetryTime;
    public bool HasPatrolRoute => _safeRoute != null && _safeRoute.Count > 0;
    public Transform PlayerTransform => _player == null ? null : _player.transform;
    /// <summary>
    /// Where the hunt was leading when aggro ended. Aggro keeps tracking the player for Aggro
    /// Time after losing contact, so this is where they actually went, not where they slipped
    /// out of view. Search goes here.
    /// </summary>
    public Vector3 LastKnownPlayerPosition { get; private set; }
    /// <summary>Set the first time the enemy aggros; never cleared. The Cautious state reads it.</summary>
    public bool IsCautious { get; private set; }

    private static readonly RaycastHit[] SightHits = new RaycastHit[16];
    private int _sightFrame = -1;
    private bool _canSeePlayer;
    private Collider _sightBlocker;

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        Health = GetComponent<Health>();

        if (!TryGetComponent(out IRagdollBody ragdoll))
            Debug.LogError($"{name} has no IRagdollBody — it cannot be thrown.", this);
        RagdollBody = ragdoll;
        Navigator = new GridNavigator(this);

        Idle = new IdleState(this);
        Patrol = new PatrolState(this);
        Searching = new SearchingState(this);
        Aggro = new AggroState(this);
        Grabbed = new GrabbedState(this);
        Ragdoll = new RagdollState(this);
        Dead = new DeadState(this);

        BuildTransitions();
    }

    // The whole transition graph, in one place. States never name each other.
    private void BuildTransitions()
    {
        At(Idle, Aggro, new FuncPredicate(DetectsPlayer));
        At(Patrol, Aggro, new FuncPredicate(DetectsPlayer));
        At(Searching, Aggro, new FuncPredicate(DetectsPlayer));

        At(Aggro, Searching, new FuncPredicate(() => Aggro.LostPlayer));

        At(Searching, Patrol, new FuncPredicate(() => Searching.SearchExpired && HasPatrolRoute));
        At(Searching, Idle, new FuncPredicate(() => Searching.SearchExpired && !HasPatrolRoute));

        At(Ragdoll, Aggro, new FuncPredicate(() => RagdollBody.IsSettled && !Health.IsDead));

        Any(Dead, new FuncPredicate(() => Health.IsDead));
    }

    private void At(IState from, IState to, IPredicate condition)
        => _state.AddTransition(from, to, condition);

    private void Any(EnemyStateBase to, IPredicate condition)
        => _state.AddAnyTransition(to, condition);

    private void Start()
    {
        if (_player == null) _player = FindAnyObjectByType<PlayerController>();
        _state.SetState(HasPatrolRoute ? Patrol : (EnemyStateBase)Idle);
    }

    private void OnEnable() => Health.OnDamaged += HandleDamaged;

    private void OnDisable() => Health.OnDamaged -= HandleDamaged;

    private void Update()
    {
        _state.Tick();
        DebugDrawPath();
    }

    private void FixedUpdate() => _state.FixedTick();

    // Externally driven transitions. Nothing can poll for these, so they bypass the table.
    public void Alert()
    {
        if (_state.Current is GrabbedState or RagdollState or DeadState) return;
        _state.SetState(Aggro);
    }

    public bool CanBeGrabbed => !Health.IsDead;

    public void EnterGrabbed() => _state.SetState(Grabbed);

    /// <summary>Rope let go without a throw (node destroyed): drop straight into pursuit.</summary>
    public void EndGrab()
    {
        if (_state.Current != Grabbed) return;
        _state.SetState(Aggro);
    }

    /// <summary>One-way: once an enemy has hunted the player it stays on edge for good.</summary>
    public void MarkCautious() => IsCautious = true;

    public void RecordTrailEnd()
    {
        if (_player != null) LastKnownPlayerPosition = _player.transform.position;
    }

    /// <summary>
    /// Placeholder attack until weapons exist: a log line and a red flash toward the player.
    /// Weapons replace this body; AggroState's call site and cadence stay as they are.
    /// </summary>
    public void Attack()
    {
        Debug.Log($"{name} attacks {_player.name}.", this);
        Debug.DrawLine(EyePosition, _player.transform.position, Color.red, 0.15f);
    }

    public void Release(Vector3 velocity)
    {
        if (_state.Current != Grabbed) return;

        // Flattened, keeping speed: an upward throw redirects along the floor, so throw
        // distance depends on the throw's strength, never its angle.
        Ragdoll.Launch(VelocityUtil.Flatten(velocity));
        _state.SetState(Ragdoll);
    }

    // Will be used for patroling and searching
    public void MoveToward(Vector3 target, float speed)
    {
        Vector3 direction = target - transform.position;
        direction.y = 0f;

        if (direction.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

        // Plain authored velocity: nothing pushes an enemy while its AI steers (the rope only
        // acts while grabbed, a state that doesn't steer), so there's no push to carry. When
        // knockback arrives it gets the player's explicit external velocity (VelocityUtil).
        VelocityUtil.SetFlatVelocity(Body, transform.forward * speed);
    }

    /// <summary>Face the player, ignoring height — otherwise forward tilts and we drive vertically.</summary>
    public void FaceTarget()
    {
        if (_player == null) return;

        Vector3 target = _player.transform.position;
        target.y = transform.position.y;
        transform.LookAt(target);
    }

    /// <summary>Pathfind to a fixed point around walls; stops on arrival.</summary>
    public void PathTo(Vector3 target, float speed) => Navigator.MoveTo(target, speed);

    /// <summary>Walk to a patrol point's area along an A* route.</summary>
    public NavResult PatrolTo(PatrolPoint point, float speed) => Navigator.MoveToArea(point.Position, point.Radius, speed);

    /// <summary>Chase the player along this floor's shared flow field.</summary>
    public void PathToPlayer(float speed)
    {
        if (_player != null) Navigator.ChaseTo(_player.transform, speed);
    }

    public void Stop() => VelocityUtil.SetFlatVelocity(Body, Vector3.zero);

    private Vector3 EyePosition => transform.position + Vector3.up * _eyeHeight;

    // Detection: close enough to notice, and in sight.
    private bool DetectsPlayer() => DistanceToPlayer() <= _alertRadius && CanSeePlayer();

    /// <summary>Aggro's contact test: still within the give-up radius, and in sight.</summary>
    public bool InContactWithPlayer() => DistanceToPlayer() <= _giveUpRadius && CanSeePlayer();

    /// <summary>What blocked the last sight check, or null when the player was in view.</summary>
    public Collider SightBlocker => _sightBlocker;

    /// <summary>
    /// A line from eye height to the player crosses nothing solid except characters (the
    /// player, enemies — this one's own eye cubes included). Unlike paths, sight is checked
    /// fresh every frame, so moving props can block it: hiding behind a crate works, while a
    /// prop lower than the eye line doesn't hide anyone. See-through windows arrive with the
    /// Perception step. Cached per frame: several transitions and the aggro state all ask.
    /// </summary>
    public bool CanSeePlayer()
    {
        if (_player == null) return false;
        if (_sightFrame == Time.frameCount) return _canSeePlayer;
        _sightFrame = Time.frameCount;

        Vector3 eye = EyePosition;
        Vector3 toPlayer = _player.transform.position - eye;
        float distance = toPlayer.magnitude;
        _canSeePlayer = true;
        _sightBlocker = null;
        if (distance < 0.01f) return true;

        int count = Physics.RaycastNonAlloc(eye, toPlayer / distance, SightHits, distance, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            if (IsCharacter(SightHits[i].collider)) continue;
            _canSeePlayer = false;
            _sightBlocker = SightHits[i].collider;
            break;
        }
        return _canSeePlayer;
    }

    // Characters see past each other: the player's own colliders (gun included) and any enemy's.
    private bool IsCharacter(Collider collider)
    {
        Rigidbody body = collider.attachedRigidbody;
        if (body == null) return false;
        return body.transform == _player.transform || body.TryGetComponent(out EnemyController _);
    }

    public float DistanceToPlayer()
    {
        if (_player == null) return float.MaxValue;

        Vector3 delta = _player.transform.position - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    private void HandleDamaged(float amount) => Alert();

    private void OnValidate()
    {
        // Give-up must sit outside alert, or the enemy aggros and de-aggros in the same frame.
        _giveUpRadius = Mathf.Max(_giveUpRadius, _alertRadius + 1f);
        _attackRange = Mathf.Min(_attackRange, _alertRadius);
    }

    // Play-mode path overlay: visible in the Scene view without selecting the enemy.
    private void DebugDrawPath()
    {
        Vector3[] corners = Navigator.Corners;
        if (corners == null) return;

        for (int i = 1; i < corners.Length; i++)
            Debug.DrawLine(corners[i - 1], corners[i], Color.yellow);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, _alertRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _giveUpRadius);

        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, _attackRange);

        // Sight line in play: green when the player is in view, red when something static is in the way.
        if (Application.isPlaying && _player != null)
        {
            Gizmos.color = CanSeePlayer() ? Color.green : Color.red;
            Gizmos.DrawLine(EyePosition, _player.transform.position);
        }
    }

    // Every enemy's route, faintly, all the time: with many enemies you can see who walks what
    // without selecting each one. The selected enemy's areas get coloured handles on top
    // (EnemyControllerEditor).
    private void OnDrawGizmos()
    {
        if (!HasPatrolRoute) return;

        Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
        int count = _safeRoute.Count;
        for (int i = 0; i < count; i++)
        {
            PatrolPoint point = _safeRoute[i];
            DrawFlatCircle(point.Position, point.Radius);

            bool last = i == count - 1;
            if (!last) Gizmos.DrawLine(point.Position, _safeRoute[i + 1].Position);
            else if (_safeRoute.Mode == PatrolMode.Loop && count > 2) Gizmos.DrawLine(point.Position, _safeRoute[0].Position);
        }
    }

    private static void DrawFlatCircle(Vector3 centre, float radius)
    {
        const int segments = 24;
        Vector3 previous = centre + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            Vector3 next = centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
    }
}
