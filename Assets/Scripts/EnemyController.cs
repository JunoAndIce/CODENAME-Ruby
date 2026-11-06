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
    [SerializeField, Min(0f)] private float _searchDuration = 5f;
    [Tooltip("How close he gets to a search spot, in metres. If something blocks the way once he's this near (another enemy already standing on the spot, say), he looks from where he stands instead of pushing at it.")]
    [SerializeField, Min(0.5f)] private float _searchReach = 1.5f;
    [Tooltip("The longest he walks to a search spot before searching wherever he has got to, in seconds. Keeps one he can't reach (blocked, or a crowded doorway) from walking forever.")]
    [SerializeField, Min(1f)] private float _searchTravelLimit = 20f;
    [Tooltip("While he sees Ruby, he calls out her position this often, in seconds (and at once on first sight). Speech reaches others immediately; the shared pool takes longer.")]
    [SerializeField, Min(0.1f)] private float _speakInterval = 1f;
    [Tooltip("How far he notices a loose body (thrown, tumbling or held) moving at Notice Speed or faster, with a clear line to it. Corpses and dropped guns aren't limited by this: see Body Notice Range and View Range.")]
    [SerializeField, Min(0f)] private float _motionRadius = 15f;
    [Tooltip("How far a body on the chain must be moved from where it was grabbed, in metres measured flat, before he takes it as Ruby dragging it and goes for her. Keep it above the charging throw's jiggle (0.1 m).")]
    [SerializeField, Min(0f)] private float _draggedDistance = 0.5f;
    [Tooltip("How close a corpse must be for him to notice it, in the same view as for Ruby (cone plus close circle). A corpse is looked at closer than Ruby is. A dropped gun is noticed out to View Range instead.")]
    [SerializeField, Min(0f)] private float _bodyNoticeRange = 6f;
    [Tooltip("Seconds between his sweeps for moving bodies, corpses and loose guns. Each enemy starts at a random offset so a squad doesn't sweep on the same step.")]
    [SerializeField, Min(0.05f)] private float _scanInterval = 0.2f;
    [Tooltip("A disturbance within this distance of where Ruby was last reported is searched for as an attack, not a problem, while that report is recent (Attack Near Time). Measured flat, in metres.")]
    [SerializeField, Min(0f)] private float _attackNearRadius = 20f;
    [Tooltip("How old the newest report of Ruby may be, in seconds, for a disturbance near it to count as an attack. Older and it's just a problem again. An enemy who has seen her ignores this and treats everything as an attack.")]
    [SerializeField, Min(0f)] private float _attackNearTime = 15f;
    [Tooltip("The time he is sent to look at something that raises his job to an attack, so a team goes aggressive together. Counted per enemy for good: each time he is put on a job he wasn't on (started, joined, called in). Distractions and glances don't count.")]
    [SerializeField, Min(1)] private int _sentToLookThreshold = 3;

    [Header("Looking")]
    [Tooltip("How long he looks around a spot he went to check, in seconds, before standing down. A search for an attack looks for Search Duration instead.")]
    [SerializeField, Min(0.1f)] private float _lookTime = 1.5f;
    [Tooltip("How much each look varies while he roams a search, as a share of Look Time: at 0.5 each lasts from half to one and a half times Look Time. Rolled for every look, so a team doesn't move off together.")]
    [SerializeField, Range(0f, 0.9f)] private float _roamLookVariation = 0.5f;
    [Tooltip("How long he holds still after finding nothing, in seconds: the gesture, until there is an animation for it.")]
    [SerializeField, Min(0f)] private float _standDownTime = 1f;
    [Tooltip("How far, in degrees, his body turns to each side of where he was facing while he looks around.")]
    [SerializeField, Range(0f, 180f)] private float _lookSweepAngle = 70f;
    [Tooltip("Seconds for one full sweep, left to right and back.")]
    [SerializeField, Min(0.2f)] private float _lookSweepPeriod = 2f;
    [Tooltip("How close a follower keeps behind the enemy who goes to check, in metres. He stops here and holds, facing the spot, so the team trails the checker and never crowds him.")]
    [SerializeField, Min(1f)] private float _followDistance = 3f;
    [Tooltip("A follower's walking speed behind the checker, as a multiple of Search Speed: below 1 so the team trails slowly behind him.")]
    [SerializeField, Range(0.2f, 1f)] private float _followSpeedFactor = 0.8f;

    [Header("Distraction")]
    [Tooltip("How long he turns to look at a lesser disturbance while searching, in seconds. The search waits meanwhile, so he doesn't lose search time.")]
    [SerializeField, Min(0.1f)] private float _distractionTime = 1f;
    [Tooltip("How many distractions inside Distraction Memory it takes before the jig is up and he goes to Aggro, taking the newest disturbance's origin as where Ruby is.")]
    [SerializeField, Min(1)] private int _distractionThreshold = 3;
    [Tooltip("How long a distraction counts toward the jig, in seconds.")]
    [SerializeField, Min(1f)] private float _distractionMemory = 12f;
    [Tooltip("A disturbance within this long of the last counted one is the same disturbance, in seconds: a thrown body thuds about ten times as it tumbles, and a firefight is a run of shots.")]
    [SerializeField, Min(0f)] private float _distractionGap = 2f;

    [Header("Grabbed")]
    [Tooltip("How fast the gun pops up out of the enemy's hand when the chain grabs him, in m/s: a small hop, to sell the grab.")]
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
    [Tooltip("Walked while unaware. Empty = he idles at his post. One point 4 m or more across (a circle's diameter, a rectangle's longer side) is wandered rather than stood on. Edit with the Scene-view handles when this enemy is selected.")]
    [SerializeField] private PatrolRoute _safeRoute = new();
    [Tooltip("Walked instead of the Safe route once he's aggressive: after he has seen Ruby or been sent after her (an attack search, or a glance at a body). Edit it with the same Scene-view handles (the toggle below picks which route they edit). Empty: a patroller keeps walking his Safe route, and an idle enemy stays at his post, alert.")]
    [SerializeField] private PatrolRoute _aggressiveRoute = new();
    [Tooltip("Patrol speed once he's aggressive, as a multiple of Patrol Speed: above 1 hurries him, below 1 slows him to a wary creep (0.5 is half speed). Applies to whichever route he walks, and to an idle enemy walking back to his post.")]
    [SerializeField, Range(0.1f, 3f)] private float _aggressiveSpeedFactor = 1.25f;
    [Tooltip("An idle enemy this close to his post, in metres, counts as at it. Where he starts is his post. Never below 0.6: the navigator reports arrival within 0.5 m, and a tighter tolerance would leave him 'away' from a post he has reached.")]
    [SerializeField, Min(0.6f)] private float _postTolerance = 1f;
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
    /// <summary>His view cone, close circle and line of sight.</summary>
    public EnemyVision Vision { get; private set; }
    public EnemyReactions Reactions { get; private set; }
    public float MotionRadius => _motionRadius;
    public float DraggedDistance => _draggedDistance;
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
    public float SearchReach => _searchReach;
    public float LookTime => _lookTime;
    public float RoamLookVariation => _roamLookVariation;
    public float StandDownTime => _standDownTime;
    public float FollowDistance => _followDistance;
    public float FollowSpeedFactor => _followSpeedFactor;
    public float DistractionTime => _distractionTime;
    internal int DistractionThreshold => _distractionThreshold;
    internal float DistractionMemory => _distractionMemory;
    internal float DistractionGap => _distractionGap;
    internal float AttackNearTime => _attackNearTime;
    internal float AttackNearRadius => _attackNearRadius;
    internal int SentToLookThreshold => _sentToLookThreshold;
    public PatrolRoute SafeRoute => _safeRoute;
    public PatrolRoute AggressiveRoute => _aggressiveRoute;
    public bool HasAggressiveRoute => _aggressiveRoute != null && _aggressiveRoute.Count > 0;

    /// <summary>
    /// Whether he patrols aggressively: he has been after Ruby (an attack search, or a glance at a
    /// body), has seen her, or has been told of her by a teammate who had (on a job). Derived on every
    /// read and never stored, and none is ever cleared, so he never goes back.
    /// </summary>
    public bool IsAggressive => Memory.Wary || Memory.KnowsRuby;

    /// <summary>
    /// The route he walks now: the Aggressive one when he's aggressive and has one, else the Safe
    /// one. An aggressive enemy with no Aggressive route walking his Safe route is a rule, not a
    /// fallback, and the Inspector says so.
    /// </summary>
    public PatrolRoute ActiveRoute => IsAggressive && HasAggressiveRoute ? _aggressiveRoute : _safeRoute;
    public bool HasActiveRoute => ActiveRoute != null && ActiveRoute.Count > 0;
    public float CurrentPatrolSpeed => IsAggressive ? _patrolSpeed * _aggressiveSpeedFactor : _patrolSpeed;
    /// <summary>Where he started: where an idle enemy stands, and goes back to after a search or a fall.</summary>
    public Vector3 PostPosition { get; private set; }
    public float PostYaw { get; private set; }
    public float PostTolerance => _postTolerance;
    public float NodePauseTime => _nodePauseTime;
    public float StuckTime => _stuckTime;
    public float RouteRetryTime => _routeRetryTime;
    public float RearmRadius => _rearmRadius;
    public float GetUpTime => _getUpTime;
    /// <summary>The body capsule's radius in world units: how far its surface sits from its centre.</summary>
    public float BodyRadius => _capsule.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z));
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

    /// <summary>
    /// Held, knocked down or dead: an override state (EnemyStateBase.IsOverride) is running, so the
    /// AI neither senses nor reacts. The one test for it. KnockDown keeps its own set.
    /// </summary>
    public bool IsOverridden => _state.Current is EnemyStateBase { IsOverride: true };

    /// <summary>In the Searching state right now. The job board reads it to check that a member of a job is searching.</summary>
    public bool IsSearching => _state.Current is SearchingState;

    /// <summary>The state he is in, for EnemyReactions to read. Reaching the machine itself stays with the controller.</summary>
    internal IState CurrentState => _state.Current;

    /// <summary>Ruby, while it's aiming at her.</summary>
    public bool TryFindTarget(out Vector3 point)
    {
        point = _player != null ? _player.transform.position : default;
        return _player != null && Stance == AnimStance.Aim;
    }
    // The one way into Searching: whoever sends him there asks first (RequestSearch) and Enter takes
    // it. A default start at some remembered spot let a caller that forgot to ask search at the wrong place, silently.
    private SearchRequest? _pendingSearch;

    private const float WanderSpotRadius = 0.5f;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private int _seesFrame = -1;
    private bool _seesPlayer;
    private bool _wasSeeing;
    private float _nextSpeak;
    private bool _wasAggressive;

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        Health = GetComponent<Health>();
        Memory = new EnemyMemory();
        Senses = new EnemySenses(this);
        Vision = new EnemyVision(this);
        Reactions = new EnemyReactions(this);

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

        // Unarmed with a free gun in reach: he goes for it from any AI state. The rows above are listed
        // first, so they win: an unarmed enemy who notices Ruby takes Aggro first, and Aggro hands on to Rearm.
        At(Idle, Rearm, new FuncPredicate(WantsGun));
        At(Patrol, Rearm, new FuncPredicate(WantsGun));
        At(Searching, Rearm, new FuncPredicate(WantsGun));
        At(Aggro, Rearm, new FuncPredicate(WantsGun));
        // Always back to the hunt: an enemy only loses its gun to Ruby, so it knows she's around.
        At(Rearm, Aggro, new FuncPredicate(() => Rearm.Armed || Rearm.GaveUp));

        At(Searching, Patrol, new FuncPredicate(() => Searching.Finished && HasActiveRoute));
        At(Searching, Idle, new FuncPredicate(() => Searching.Finished && !HasActiveRoute));

        // The route he walks follows how aggressive he is (IsAggressive), so becoming aggressive can hand an
        // idle enemy a route to walk, or leave a patroller with none for his mode and send him to his post.
        At(Idle, Patrol, new FuncPredicate(() => HasActiveRoute));
        At(Patrol, Idle, new FuncPredicate(() => !HasActiveRoute));

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
        PostPosition = transform.position;
        PostYaw = transform.eulerAngles.y;
        _state.SetState(HasActiveRoute ? Patrol : (EnemyStateBase)Idle);

        Intel = FindAnyObjectByType<Intel>();
        if (Intel == null) Debug.LogError($"{name}: no Intel in the scene — officers can't share what they see or hear each other.", this);
        else Intel.Register(this);
    }

    private void OnDestroy()
    {
        if (Intel == null) return;
        Intel.Leave(this);
        Intel.Unregister(this);
    }

    private void OnEnable() => Health.OnDamaged += HandleDamaged;

    private void OnDisable() => Health.OnDamaged -= HandleDamaged;

    private void Update()
    {
        _state.Tick();
        UpdateKnowledge();
        LogBecameAggressive();
        DebugDrawPath();
    }

    // IsAggressive is derived, so nothing "enters" it; this catches the moment it turns true, once,
    // and says why and which route he'll walk. It never goes false again.
    private void LogBecameAggressive()
    {
        bool aggressive = IsAggressive;
        if (aggressive && !_wasAggressive)
        {
            string why = Memory.HasSeenRuby ? "he has seen Ruby" : Memory.WasToldOfRuby ? "he has been told of Ruby" : "he has been sent after her";
            string route = HasAggressiveRoute ? "his Aggressive route" : HasActiveRoute ? "his Safe route (he has no Aggressive one)" : "no route: he stays at his post, alert";
            DebugLog.Info($"{name} is now aggressive: {why}. From here he patrols at {_aggressiveSpeedFactor:0.##}x ({CurrentPatrolSpeed:0.##} m/s, from {_patrolSpeed:0.##}) on {route}, looking around at each point.", this);
        }
        _wasAggressive = aggressive;
    }

    private void FixedUpdate()
    {
        _state.FixedTick();
        if (!Health.IsDead && !IsOverridden)
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
    /// <summary>Sends him into Aggro (hurt, or told by an enemy who sees Ruby). True when that moved him: not when he was already there, or can't act.</summary>
    public bool Alert()
    {
        if (IsOverridden) return false;
        if (_state.Current == Aggro) return false;
        _state.SetState(Aggro);
        return true;
    }

    /// <summary>Forwards to EnemyReactions.Investigate, so the senses call it where they always have.</summary>
    public bool Investigate(Incident incident, bool walkThere = true) => Reactions.Investigate(incident, walkThere);

    /// <summary>Forwards to EnemyReactions.BecomeWaryAt.</summary>
    internal void BecomeWary(Vector3 at, string why) => Reactions.BecomeWaryAt(at, why);

    // Corpses too: a dead enemy stays a body the chain can swing and throw.
    public bool CanBeGrabbed => true;

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld { get; private set; }

    private Vector3 _heldFrom;

    /// <summary>How far he has been moved from where he was grabbed, in flat metres; 0 when he isn't held. What tells a watcher Ruby is dragging him.</summary>
    public float HeldMovedBy => IsHeld ? PathGrid.FlatDistance(_heldFrom, transform.position) : 0f;

    /// <summary>
    /// The job he is on, or null: a handful of enemies dealing with one thing together (GroupJob).
    /// Only the job board sets it, as it adds and removes members, and SearchingState gives him up on exit.
    /// </summary>
    public GroupJob Job { get; internal set; }

    /// <summary>Forwards to EnemyReactions.CanJoinJob.</summary>
    public bool CanJoinJob(int priority) => Reactions.CanJoinJob(priority);

    /// <summary>
    /// Sends him to a job's place for its reason, as a search: requested from Idle or Patrol, a
    /// retarget if he is already searching (which includes SearchingState.Enter itself: the machine has
    /// made Searching current before calling it, so a job joined from there retargets and never calls
    /// SetState). The job board has already put him on the job's member list.
    /// A search for an attack makes him wary, as any other does.
    /// </summary>
    internal void FollowJob(GroupJob job)
    {
        if (job.Reason == StimulusResponse.SearchForAttack) Reactions.BecomeWary();
        // Built here rather than kept on the job: a job doesn't remember whether its latest trigger was
        // a find, and the search doesn't read it, so it carries none.
        var request = new SearchRequest(new Incident(job.Point, job.Reason, job.Priority));
        if (_state.Current is SearchingState)
        {
            Searching.Retarget(request);
            return;
        }

        BeginSearch(request);
    }

    /// <summary>
    /// Starts a search from Idle or Patrol. Ask before the transition: SearchingState.Enter takes the
    /// request, and entering with none is an error. Aggro's exit asks alone (RequestSearch), because
    /// the table makes that transition.
    /// </summary>
    internal void BeginSearch(SearchRequest request)
    {
        RequestSearch(request);
        _state.SetState(Searching);
    }

    /// <summary>
    /// Asks for the search the next entry into Searching will begin: the one way in. Whoever moves him
    /// there asks first (Investigate, FollowJob), or Aggro on its way out after a lost fight. A search
    /// already running takes Retarget instead, which has no Enter.
    /// </summary>
    internal void RequestSearch(SearchRequest request) => _pendingSearch = request;

    /// <summary>Hands over the pending request and clears it. SearchingState.Enter is the only caller.</summary>
    internal bool TakeSearchRequest(out SearchRequest request)
    {
        request = _pendingSearch.GetValueOrDefault();
        bool had = _pendingSearch.HasValue;
        _pendingSearch = null;
        return had;
    }

    public void EnterGrabbed()
    {
        PopGun();
        _heldFrom = transform.position;
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
    /// He turns his walking heading at Turn Speed, and slows to fit the turn only for a point he must reach.
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

    /// <summary>
    /// Turns his body toward a heading about Y, at Turn Speed, and says whether he's facing it.
    /// For standing at his post: facing the way he started once he's back.
    /// </summary>
    public bool TurnToYaw(float yaw)
    {
        Vector3 goal = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 heading = transform.forward;
        heading.y = 0f;
        if (heading.sqrMagnitude > 0.0001f && Vector3.Angle(heading, goal) < 1f) return true;

        heading = Steering.TurnToward(heading, goal, _turnSpeed * Time.fixedDeltaTime);
        transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
        return false;
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
    /// looking about. The look lasts duration, and the sweep is stretched to a whole number of
    /// sweeps within it, so he ends facing the way he began: a fixed period left a 1.5 s look 70
    /// degrees off the spot he'd been looking at. It sets the heading about Y alone: his root never
    /// pitches in play, and a tilt would drive him off the floor. For FixedTick, where the walk also
    /// writes the heading.
    /// </summary>
    public void LookAround(float baseYaw, float elapsed, float duration)
    {
        Stop();
        duration = Mathf.Max(duration, 0.01f);
        float sweeps = Mathf.Max(1f, Mathf.Round(duration / _lookSweepPeriod));
        float sweep = _lookSweepAngle * Mathf.Sin(Mathf.Min(elapsed, duration) * Mathf.PI * 2f * sweeps / duration);
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
        bool known = TryGetNewestRubyFact(out RubyFact fact);
        point = fact.Position;
        return known;
    }

    /// <summary>The whole newest fact about Ruby, with when and how he knows it. False when he knows nothing.</summary>
    public bool TryGetNewestRubyFact(out RubyFact fact)
    {
        IntelSnapshot pool = Intel != null ? Intel.Published : IntelSnapshot.Empty;
        return Memory.TryGetNewestRuby(pool, out fact);
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

    /// <summary>
    /// Chase another body along its floor's shared field, which every chaser of it reads (a team
    /// following its checker). Stops and warns once when it is on another floor or no grid.
    /// </summary>
    public void ChaseTo(Transform target, float speed) => Navigator.ChaseTo(target, speed);

    public void Stop()
    {
        _intendedSpeed = 0f;
        VelocityUtil.SetFlatVelocity(Body, Vector3.zero);
    }

    internal Vector3 EyePosition => transform.position + Vector3.up * _eyeHeight;

    /// <summary>
    /// Whether he has Ruby in his eyes this frame: noticed, or already hunting her with a clear line
    /// to her, at any distance. Not only within Give Up Radius: Aggro chases her whenever she's in
    /// sight, so past the radius he followed her without learning where she was, and when she dropped
    /// out of sight he turned round and walked back to the old spot. The radius only decides when the
    /// hunt ends (InContactWithPlayer). Alive and on his feet only: a corpse, a held or a tumbling
    /// enemy sees and says nothing. Cached per frame, like CanSeePlayer.
    /// </summary>
    private bool SeesPlayer
    {
        get
        {
            if (_seesFrame == Time.frameCount) return _seesPlayer;
            _seesFrame = Time.frameCount;
            _seesPlayer = !Health.IsDead && !IsOverridden
                && (DetectsPlayer() || (_state.Current == Aggro && CanSeePlayer()));
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

    // Detection: noticed means in his view, and in sight. Sight lives in EnemyVision; these forwards
    // keep every state, sense and the editor calling the controller as before.
    private bool DetectsPlayer() => Vision.DetectsPlayer();

    public bool InView(Vector3 point) => Vision.InView(point);

    public float ViewRange => _viewRange;
    internal float ViewAngle => _viewAngle;
    internal float CloseRadius => _closeRadius;
    internal float GiveUpRadius => _giveUpRadius;
    // Start fills it after Awake builds the Vision, so EnemyVision reads it here on every call.
    internal PlayerController Player => _player;

    public bool InView(Vector3 point, float range) => Vision.InView(point, range);

    public bool InContactWithPlayer() => Vision.InContactWithPlayer();

    public Collider SightBlocker => Vision.SightBlocker;

    public bool CanSeePlayer() => Vision.CanSeePlayer();

    public bool HasClearSight(Vector3 point, Transform target) => Vision.HasClearSight(point, target);

    public float DistanceToPlayer() => Vision.DistanceToPlayer();

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
        if (EnemyVision.TryFlatForward(transform, out Vector3 flatForward)) DrawFlatArc(eye, flatForward, _viewAngle, _viewRange);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _giveUpRadius);

        // What the senses sweep: loose bodies moving out to Motion Radius, corpses within Body Notice Range.
        Gizmos.color = new Color(0.6f, 0.2f, 1f);
        Gizmos.DrawWireSphere(transform.position, _motionRadius);
        Gizmos.color = new Color(1f, 0.4f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, _bodyNoticeRange);

        // Around where he last heard or saw of Ruby, while that report is recent enough: a
        // disturbance inside this circle is searched for as an attack. Playing doesn't mean he has woken:
        // the prefab open in Prefab Mode during Play never runs Awake, so it has no Memory to ask.
        if (Application.isPlaying && Memory != null && TryGetNewestRubyFact(out RubyFact news) && Time.time - news.Time <= _attackNearTime)
        {
            Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.6f);
            DrawFlatCircle(news.Position + Vector3.up * 0.1f, _attackNearRadius);
        }

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

    // Every enemy's routes, faintly, so you can see who walks what without selecting each one:
    // cyan for the Safe route, orange for the Aggressive one.
    private void OnDrawGizmos()
    {
        DrawRoute(_safeRoute, new Color(0f, 1f, 1f, 0.35f));
        DrawRoute(_aggressiveRoute, new Color(1f, 0.55f, 0.1f, 0.45f));
    }

    private static void DrawRoute(PatrolRoute route, Color colour)
    {
        if (route == null || route.Count == 0) return;

        Gizmos.color = colour;
        int count = route.Count;
        for (int i = 0; i < count; i++)
        {
            PatrolPoint point = route[i];
            DrawFlatArea(point.Area);

            // A random route has no order, so lines between its points would only mislead.
            if (route.Mode == PatrolMode.Random) continue;

            bool last = i == count - 1;
            if (!last) Gizmos.DrawLine(point.Position, route[i + 1].Position);
            else if (route.Mode == PatrolMode.Loop && count > 2) Gizmos.DrawLine(point.Position, route[0].Position);
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
