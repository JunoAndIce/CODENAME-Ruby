using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(Health))]
public class EnemyController : MonoBehaviour, IGrabbable, IPathAgent, IKnockdown, IBulletTarget
{
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

    [Header("Dropped")]
    [Tooltip("An enemy let go while being spun always flings out. One that wasn't (being dragged, say) flings if it's moving at least this fast across the floor, and otherwise spins in place.")]
    [SerializeField, Min(0f)] private float _dropFlingSpeed = 3f;
    [Tooltip("How fast a dropped enemy starts spinning, upright, in radians per second.")]
    [SerializeField, Min(0f)] private float _dropSpinSpeed = 12f;
    [Tooltip("Seconds the spin takes to wind down to a stop, after which the enemy is back in Aggro.")]
    [SerializeField, Min(0f)] private float _dropSpinTime = 3f;
    [Tooltip("How bouncy a spinning enemy's hit on another enemy is: 0 stops dead along the hit, 1 keeps all the speed.")]
    [SerializeField, Range(0f, 1f)] private float _topBounce = 0.6f;

    [Header("Dead")]
    [Tooltip("How bright a corpse's colours are, as a share of the living enemy's, so the dead read at a glance.")]
    [SerializeField, Range(0f, 1f)] private float _corpseBrightness = 0.35f;

    [Header("Patrol")]
    [Tooltip("Walked while unaware. Empty = idle in place. One point 4 m or more across (a circle's diameter, a rectangle's longer side) is wandered rather than stood on. Edit with the Scene-view handles when this enemy is selected.")]
    [SerializeField] private PatrolRoute _safeRoute = new();
    [Tooltip("Default wait at each patrol point; a point's own Pause Time overrides it.")]
    [SerializeField, Min(0f)] private float _nodePauseTime = 1.5f;
    [Tooltip("Seconds without getting closer before a patrol move counts as blocked (another enemy on the spot, a prop, a jammed doorway).")]
    [SerializeField, Min(0.1f)] private float _stuckTime = 1.5f;
    [Tooltip("When every patrol point fails in a row, wait this long before trying the route again.")]
    [SerializeField, Min(0f)] private float _routeRetryTime = 3f;

    [Header("Gun")]
    [Tooltip("Unarmed, an enemy goes for a free dropped gun within this radius. 0 = never re-arms.")]
    [SerializeField, Min(0f)] private float _rearmRadius = 10f;

    [Header("References")]
    [SerializeField] private PlayerController _player;

    private readonly StateMachine _state = new();
    private CapsuleCollider _capsule;
    // Velocity going into this physics step. Collision callbacks arrive after the solver has
    // already spent the speed, so a hit is worked out from this, not from the body.
    private Vector3 _preStepVelocity;
    private float _nextRearm;   // after giving up on a gun, no new attempt before this time
    // Creation order, so exactly one of two colliding tops handles their hit.
    private static int _created;
    private int _order;

    public Rigidbody Body { get; private set; }
    public Health Health { get; private set; }
    public IRagdollBody RagdollBody { get; private set; }
    public GridNavigator Navigator { get; private set; }
    public IdleState Idle { get; private set; }
    public PatrolState Patrol { get; private set; }
    public SearchingState Searching { get; private set; }
    public AggroState Aggro { get; private set; }
    public RearmState Rearm { get; private set; }
    public GrabbedState Grabbed { get; private set; }
    public RagdollState Ragdoll { get; private set; }
    public DeadState Dead { get; private set; }
    /// <summary>The enemy's gun hand, or null on an enemy built without one.</summary>
    public GunHolder Arms { get; private set; }
    public bool IsArmed => Arms != null && Arms.IsArmed;
    /// <summary>The player is on her feet. Nobody shoots a body.</summary>
    public bool PlayerIsUp => _player != null && _player.CanAct;
    /// <summary>Only on the chain does an enemy, alive or dead, stop bullets: a shield. Free, it lets them through.</summary>
    public bool StopsBullets => IsHeld;

    /// <summary>Lets the gun go with the body's velocity, so it skids on the way the enemy was moving.</summary>
    public void DropGun()
    {
        if (Arms != null) Arms.Drop(Body.linearVelocity);
    }

