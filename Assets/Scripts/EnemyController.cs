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

    [Header("Patrol")]
    [SerializeField] private Transform[] _waypoints;
    [SerializeField] private PlayerController _player;

    [Header("Debug")]
    [SerializeField] private bool _setToGrabState = false;


    // ENEMY STATES
    private readonly StateMachine _state = new();
    public Rigidbody Body { get; private set; }
    public Health Health { get; private set; }
    public IRagdollBody RagdollBody { get; private set; }
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
    public Transform[] Waypoints => _waypoints;
    public bool HasPatrolRoute => _waypoints != null && _waypoints.Length > 0;
    public Transform PlayerTransform => _player == null ? null : _player.transform;

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        Health = GetComponent<Health>();

        if (!TryGetComponent(out IRagdollBody ragdoll))
            Debug.LogError($"{name} has no IRagdollBody — it cannot be thrown.", this);
        RagdollBody = ragdoll;

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

    private void Update() => _state.Tick();

    private void FixedUpdate() => _state.FixedTick();

    // Externally driven transitions. Nothing can poll for these, so they bypass the table.
    public void Alert()
    {
        if (_state.Current is GrabbedState or RagdollState or DeadState) return;
        _state.SetState(Chase);
    }

    public void EnterGrabbed() => _state.SetState(Grabbed);

    public void Release(Vector3 velocity)
    {
        if (_state.Current != Grabbed) return;

        // Grabbed.Exit clears isKinematic; a kinematic body ignores the launch velocity.
        Ragdoll.Launch(YConstraint.Flatten(velocity));
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
        v.y = Body.linearVelocity.y;   // preserve gravity
        Body.linearVelocity = v;
    }

    /// <summary>Face the player, ignoring height — otherwise forward tilts and we drive vertically.</summary>
    public void FaceTarget()
    {
        if (_player == null) return;

        Vector3 target = _player.transform.position;
        target.y = transform.position.y;
        transform.LookAt(target);
    }

    public void Stop() => Body.linearVelocity = new Vector3(0f, Body.linearVelocity.y, 0f);

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

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, _alertRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _giveUpRadius);

        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, _attackRange);

        if (!HasPatrolRoute) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < _waypoints.Length; i++)
        {
            if (_waypoints[i] == null) continue;

            Gizmos.DrawWireCube(_waypoints[i].position, Vector3.one * 0.3f);

            Transform next = _waypoints[(i + 1) % _waypoints.Length];
            if (next != null) Gizmos.DrawLine(_waypoints[i].position, next.position);
        }
    }
}
