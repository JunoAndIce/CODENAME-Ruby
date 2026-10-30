using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(Health))]
public class EnemyController : MonoBehaviour, IGrabbable, IPathAgent, IKnockdown, IBulletTarget, IAnimatedCharacter
{
    [Header("Movement")]
    [SerializeField] private float _chaseSpeed = 2f;
    [SerializeField] private float _patrolSpeed = 1f;
    [SerializeField] private float _searchSpeed = 1.5f;
    [Tooltip("How fast the body turns while walking a path, in degrees a second: lower makes wider arcs round bends. The legs' own turn on the model (CharacterAnimator's Turn Speed) is separate.")]
    [SerializeField, Min(1f)] private float _turnSpeed = 720f;

    [Header("Awareness")]
    [Tooltip("How far he sees in front of him, within View Angle, if in sight.")]
    [FormerlySerializedAs("_alertRadius")] [SerializeField, Min(0f)] private float _viewRange = 12f;
    [Tooltip("How wide the view in front of him is, in degrees, centred on his facing.")]
    [SerializeField, Range(1f, 360f)] private float _viewAngle = 100f;
    [Tooltip("Whichever way he faces, he notices Ruby this close, if in sight, so she can't stand right behind him.")]
    [SerializeField, Min(0f)] private float _closeRadius = 2f;
    [Tooltip("Aggro keeps contact inside this radius, if in sight. Sits outside View Range.")]
    [SerializeField] private float _giveUpRadius = 18f;
    [Tooltip("Seconds out of contact (out of sight, or beyond Give Up Radius) before aggro turns to searching.")]
    [SerializeField] private float _aggroTime = 1.5f;
    [Tooltip("Height above the enemy's centre that sight is cast from.")]
    [SerializeField] private float _eyeHeight = 0.5f;
    [Tooltip("How long he searches once he has reached the spot, in seconds. The walk there doesn't count.")]
    [SerializeField] private float _searchDuration = 5f;
    [Tooltip("The longest he walks to a search spot before searching wherever he has got to, in seconds. Keeps one he can't reach (blocked, or a crowded doorway) from walking forever.")]
    [SerializeField, Min(1f)] private float _searchTravelLimit = 20f;
    [Tooltip("While he sees Ruby, he calls out her position this often, in seconds (and at once on first sight). Speech reaches others immediately; the shared pool takes longer.")]
    [SerializeField, Min(0.1f)] private float _speakInterval = 1f;
    [Tooltip("How far he notices a loose body (thrown, tumbling or held) moving at Notice Speed or faster, with a clear line to it. Corpses and dropped guns aren't limited by this: see Body Notice Range and View Range.")]
    [SerializeField, Min(0f)] private float _motionRadius = 15f;
    [Tooltip("How close a corpse must be for him to notice it, in the same view as for Ruby (cone plus close circle). A corpse is looked at closer than Ruby is. A dropped gun is noticed out to View Range instead.")]
    [SerializeField, Min(0f)] private float _bodyNoticeRange = 6f;
    [Tooltip("Seconds between his sweeps for moving bodies, corpses and loose guns. Each officer starts at a random offset so a squad doesn't sweep on the same step.")]
    [SerializeField, Min(0.05f)] private float _scanInterval = 0.2f;

    [Header("Looking")]
    [Tooltip("How long he looks around a spot he went to check, in seconds, before standing down. A search for an attack looks for Search Duration instead.")]
    [SerializeField, Min(0.1f)] private float _lookTime = 1.5f;
    [Tooltip("How long he holds still after finding nothing, in seconds: the gesture, until there is an animation for it.")]
    [SerializeField, Min(0f)] private float _standDownTime = 1f;
    [Tooltip("How far, in degrees, his body turns to each side of where he was facing while he looks around.")]
    [SerializeField, Range(0f, 180f)] private float _lookSweepAngle = 70f;
    [Tooltip("Seconds for one full sweep, left to right and back.")]
    [SerializeField, Min(0.2f)] private float _lookSweepPeriod = 2f;