    /// <summary>
    /// After giving up on every gun in reach, wait Route Retry Time before looking again. Without
    /// it, an enemy with only unreachable guns around would flicker between hunting and re-arming.
    /// </summary>
    public void WaitBeforeRearming() => _nextRearm = Time.time + _routeRetryTime;

    private bool WantsGun() => Arms != null && !Arms.IsArmed && _rearmRadius > 0f && Time.time >= _nextRearm && Rearm.CanFindGun();

    public float ChaseSpeed => _chaseSpeed;
    public float PatrolSpeed => _patrolSpeed;
    public float SearchSpeed => _searchSpeed;
    public float AggroTime => _aggroTime;
    public float SearchDuration => _searchDuration;
    public float DropSpinSpeed => _dropSpinSpeed;
    public float DropSpinTime => _dropSpinTime;
    public PatrolRoute SafeRoute => _safeRoute;
    public float NodePauseTime => _nodePauseTime;
    public float StuckTime => _stuckTime;
    public float RouteRetryTime => _routeRetryTime;
    public float RearmRadius => _rearmRadius;
    /// <summary>The body capsule's radius in world units: how far its surface sits from its centre.</summary>
    public float BodyRadius => _capsule.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z));
    public bool HasPatrolRoute => _safeRoute != null && _safeRoute.Count > 0;
    public Transform PlayerTransform => _player == null ? null : _player.transform;
    /// <summary>Ragdolling in an upright spin: a spinning top that knocks other enemies away.</summary>
    public bool IsTop => _state.Current == Ragdoll && RagdollBody.IsSpinning;
    /// <summary>
    /// Where the hunt was leading when aggro ended, which is where search goes. Aggro keeps
    /// tracking the player for Aggro Time after losing contact, so this is where they went, not
    /// where they slipped out of view.
    /// </summary>
    public Vector3 LastKnownPlayerPosition { get; private set; }

    private const float WanderSpotRadius = 0.5f;
    private static readonly RaycastHit[] SightHits = new RaycastHit[16];
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private int _sightFrame = -1;
    private bool _canSeePlayer;
    private Collider _sightBlocker;

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        _order = ++_created;
        Health = GetComponent<Health>();

        if (!TryGetComponent(out IRagdollBody ragdoll))
            Debug.LogError($"{name} has no IRagdollBody — it cannot be thrown.", this);
        RagdollBody = ragdoll;
        Navigator = new GridNavigator(this);
        Arms = GetComponent<GunHolder>();

        Idle = new IdleState(this);
        Patrol = new PatrolState(this);
        Searching = new SearchingState(this);
        Aggro = new AggroState(this);
        Rearm = new RearmState(this);
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

        // Unarmed with a free gun in reach: fetching it comes before anything else the AI wants.
        At(Idle, Rearm, new FuncPredicate(WantsGun));
        At(Patrol, Rearm, new FuncPredicate(WantsGun));
        At(Searching, Rearm, new FuncPredicate(WantsGun));
        At(Aggro, Rearm, new FuncPredicate(WantsGun));
        // Always back to the hunt: an enemy only loses its gun to Ruby, so it knows she's around.
        At(Rearm, Aggro, new FuncPredicate(() => Rearm.Armed || Rearm.GaveUp));

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

    private void FixedUpdate()
    {
        _state.FixedTick();
        _preStepVelocity = Body.linearVelocity;
    }

    // A spinning top that hits another enemy trades momentum with it along the line between them,
    // by weight, at Top Bounce: it bounces off and keeps spinning, and the one it hit is sent off
    // at its share. Whether that knocks it down is ImpactDamage's call, by the shared Knockdown
    // Speed, so a slow graze only shoves. Each pair is handled once: the top, or of two tops, the
    // one created first.
    private void OnCollisionEnter(Collision collision)
    {
        if (!IsTop || collision.rigidbody == null) return;
        if (!collision.rigidbody.TryGetComponent(out EnemyController other)) return;
        if (other.Health.IsDead || other._state.Current == other.Grabbed) return;
        if (other.IsTop && other._order < _order) return;

        Vector3 n = other.Body.position - Body.position;
        n.y = 0f;
        if (n.sqrMagnitude < 0.0001f) return;
        n.Normalize();

        Vector3 vTop = _preStepVelocity;
        Vector3 vOther = other._preStepVelocity;
        float approach = Vector3.Dot(vTop - vOther, n);
        if (approach <= 0f) return;

        float mTop = Body.mass;
        float mOther = other.Body.mass;
        float exchange = (1f + _topBounce) * approach / (mTop + mOther);
        Vector3 topAfter = vTop - n * (exchange * mOther);
        Vector3 otherAfter = vOther + n * (exchange * mTop);

        RagdollBody.Push(new Vector3(topAfter.x, Body.linearVelocity.y, topAfter.z));
        other.TakeHit(new Vector3(otherAfter.x, other.Body.linearVelocity.y, otherAfter.z));
    }

    // Already ragdolling (a top or a tumble), it keeps doing that at the new velocity. On its feet
    // it's shoved: its AI takes the body back next step, unless the hit also knocked it down, in
    // which case the knockdown launches it at this velocity, whichever callback runs first.
    private void TakeHit(Vector3 velocity)
    {
        if (_state.Current == Ragdoll) RagdollBody.Push(velocity);
        else Body.linearVelocity = velocity;
    }

    /// <summary>
    /// Knocked off its feet by a hit (ImpactDamage): ragdolls tumbling at the velocity the hit left
    /// it with, then gets up into Aggro. Held on the chain or already down, the hit only hurts, so
    /// a swung enemy keeps swinging.
    /// </summary>
    public void KnockDown()
    {
        if (Health.IsDead || _state.Current is GrabbedState or RagdollState or DeadState) return;
        DropGun();
        Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity));
        _state.SetState(Ragdoll);
    }

    // Externally driven transitions. Nothing can poll for these, so they bypass the table.
    public void Alert()
    {
        if (_state.Current is GrabbedState or RagdollState or DeadState) return;
        _state.SetState(Aggro);
    }

    // Corpses too: a dead enemy stays a body the chain can swing and throw.
    public bool CanBeGrabbed => true;

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld { get; private set; }

    public void EnterGrabbed()
    {
        DropGun();
        IsHeld = true;
        if (Health.IsDead) Dead.Grab();
        else _state.SetState(Grabbed);
    }

    /// <summary>
    /// Rope let go without a throw (dropped, or the node destroyed): the enemy spins upright until
    /// the spin winds down, then is back in Aggro. A spun enemy flings out on its momentum while it
    /// spins, turning the way it orbited; a slower one spins where it stands. A corpse just tumbles.
    /// </summary>
    public void EndGrab(bool wasSpun)
    {
        IsHeld = false;
        if (Health.IsDead)
        {
            Dead.LetGo(Body.linearVelocity);
            return;
        }
        if (_state.Current != Grabbed) return;

        // Being spun always flings: a speed test alone would be a coin flip whenever Drop Fling
        // Speed sits near Spin Speed, since the spin's measured speed wobbles each step.
        Vector3 velocity = VelocityUtil.Flatten(Body.linearVelocity);
        bool flung = wasSpun || velocity.magnitude >= _dropFlingSpeed;
        float direction = 1f;
        if (flung && _player != null)
        {
            // For a body orbiting a point, (offset × velocity).y has the sign of its turn.
            Vector3 offset = transform.position - _player.transform.position;
            direction = Vector3.Cross(offset, velocity).y >= 0f ? 1f : -1f;
        }

        Ragdoll.Spin(flung ? velocity : Vector3.zero, direction);
        _state.SetState(Ragdoll);
    }

    /// <summary>
    /// Darkens every renderer on the enemy to Corpse Brightness, through property blocks so the
    /// shared materials (and every other enemy using them) are untouched.
    /// </summary>
    public void Darken()
    {
        var block = new MaterialPropertyBlock();
        foreach (Renderer rendered in GetComponentsInChildren<Renderer>())
        {
            Material material = rendered.sharedMaterial;
            if (material == null) continue;

            Color colour = material.HasProperty(BaseColorId) ? material.GetColor(BaseColorId) : material.color;
            colour = new Color(colour.r * _corpseBrightness, colour.g * _corpseBrightness, colour.b * _corpseBrightness, colour.a);

            rendered.GetPropertyBlock(block);
            block.SetColor(BaseColorId, colour);   // URP Lit
            block.SetColor(ColorId, colour);       // built-in and sprite shaders
            rendered.SetPropertyBlock(block);
        }
    }

    public void RecordTrailEnd()
    {
        if (_player != null) LastKnownPlayerPosition = _player.transform.position;
    }

    public void Release(Vector3 velocity)
    {
        IsHeld = false;
        if (Health.IsDead)
        {
            Dead.LetGo(velocity);
            return;
        }
        if (_state.Current != Grabbed) return;

        // Flattened, keeping speed: an upward throw redirects along the floor, so throw
        // distance depends on the throw's strength, never its angle.
        Ragdoll.Launch(VelocityUtil.Flatten(velocity));
        _state.SetState(Ragdoll);
    }

    public void MoveToward(Vector3 target, float speed)
    {
        Vector3 direction = target - transform.position;
        direction.y = 0f;

        if (direction.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        VelocityUtil.SetFlatVelocity(Body, PushLimit.Limit(_capsule, transform.forward * speed));
    }

    /// <summary>
    /// Backs toward a spot while facing the player: an armed enemy giving ground keeps its gun on
    /// her instead of turning its back, which MoveToward would do.
    /// </summary>
    public void StepAway(Vector3 spot, float speed)
    {
        FaceTarget();
        Vector3 direction = spot - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            Stop();
            return;
        }
        VelocityUtil.SetFlatVelocity(Body, PushLimit.Limit(_capsule, direction.normalized * speed));
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
    public NavResult PatrolTo(PatrolPoint point, float speed) => Navigator.MoveToArea(point.Area, speed);

    /// <summary>
    /// A random spot in a wandered area, at least a quarter of the way across it from here (half a
    /// circle's radius), so each walk reads as roaming rather than shuffling.
    /// </summary>
    public bool TryPickWanderSpot(PatrolPoint point, out Vector3 spot)
    {
        GridArea area = point.Area;
        return Navigator.PickSpotInArea(area, area.Span * 0.25f, out spot);
    }

    /// <summary>
    /// Walk to a wander spot, as an area just big enough to arrive in: with a wider one, another
    /// enemy standing on the spot would count as arrived instead of blocked.
    /// </summary>
    public NavResult WanderTo(Vector3 spot, float speed) => WalkTo(spot, WanderSpotRadius, speed);

    /// <summary>Walk until within reach of a point, along an A* route.</summary>
    public NavResult WalkTo(Vector3 point, float reach, float speed) => Navigator.MoveToArea(GridArea.Circle(point, reach), speed);

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
    /// A line from eye height to the player that crosses nothing solid except characters. Checked
    /// fresh, so a crate can hide the player while a prop below the eye line can't. Cached per
    /// frame: several transitions and the aggro state all ask.
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

    // Characters see past each other.
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

        // The gun's engagement band: fires out to Range, closes in only to Min Distance. In edit
        // mode Arms isn't set yet, so read the gun under the hand directly.
        Gun gun = Arms != null ? Arms.Held : GetComponentInChildren<Gun>();
        if (gun != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(transform.position, gun.Range);
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(transform.position, gun.MinDistance);
        }

        // Sight line in play: green when the player is in view, red when something static is in the way.
        if (Application.isPlaying && _player != null)
        {
            Gizmos.color = CanSeePlayer() ? Color.green : Color.red;
            Gizmos.DrawLine(EyePosition, _player.transform.position);
        }
    }

    // Every enemy's route, faintly, so you can see who walks what without selecting each one.
    private void OnDrawGizmos()
    {
        if (!HasPatrolRoute) return;

        Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
        int count = _safeRoute.Count;
        for (int i = 0; i < count; i++)
        {
            PatrolPoint point = _safeRoute[i];
            DrawFlatArea(point.Area);

            // A random route has no order, so lines between its points would only mislead.
            if (_safeRoute.Mode == PatrolMode.Random) continue;

            bool last = i == count - 1;
            if (!last) Gizmos.DrawLine(point.Position, _safeRoute[i + 1].Position);
            else if (_safeRoute.Mode == PatrolMode.Loop && count > 2) Gizmos.DrawLine(point.Position, _safeRoute[0].Position);
        }
    }

    private static void DrawFlatArea(GridArea area)
    {
        if (area.Shape == AreaShape.Rectangle)
            Gizmos.DrawWireCube(area.Centre, new Vector3(area.HalfSize.x * 2f, 0f, area.HalfSize.y * 2f));
        else
            DrawFlatCircle(area.Centre, area.HalfSize.x);
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
