using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Health))]
public class EnemyController : MonoBehaviour
{
    // CONFIGURABLE VALUES
    [Header("Movement")]
    [SerializeField] private float _chaseSpeed = 2f;
    [SerializeField] private float _patrolSpeed = 1f;
    [SerializeField] private float _searchSpeed = 1.5f;

    [Header("Awareness")]
    [SerializeField] private float _alertRadius = 12f;
    [SerializeField] private float _giveUpRadius = 18f;
    [SerializeField] private float _aggroTime = 1.5f;
    [SerializeField] private float _searchDuration = 5f;

    [Header("Combat")]
    [SerializeField] private float _attackRange = 2f;
    [SerializeField] private float _grabBreakoutTime = 2.5f;

    [Header("Physics")]
    [Tooltip("Decay rate applied ONLY to external carried velocity (tether yanks, knockback, flings). Same carried-velocity scheme as the player: authored AI velocity is re-written every tick, so external impulses must be carried forward and decayed instead of being erased.")]
    [SerializeField] private float _flingDamping = 3f;
    Vector3 _lastAuthoredMove;

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
    public EnemyNavigator Navigator { get; private set; }
    public IdleState Idle { get; private set; }
    public PatrolState Patrol { get; private set; }
    public SearchingState Searching { get; private set; }
    public ChaseState Chase { get; private set; }
    public AttackState Attack { get; private set; }
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
    public float GrabBreakoutTime => _grabBreakoutTime;
    public PatrolRoute SafeRoute => _safeRoute;
    public float NodePauseTime => _nodePauseTime;
    public float StuckTime => _stuckTime;
    public float RouteRetryTime => _routeRetryTime;
    public bool HasPatrolRoute => _safeRoute != null && _safeRoute.Count > 0;
    public Transform PlayerTransform => _player == null ? null : _player.transform;

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        Health = GetComponent<Health>();

        if (!TryGetComponent(out IRagdollBody ragdoll))
            Debug.LogError($"{name} has no IRagdollBody — it cannot be thrown.", this);
        RagdollBody = ragdoll;
        Navigator = new EnemyNavigator(this);

        Idle = new IdleState(this);
        Patrol = new PatrolState(this);
        Searching = new SearchingState(this);
        Chase = new ChaseState(this);
        Attack = new AttackState(this);
        Grabbed = new GrabbedState(this);
        Ragdoll = new RagdollState(this);
        Dead = new DeadState(this);

        BuildTransitions();
    }

    // The whole transition graph, in one place. States never name each other.
    private void BuildTransitions()
    {
        At(Idle, Chase, new FuncPredicate(() => DistanceToPlayer() <= _alertRadius));
        At(Patrol, Chase, new FuncPredicate(() => DistanceToPlayer() <= _alertRadius));

        At(Chase, Attack, new FuncPredicate(() => DistanceToPlayer() <= _attackRange));
        At(Attack, Chase, new FuncPredicate(() => DistanceToPlayer() > _attackRange));

        At(Chase, Searching, new FuncPredicate(() => Chase.LostPlayer));
        At(Searching, Chase, new FuncPredicate(() => DistanceToPlayer() <= _alertRadius));


        At(Searching, Patrol, new FuncPredicate(() => Searching.SearchExpired && HasPatrolRoute));
        At(Searching, Idle, new FuncPredicate(() => Searching.SearchExpired && !HasPatrolRoute));

        At(Ragdoll, Chase, new FuncPredicate(() => RagdollBody.IsSettled && !Health.IsDead));

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
        _state.SetState(Chase);
    }

    public void EnterGrabbed() => _state.SetState(Grabbed);

    /// <summary>Rope let go without a throw (node destroyed): drop straight into pursuit.</summary>
    public void EndGrab()
    {
        if (_state.Current != Grabbed) return;
        _state.SetState(Chase);
    }

    public void Release(Vector3 velocity)
    {
        if (_state.Current != Grabbed) return;

        // Grabbed.Exit clears isKinematic; a kinematic body ignores the launch velocity.
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

        Vector3 v = transform.forward * speed;
        // Shared carried-velocity scheme (VelocityUtil): external impulses from the
        // tether solver, push verb, and knockback survive the AI's velocity write
        // and decay on their own — otherwise a grappled enemy reads as weightless.
        VelocityUtil.ApplyAuthoredMove(Body, v, _flingDamping, ref _lastAuthoredMove);
    }

    /// <summary>Face the player, ignoring height — otherwise forward tilts and we drive vertically.</summary>
    public void FaceTarget()
    {
        if (_player == null) return;

        Vector3 target = _player.transform.position;
        target.y = transform.position.y;
        transform.LookAt(target);
    }

    /// <summary>Pathfind toward target around walls. Returns true once arrived.</summary>
    public bool PathTo(Vector3 target, float speed) => Navigator.MoveTo(target, speed);

    /// <summary>Walk to a patrol point's area along an A* route.</summary>
    public NavResult PatrolTo(PatrolPoint point, float speed) => Navigator.MoveToArea(point.Position, point.Radius, speed);

    /// <summary>Chase the player along this floor's shared flow field. Returns true when on top of them.</summary>
    public bool PathToPlayer(float speed) => _player != null && Navigator.ChaseTo(_player.transform, speed);

    public void Stop() => VelocityUtil.ApplyAuthoredMove(Body, Vector3.zero, _flingDamping, ref _lastAuthoredMove);

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
        Vector3[] corners = Navigator?.Corners;
        if (corners != null)
        {
            Gizmos.color = Color.yellow;
            for (int i = 1; i < corners.Length; i++)
                Gizmos.DrawLine(corners[i - 1], corners[i]);
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, _alertRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _giveUpRadius);

        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, _attackRange);
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