    [Header("Grabbed")]
    [Tooltip("How fast the gun pops up out of the officer's hand when the chain grabs him, in m/s: a small hop, to sell the grab.")]
    [SerializeField, Min(0f)] private float _grabPopUp = 3f;
    [Tooltip("How fast the popped gun moves away from his body, in m/s, so it clears his hands.")]
    [SerializeField, Min(0f)] private float _grabPopOut = 2f;

    [Header("Getting up")]
    [Tooltip("Seconds a surviving enemy stands getting up after a fall, unable to move or shoot, before it hunts again.")]
    [SerializeField, Min(0f)] private float _getUpTime = 1f;

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
    private float _intendedSpeed;   // the last move's speed, 0 once stopped
    private float _nextRearm;   // after giving up on a gun, no new attempt before this time

    public Rigidbody Body { get; private set; }
    /// <summary>What he knows about Ruby: his own sightings and what he was told.</summary>
    public EnemyMemory Memory { get; private set; }
    /// <summary>What he hears, and what else he notices without sight.</summary>
    public EnemySenses Senses { get; private set; }
    public float MotionRadius => _motionRadius;
    public float BodyNoticeRange => _bodyNoticeRange;
    public float ScanInterval => _scanInterval;
    /// <summary>The scene's shared pool, or null when there is none (an error was logged at Start).</summary>
    public Intel Intel { get; private set; }
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

