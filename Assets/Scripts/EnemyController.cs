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
    [Tooltip("How close he gets to a search spot, in metres. If something blocks the way once he's this near (another officer already standing on the spot, say), he looks from where he stands instead of pushing at it.")]
    [SerializeField, Min(0.5f)] private float _searchReach = 1.5f;
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
    [Tooltip("A disturbance within this distance of where Ruby was last reported is searched for as an attack, not a problem, while that report is recent (Attack Near Time). Measured flat, in metres.")]
    [SerializeField, Min(0f)] private float _attackNearRadius = 20f;
    [Tooltip("How old the newest report of Ruby may be, in seconds, for a disturbance near it to count as an attack. Older and it's just a problem again. An officer who has seen her ignores this and treats everything as an attack.")]
    [SerializeField, Min(0f)] private float _attackNearTime = 15f;

    [Header("Looking")]
    [Tooltip("How long he looks around a spot he went to check, in seconds, before standing down. A search for an attack looks for Search Duration instead.")]
    [SerializeField, Min(0.1f)] private float _lookTime = 1.5f;
    [Tooltip("How long he holds still after finding nothing, in seconds: the gesture, until there is an animation for it.")]
    [SerializeField, Min(0f)] private float _standDownTime = 1f;
    [Tooltip("How far, in degrees, his body turns to each side of where he was facing while he looks around.")]
    [SerializeField, Range(0f, 180f)] private float _lookSweepAngle = 70f;
    [Tooltip("Seconds for one full sweep, left to right and back.")]
    [SerializeField, Min(0.2f)] private float _lookSweepPeriod = 2f;
    [Tooltip("How close a follower keeps behind the officer who goes to check, in metres. He stops here and holds, facing the spot, so the team trails the checker and never crowds him.")]
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
    [Tooltip("Walked while unaware. Empty = he idles at his post. One point 4 m or more across (a circle's diameter, a rectangle's longer side) is wandered rather than stood on. Edit with the Scene-view handles when this enemy is selected.")]
    [SerializeField] private PatrolRoute _safeRoute = new();
    [Tooltip("Walked instead of the Safe route once he's aggressive: after he has seen Ruby or been sent after her (an attack search, or a glance at a body). Edit it with the same Scene-view handles (the toggle below picks which route they edit). Empty: a patroller keeps walking his Safe route, and an idle officer stays at his post, alert.")]
    [SerializeField] private PatrolRoute _aggressiveRoute = new();
    [Tooltip("Patrol speed once he's aggressive, as a multiple of Patrol Speed: above 1 hurries him, below 1 slows him to a wary creep (0.5 is half speed). Applies to whichever route he walks, and to an idle officer walking back to his post. At the prefab's Patrol Speed of 7 m/s, 1.25 is 8.75 m/s and 0.5 is 3.5 m/s.")]
    [SerializeField, Range(0.1f, 3f)] private float _aggressiveSpeedFactor = 1.25f;
    [Tooltip("An idle officer this close to his post, in metres, counts as at it. Where he starts is his post. Never below 0.6: the navigator reports arrival within 0.5 m, and a tighter tolerance would leave him 'away' from a post he has reached.")]
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
    public float SearchReach => _searchReach;
    public float LookTime => _lookTime;
    public float StandDownTime => _standDownTime;
    public float FollowDistance => _followDistance;
    public float FollowSpeedFactor => _followSpeedFactor;
    public float DistractionTime => _distractionTime;
    public PatrolRoute SafeRoute => _safeRoute;
    public PatrolRoute AggressiveRoute => _aggressiveRoute;
    public bool HasAggressiveRoute => _aggressiveRoute != null && _aggressiveRoute.Count > 0;

    /// <summary>
    /// Whether he patrols aggressively: he has been after Ruby (an attack search, or a glance at a
    /// body) or has seen her. Derived on every read and never stored, and neither is ever cleared,
    /// so he never goes back. A level-wide alert from the first Aggro was removed for now: only
    /// what an officer himself has been through makes him aggressive.
    /// </summary>
    public bool IsAggressive => Memory.Wary || Memory.HasSeenRuby;

    /// <summary>
    /// The route he walks now: the Aggressive one when he's aggressive and has one, else the Safe
    /// one. An aggressive officer with no Aggressive route walking his Safe route is a rule, not a
    /// fallback, and the Inspector says so.
    /// </summary>
    public PatrolRoute ActiveRoute => IsAggressive && HasAggressiveRoute ? _aggressiveRoute : _safeRoute;
    public bool HasActiveRoute => ActiveRoute != null && ActiveRoute.Count > 0;
    public float CurrentPatrolSpeed => IsAggressive ? _patrolSpeed * _aggressiveSpeedFactor : _patrolSpeed;
    /// <summary>Where he started: where an idle officer stands, and goes back to after a search or a fall.</summary>
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
    private bool _wasAggressive;

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

        At(Searching, Patrol, new FuncPredicate(() => Searching.Finished && HasActiveRoute));
        At(Searching, Idle, new FuncPredicate(() => Searching.Finished && !HasActiveRoute));

        // The route he walks follows how aggressive he is (IsAggressive), so an alert can hand an idle
        // officer a route to walk, or leave a patroller with none for his mode and send him to his post.
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
            string why = Memory.HasSeenRuby ? "he has seen Ruby" : "he has been sent after her";
            string route = HasAggressiveRoute ? "his Aggressive route" : HasActiveRoute ? "his Safe route (he has no Aggressive one)" : "no route: he stays at his post, alert";
            DebugLog.Info($"{name} is now aggressive: {why}. From here he patrols at {_aggressiveSpeedFactor:0.##}x ({CurrentPatrolSpeed:0.##} m/s, from {_patrolSpeed:0.##}) on {route}, looking around at each point.", this);
        }
        _wasAggressive = aggressive;
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
    /// <summary>Sends him into Aggro (hurt, or told by an officer who sees Ruby). True when that moved him: not when he was already there, or can't act.</summary>
    public bool Alert()
    {
        if (_state.Current is GrabbedState or RagdollState or DeadState) return false;
        if (_state.Current == Aggro) return false;
        _state.SetState(Aggro);
        return true;
    }

    /// <summary>
    /// Told or heard something worth searching. Idle and patrolling officers start searching
    /// there. One already searching moves only for a strictly higher priority: an equal one never
    /// does, so a shootout's alternating shots can't swap his target or re-flood his field, and a
    /// quiet sound can't pull him off a spot he's checking. But he **notices** it: an equal or
    /// lower one is a distraction (see Distract) rather than being ignored, which left an officer
    /// searching on beside things flying past him. Aggro and the
    /// states that can't act on it ignore the call: he already knows where Ruby is. True when it
    /// was taken; every decision is logged. A search for an attack makes him wary for good, taken
    /// or not, so it always ends on an aggressive patrol. With walkThere false he looks from where
    /// he stands instead of going to the point (a glance at a body the squad already knows).
    /// On a job, a stimulus at the job's place goes to the job first (Intel.HandleAtOwnJob, BecomeWary).
    /// isFind marks a body or gun sighting, which says nothing about where Ruby is.
    /// </summary>
    public bool Investigate(Vector3 point, StimulusResponse reason, int priority, bool walkThere = true, bool isFind = false)
    {
        if (reason == StimulusResponse.Ignore)
        {
            DebugLog.Info($"{name} ignores {reason} at {point}: the map says to ignore it.", this);
            return false;
        }
        if (reason == StimulusResponse.SearchForProblem && ShouldSearchForAttack(point, out string why))
        {
            DebugLog.Info($"{name} treats the problem at {point} as an attack: {why}.", this);
            reason = StimulusResponse.SearchForAttack;
        }
        if (reason == StimulusResponse.SearchForAttack) BecomeWary(point, $"{name} senses {reason} at {point}");
        if (_state.Current is GrabbedState or RagdollState or DeadState or RearmState or AggroState)
        {
            DebugLog.Info($"{name} ignores {reason} at {point} (priority {priority}): he is in {_state.Current.GetType().Name}.", this);
            return false;
        }

        if (_state.Current is SearchingState)
        {
            // On a job, anything at its place goes to the job first: a find turns a check into a search
            // and a more serious thing upgrades it, for the whole team. Only what the job didn't take
            // is a distraction for him, and a glance never changes the job, so it never retargets him.
            GroupJob own = Job;
            bool atOwnJob = own != null && Intel != null && Intel.Covers(own, point);
            if (atOwnJob && Intel.HandleAtOwnJob(this, point, reason, priority, isFind, walkThere)) return true;
            if (atOwnJob || priority <= Searching.Priority) return Distract(point, reason, priority, isFind);
            if (own != null && !walkThere) return Distract(point, reason, priority, isFind);

            // More serious than his search: the squad decides, as a job. A glance off a job (walkThere
            // false) is his own look from where he stands, and a scene with no Intel (already reported)
            // has no squad to ask, so both stay a search of his own. A null answer means a job
            // nearby already has it; that counts as taken, or the motion scan would ask again every sweep.
            if (walkThere && Intel != null)
            {
                Intel.Handle(this, point, reason, priority, isFind);
                // Still on the job he was on: the team that has it can't take him, so he stays and is distracted.
                if (own != null && Job == own) return Distract(point, reason, priority, isFind);
                return true;
            }

            Searching.Retarget(point, reason, priority, walkThere);
            DebugLog.Info($"{name} retargets his search to {point}: {reason} at priority {priority}.", this);
            return true;
        }

        if (walkThere && Intel != null)
        {
            // Idle or patrolling: the squad decides whether this is already being handled, and who goes.
            // Left to a job nearby is as good as taken, for the reason above.
            Intel.Handle(this, point, reason, priority, isFind);
            return true;
        }

        // Stage before the transition, so SearchingState.Enter keeps this spot rather than Ruby's last seen.
        Searching.Stage(point, reason, priority, walkThere);
        _state.SetState(Searching);
        DebugLog.Info($"{name} starts a search at {point}: {reason} at priority {priority}.", this);
        return true;
    }

    /// <summary>
    /// He has reason to think Ruby is about, because of something at `at`. On a job, at the job's place,
    /// the job takes it and everyone on it goes wary together, so one officer can't latch wary alone
    /// while the job says all clear. Anywhere else (no job, or a thing away from his job's place) it
    /// is his own knowledge and he latches wary himself.
    /// </summary>
    internal void BecomeWary(Vector3 at, string why)
    {
        if (Job != null && Intel != null && Intel.RaiseJobAt(this, at, why)) return;
        Memory.BecomeWary();
    }

    // Something lesser happened while he was searching. He turns to look at it and the search waits
    // a moment, but it isn't dropped or retargeted. Enough of them close together and the jig is
    // up: he aggros, and a thrown object's origin or a shot's muzzle is where he takes Ruby to be,
    // so Aggro has somewhere to go. A body or gun says nothing about her, so he goes to Aggro with
    // what he already knows, and searches where he stands if that is nothing.
    private bool Distract(Vector3 point, StimulusResponse reason, int priority, bool isFind)
    {
        int count = Memory.NoteDistraction(Time.time, _distractionMemory, _distractionGap, out bool counted);
        if (counted && count >= _distractionThreshold)
        {
            Memory.ClearDistractions();
            if (!isFind) Memory.Learn(new RubyFact(point, Time.time, Knowledge.Heard));
            DebugLog.Info($"{name}: Enemies have been disturbed too much! ({count} disturbances in {_distractionMemory:0.#}s, the last {reason} at {point}); he aggros.", this);
            Alert();
            return true;
        }

        Searching.Distract(point, counted);
        DebugLog.Info($"{name} {(counted ? "is distracted by" : "glances again at")} {reason} at {point} (priority {priority}, his search is {Searching.Priority}): {count} of {_distractionThreshold}.", this);
        return true;
    }

    // Corpses too: a dead enemy stays a body the chain can swing and throw.
    public bool CanBeGrabbed => true;

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>
    /// The job he is on, or null: a handful of officers dealing with one thing together (GroupJob).
    /// Only Intel sets it, as it adds and removes members, and SearchingState gives him up on exit.
    /// </summary>
    public GroupJob Job { get; internal set; }

    /// <summary>
    /// Whether the squad may call him onto a job at this priority: he is idle or patrolling, or he is
    /// searching alone (no job, or a job of one) at a lower priority than this. Never in a fight, down,
    /// dead or fetching a gun, and never off a team that has two or more.
    /// </summary>
    public bool CanJoinJob(int priority)
    {
        if (Health.IsDead) return false;
        if (_state.Current is IdleState or PatrolState) return true;
        return _state.Current is SearchingState
            && Searching.Priority < priority
            && (Job == null || Job.Count == 1);
    }

    /// <summary>
    /// Sends him to a job's place for its reason, as a search: staged from Idle or Patrol, a
    /// retarget if he is already searching. Intel has already put him on the job's member list.
    /// A search for an attack makes him wary, as any other does.
    /// </summary>
    internal void FollowJob(GroupJob job)
    {
        if (job.Reason == StimulusResponse.SearchForAttack) Memory.BecomeWary();
        if (_state.Current is SearchingState)
        {
            Searching.Retarget(job.Point, job.Reason, job.Priority, true);
            return;
        }

        // Stage before the transition, so SearchingState.Enter keeps this spot rather than Ruby's last seen.
        Searching.Stage(job.Point, job.Reason, job.Priority, true);
        _state.SetState(Searching);
    }

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

    // A disturbance is an attack, not just a problem, to someone who knows she's about: he has seen
    // her, or the newest news of her is recent and close to it. The age is the fact's own time, so
    // an old report can't turn every later thud into an attack for good.
    private bool ShouldSearchForAttack(Vector3 point, out string why)
    {
        if (Memory.HasSeenRuby)
        {
            why = "he has seen Ruby";
            return true;
        }

        if (TryGetNewestRubyFact(out RubyFact fact))
        {
            float age = Time.time - fact.Time;
            float distance = PathGrid.FlatDistance(fact.Position, point);
            if (age <= _attackNearTime && distance <= _attackNearRadius)
            {
                why = $"Ruby was {fact.Source} {distance:0.#} m from it, {age:0.#}s ago";
                return true;
            }
        }

        why = null;
        return false;
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

        // Around where he last heard or saw of Ruby, while that report is recent enough: a
        // disturbance inside this circle is searched for as an attack.
        if (Application.isPlaying && TryGetNewestRubyFact(out RubyFact news) && Time.time - news.Time <= _attackNearTime)
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