    // The grab knocks the gun out of his hand: a small hop up and away from his body, on top of
    // how he was moving.
    private void PopGun()
    {
        if (!IsArmed) return;
        Vector3 outward = Arms.HeldGun.transform.position - transform.position;
        outward.y = 0f;
        outward = outward.sqrMagnitude > 0.0001f ? outward.normalized : transform.forward;
        Arms.Drop(Body.linearVelocity + outward * _grabPopOut + Vector3.up * _grabPopUp);
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
    public float SearchTravelLimit => _searchTravelLimit;
    public float LookTime => _lookTime;
    public float StandDownTime => _standDownTime;
    public PatrolRoute SafeRoute => _safeRoute;
    public float NodePauseTime => _nodePauseTime;
    public float StuckTime => _stuckTime;
    public float RouteRetryTime => _routeRetryTime;
    public float RearmRadius => _rearmRadius;
    public float GetUpTime => _getUpTime;
    /// <summary>The body capsule's radius in world units: how far its surface sits from its centre.</summary>
    public float BodyRadius => _capsule.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z));
    public bool HasPatrolRoute => _safeRoute != null && _safeRoute.Count > 0;
    public Transform PlayerTransform => _player == null ? null : _player.transform;

    /// <summary>The speed of the last move it was told to make, 0 once stopped: how fast it's trying to go.</summary>
    public float MoveSpeed => _intendedSpeed;
    public WeaponHold Weapon => IsArmed ? WeaponHold.Pistol : WeaponHold.None;

    /// <summary>What its body is doing, for its animation. Firing, it aims; moving otherwise, it runs.</summary>
    public AnimStance Stance => _state.Current switch
    {
        AggroState => Aggro.Firing ? AnimStance.Aim : _intendedSpeed > 0f ? AnimStance.Run : AnimStance.Rest,
        RearmState => _intendedSpeed > 0f ? AnimStance.Run : AnimStance.Rest,
        GrabbedState => AnimStance.Restrained,
        RagdollState => Ragdoll.Standing ? AnimStance.GettingUp : AnimStance.Frozen,
        // Tied upright on the chain, a corpse is restrained like the living; off it, it keeps its pose.
        DeadState => IsHeld ? AnimStance.Restrained : AnimStance.Frozen,
        _ => AnimStance.Rest,
    };

    /// <summary>Ruby, while it's aiming at her.</summary>
    public bool TryFindTarget(out Vector3 point)
    {
        point = _player != null ? _player.transform.position : default;
        return _player != null && Stance == AnimStance.Aim;
    }
    /// <summary>
    /// What he knew of Ruby's whereabouts when aggro ended (his own position when he knew nothing),
    /// which is where search goes. Never her true position: a search that read it would track her
    /// through walls.
    /// </summary>
    public Vector3 LastKnownPlayerPosition { get; private set; }

    private const float WanderSpotRadius = 0.5f;
    private static readonly RaycastHit[] SightHits = new RaycastHit[16];
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private int _sightFrame = -1;
    private bool _canSeePlayer;
    private Collider _sightBlocker;
    private int _seesFrame = -1;
    private bool _seesPlayer;
    private bool _wasSeeing;
    private float _nextSpeak;

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        Health = GetComponent<Health>();
        Memory = new EnemyMemory();
        Senses = new EnemySenses(this);

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

        At(Searching, Patrol, new FuncPredicate(() => Searching.Finished && HasPatrolRoute));
        At(Searching, Idle, new FuncPredicate(() => Searching.Finished && !HasPatrolRoute));

        At(Ragdoll, Aggro, new FuncPredicate(() => Ragdoll.GotUp && !Health.IsDead));

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

        Intel = FindAnyObjectByType<Intel>();
        if (Intel == null) Debug.LogError($"{name}: no Intel in the scene — officers can't share what they see or hear each other.", this);
        else Intel.Register(this);
    }

    private void OnDestroy()
    {
        if (Intel != null) Intel.Unregister(this);
    }

    private void OnEnable() => Health.OnDamaged += HandleDamaged;

    private void OnDisable() => Health.OnDamaged -= HandleDamaged;

    private void Update()
    {
        _state.Tick();
        UpdateKnowledge();
        DebugDrawPath();
    }

    private void FixedUpdate()
    {
        _state.FixedTick();
        if (!Health.IsDead && _state.Current is not (GrabbedState or RagdollState or DeadState))
            Senses.Scan();
    }

    /// <summary>
    /// Knocked off its feet by a hit (ImpactDamage): ragdolls tumbling at the velocity the hit left
    /// it with, then gets up into Aggro. Held on the chain or already down, the hit only hurts, so
    /// a swung enemy keeps swinging.
    /// </summary>
    public void KnockDown()
    {
        if (Health.IsDead || _state.Current is GrabbedState or DeadState) return;
        if (_state.Current == Ragdoll)
        {
            // Getting up, it's on its feet, so a hard enough hit floors it again, as it would anyone
            // standing. SetState can't re-enter Ragdoll, so the state is relaunched instead.
            if (Ragdoll.Standing) Ragdoll.Relaunch(VelocityUtil.Flatten(Body.linearVelocity));
            return;
        }
        DropGun();
        Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity));
        _state.SetState(Ragdoll);
    }

    // Externally driven transitions. Nothing can poll for these, so they bypass the table.
    // Investigate is one too: a sound happens once, so there is nothing for the table to poll.
    public void Alert()
    {
        if (_state.Current is GrabbedState or RagdollState or DeadState) return;
        _state.SetState(Aggro);
    }

    /// <summary>
    /// Told or heard something worth searching. Idle and patrolling officers start searching
    /// there. One already searching moves only for a strictly higher priority: an equal one never
    /// does, so a shootout's alternating shots can't swap his target or re-flood his field, and a
    /// quiet sound can't pull him off a spot he's checking. Aggro and the
    /// states that can't act on it ignore the call: he already knows where Ruby is. True when it
    /// was taken; every decision is logged. A search for an attack makes him wary for good, taken
    /// or not, so it always ends on an aggressive patrol. With walkThere false he looks from where
    /// he stands instead of going to the point (a glance at a body the squad already knows).
    /// </summary>
    public bool Investigate(Vector3 point, StimulusResponse reason, int priority, bool walkThere = true)
    {
        if (reason == StimulusResponse.Ignore)
        {
            DebugLog.Info($"{name} ignores {reason} at {point}: the map says to ignore it.", this);
            return false;
        }
        if (reason == StimulusResponse.SearchForAttack) Memory.BecomeWary();
        if (_state.Current is GrabbedState or RagdollState or DeadState or RearmState or AggroState)
        {
            DebugLog.Info($"{name} ignores {reason} at {point} (priority {priority}): he is in {_state.Current.GetType().Name}.", this);
            return false;
        }

        if (_state.Current is SearchingState)
        {
            if (priority <= Searching.Priority)
            {
                DebugLog.Info($"{name} ignores {reason} at {point} (priority {priority}): equal or lower than his search at {Searching.Priority}.", this);
                return false;
            }
            Searching.Retarget(point, reason, priority, walkThere);
            DebugLog.Info($"{name} retargets his search to {point}: {reason} at priority {priority}.", this);
            return true;
        }

        // Stage before the transition, so SearchingState.Enter keeps this spot rather than Ruby's last seen.
        Searching.Stage(point, reason, priority, walkThere);
        _state.SetState(Searching);
        DebugLog.Info($"{name} starts a search at {point}: {reason} at priority {priority}.", this);
        return true;
    }

    // Corpses too: a dead enemy stays a body the chain can swing and throw.
    public bool CanBeGrabbed => true;

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld { get; private set; }

    public void EnterGrabbed()
    {
        PopGun();
        IsHeld = true;
        if (Health.IsDead) Dead.Grab();
        else _state.SetState(Grabbed);
    }

    /// <summary>
    /// Rope let go without a throw (dropped, or the node destroyed): it falls, limp, at the speed it
    /// had. Let go while spun (`wasSpun`), the grapple has already turned that speed onto Ruby's aim,
    /// so it flies out along it; otherwise it crumples. A corpse just tumbles.
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

        Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity));
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

    /// <summary>Sets where the search will start from; Aggro calls it on the way out.</summary>
    public void SetLastKnownPlayerPosition(Vector3 point) => LastKnownPlayerPosition = point;

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

    /// <summary>
    /// Curved Movement for realism.
    /// </summary>
    public void MoveToward(Vector3 target, float speed, bool mustReach)
    {
        Vector3 direction = target - transform.position;
        direction.y = 0f;

        Vector3 heading = transform.forward;
        heading.y = 0f;
        float fitted = speed;
        // Closer than 10 cm there is no direction worth turning to: keep the heading and speed.
        if (direction.sqrMagnitude > 0.01f)
        {
            // Fit the speed only to a point he must reach. A flow-field step rolls ahead every
            // step, so there is nothing to orbit and the cap would only slow a chase on ordinary
            // bends; its last step, the goal cell, stops rolling and is a point he must reach.
            if (mustReach)
                fitted = Steering.SpeedToFit(speed, _turnSpeed, direction.magnitude, Vector3.Angle(heading, direction));
            heading = Steering.TurnToward(heading, direction, _turnSpeed * Time.fixedDeltaTime);
            transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
        }
        else if (heading.sqrMagnitude > 0.0001f)
            heading.Normalize();

        // The fitted speed, so the animation sees him slow in a tight turn.
        _intendedSpeed = fitted;
        VelocityUtil.SetFlatVelocity(Body, PushLimit.Limit(_capsule, heading * fitted));
    }

    /// <summary>
    /// Backs toward a spot while facing the player: an armed enemy giving ground keeps its gun on
    /// her instead of turning its back, which MoveToward would do.
    /// </summary>
    public void StepAway(Vector3 spot, float speed)
    {
        _intendedSpeed = speed;
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

    /// <summary>Face a point at once, ignoring height: forward must never tilt, or the body drives vertically.</summary>
    public void FaceToward(Vector3 point)
    {
        Vector3 direction = point - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }

    /// <summary>
    /// Stops and turns his body side to side about where he was facing, baseYaw, so a look reads as
    /// looking about. It sets the heading about Y alone: his root never pitches in play, and a tilt
    /// would drive him off the floor. For FixedTick, where the walk also writes the heading.
    /// </summary>
    public void LookAround(float baseYaw, float elapsed)
    {
        Stop();
        float sweep = _lookSweepAngle * Mathf.Sin(elapsed * Mathf.PI * 2f / _lookSweepPeriod);
        transform.rotation = Quaternion.Euler(0f, baseYaw + sweep, 0f);
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
    public NavResult PathTo(Vector3 target, float speed) => Navigator.MoveTo(target, speed);

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

    /// <summary>
    /// Where Ruby most likely is, as far as he knows: the newer of his own memory and the published
    /// pool. Works from memory alone when the scene has no Intel. False when he knows nothing.
    /// </summary>
    public bool TryGetNewestRuby(out Vector3 point)
    {
        IntelSnapshot pool = Intel != null ? Intel.Published : IntelSnapshot.Empty;
        bool known = Memory.TryGetNewestRuby(pool, out RubyFact fact);
        point = fact.Position;
        return known;
    }

    /// <summary>
    /// Chase the newest thing he knows of Ruby, not Ruby herself, so breaking sight can drop a fight.
    /// Arrived when he is on the spot; Unreachable (and stopped) when he has nothing to go on.
    /// </summary>
    public NavResult ChaseKnown(float speed)
    {
        if (!TryGetNewestRuby(out Vector3 point))
        {
            Stop();
            return NavResult.Unreachable;
        }
        return Navigator.MoveTo(point, speed);
    }

    /// <summary>Chase the player along this floor's shared flow field.</summary>
    public void PathToPlayer(float speed)
    {
        if (_player != null) Navigator.ChaseTo(_player.transform, speed);
    }

    public void Stop()
    {
        _intendedSpeed = 0f;
        VelocityUtil.SetFlatVelocity(Body, Vector3.zero);
    }

    private Vector3 EyePosition => transform.position + Vector3.up * _eyeHeight;

    /// <summary>
    /// Whether he has Ruby in his eyes this frame: noticed, or already hunting her and in contact.
    /// Alive and on his feet only: a corpse, a held or a tumbling officer sees and says nothing.
    /// Cached per frame, like CanSeePlayer.
    /// </summary>
    public bool SeesPlayer
    {
        get
        {
            if (_seesFrame == Time.frameCount) return _seesPlayer;
            _seesFrame = Time.frameCount;
            _seesPlayer = !Health.IsDead && _state.Current is not (GrabbedState or RagdollState or DeadState)
                && (DetectsPlayer() || (_state.Current == Aggro && InContactWithPlayer()));
            return _seesPlayer;
        }
    }

    // Remembering and speaking aren't states: they run beside the machine. What he sees he knows at
    // once; calling it out is bounded by Speak Interval so a long sighting isn't a shout per frame.
    private void UpdateKnowledge()
    {
        bool sees = SeesPlayer;
        if (sees)
        {
            var fact = new RubyFact(_player.transform.position, Time.time, Knowledge.Seen);
            Memory.Learn(fact);
            if (Intel != null && (!_wasSeeing || Time.time >= _nextSpeak))
            {
                _nextSpeak = Time.time + _speakInterval;
                Intel.Say(this, fact);
            }
        }
        _wasSeeing = sees;
    }

    // Detection: noticed means in his view, and in sight.
    private bool DetectsPlayer() => _player != null && InView(_player.transform.position) && CanSeePlayer();

    /// <summary>
    /// Whether a point is in his view: the cone in front of him out to View Range, or the close
    /// circle all round, which is there so nobody can stand right behind him unnoticed. Measured
    /// flat, and only the shape: whether anything blocks the line is CanSeePlayer's business.
    /// With no horizontal facing there's no cone: only the close circle counts. His root never
    /// pitches in play (its rotation is frozen and only turned about Y), so this only covers an
    /// officer placed pitched in the scene.
    /// </summary>
    public bool InView(Vector3 point) => InView(point, _viewRange);

    public float ViewRange => _viewRange;

    /// <summary>The same cone and close circle, out to a range of the caller's choosing (a corpse is looked at closer than Ruby).</summary>
    public bool InView(Vector3 point, float range)
    {
        Vector3 offset = point - transform.position;
        offset.y = 0f;
        float distance = offset.magnitude;
        if (distance <= _closeRadius) return true;
        if (distance > range) return false;

        // No facing, no cone. Angle against a zero vector reads 0 and would put everything in range in front.
        if (!TryFlatForward(out Vector3 forward)) return false;
        return Vector3.Angle(forward, offset) <= _viewAngle * 0.5f;
    }

    // Flattened so a tilted body can't skew the angle; the same reason FaceTarget flattens. False
    // when the body points straight up or down, which leaves nothing to flatten. InView and its
    // gizmo share this so they can't disagree about when the cone exists.
    private bool TryFlatForward(out Vector3 forward)
    {
        forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= 0.0001f) return false;
        forward.Normalize();
        return true;
    }

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

        _sightBlocker = FindSightBlocker(_player.transform.position, _player.transform);
        _canSeePlayer = _sightBlocker == null;
        return _canSeePlayer;
    }

    /// <summary>
    /// Whether the line from his eyes to a point is clear. The target's own colliders don't block
    /// it, so a body can be checked from its centre without its skin getting in the way.
    /// </summary>
    public bool HasClearSight(Vector3 point, Transform target) => FindSightBlocker(point, target) == null;

    private Collider FindSightBlocker(Vector3 point, Transform target)
    {
        Vector3 eye = EyePosition;
        Vector3 toPoint = point - eye;
        float distance = toPoint.magnitude;
        if (distance < 0.01f) return null;

        int count = Physics.RaycastNonAlloc(eye, toPoint / distance, SightHits, distance, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider hit = SightHits[i].collider;
            if (IsCharacter(hit) || (target != null && hit.transform.IsChildOf(target))) continue;
            return hit;
        }
        return null;
    }

    // Characters see past each other, ragdoll bones included: each bone is a body of its own.
    private bool IsCharacter(Collider collider)
    {
        Rigidbody body = collider.attachedRigidbody;
        if (body == null) return false;
        return body.GetComponentInParent<PlayerController>() == _player || body.GetComponentInParent<EnemyController>() != null;
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
        // Give-up must sit outside both view range and the close circle, or the enemy aggros and
        // de-aggros in the same frame.
        _giveUpRadius = Mathf.Max(_giveUpRadius, Mathf.Max(_viewRange, _closeRadius) + 1f);
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
        // The view, flat at eye height: green when the player is noticed, red when she's in the
        // view but hidden, grey otherwise. Edit mode has no player to test, so it stays grey.
        bool tracking = Application.isPlaying && _player != null;
        bool inView = tracking && InView(_player.transform.position);
        Gizmos.color = inView ? (CanSeePlayer() ? Color.green : Color.red) : Color.grey;
        Vector3 eye = EyePosition;
        DrawFlatCircle(eye, _closeRadius);
        // Pitched straight up or down he has no facing and no cone (see InView), so draw none
        // rather than invent a direction.
        if (TryFlatForward(out Vector3 flatForward)) DrawFlatArc(eye, flatForward, _viewAngle, _viewRange);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _giveUpRadius);

        // What the senses sweep: loose bodies moving out to Motion Radius, corpses within Body Notice Range.
        Gizmos.color = new Color(0.6f, 0.2f, 1f);
        Gizmos.DrawWireSphere(transform.position, _motionRadius);
        Gizmos.color = new Color(1f, 0.4f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, _bodyNoticeRange);

        // The gun's engagement band: fires out to Range, closes in only to Min Distance. In edit
        // mode Arms isn't set yet, so read the gun under the hand directly.
        Gun gun = Arms != null ? Arms.HeldGun : GetComponentInChildren<Gun>();
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

    // A pie slice: the two edges and the rim between them, centred on forward.
    private static void DrawFlatArc(Vector3 centre, Vector3 forward, float angle, float radius)
    {
        const int segments = 24;
        Vector3 previous = centre + Quaternion.AngleAxis(-angle * 0.5f, Vector3.up) * forward * radius;
        Gizmos.DrawLine(centre, previous);
        for (int i = 1; i <= segments; i++)
        {
            float step = -angle * 0.5f + angle * i / segments;
            Vector3 next = centre + Quaternion.AngleAxis(step, Vector3.up) * forward * radius;
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
        Gizmos.DrawLine(centre, previous);
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
