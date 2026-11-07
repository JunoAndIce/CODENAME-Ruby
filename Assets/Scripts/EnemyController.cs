using UnityEngine;
using UnityEngine.Serialization;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(Health))]
public class EnemyController : MonoBehaviour, IGrabbable, IPathAgent, IKnockdown, IBulletTarget, IAnimatedCharacter
{
    [Header("Moving")]
    [SerializeField] private float _chaseSpeed = 2f;
    [SerializeField] private float _patrolSpeed = 1f;
    [Tooltip("Patrol speed once he's aggressive, as a multiple of Patrol Speed: above 1 hurries him, below 1 slows him to a wary creep (0.5 is half speed). Applies to whichever route he walks, and to an idle enemy walking back to his post.")]
    [SerializeField, Range(0.1f, 3f)] private float _aggressiveSpeedFactor = 1.25f;
    [SerializeField] private float _searchSpeed = 1.5f;
    [Tooltip("His speed while he clears a Search's area, as a multiple of Search Speed: below 1, so he creeps and looks. Walking in to the area stays at full Search Speed.")]
    [SerializeField, Range(0.1f, 1f)] private float _clearingSpeedFactor = 0.6f;
    [Tooltip("A follower's walking speed behind the checker, as a multiple of Search Speed: below 1 so the team trails slowly behind him.")]
    [SerializeField, Range(0.2f, 1f)] private float _followSpeedFactor = 0.8f;
    [Tooltip("How fast the body turns while walking a path, in degrees a second: lower makes wider arcs round bends. The legs' own turn on the model (CharacterAnimator's Turn Speed) is separate.")]
    [SerializeField, Min(1f)] private float _turnSpeed = 720f;
    [Tooltip("The room he keeps round other enemies, in metres from centre to centre. Walking any path he curves away from anyone inside this, harder the closer they are, and he doesn't choose a roaming or wandering spot inside it. Capsules touch at about 0.7 m, so below that it does nothing. 0 turns it off.")]
    [SerializeField, Min(0f)] private float _personalSpace = 1.5f;
    [Tooltip("Seconds without getting closer before a patrol move counts as blocked (another enemy on the spot, a prop, a jammed doorway).")]
    [SerializeField, Min(0.1f)] private float _stuckTime = 1.5f;

    [Header("Sight")]
    [Tooltip("How far he sees in front of him, within View Angle, if in sight.")]
    [FormerlySerializedAs("_alertRadius")] [SerializeField, Min(0f)] private float _viewRange = 12f;
    [Tooltip("How wide the view in front of him is, in degrees, centred on his facing.")]
    [SerializeField, Range(1f, 360f)] private float _viewAngle = 100f;
    [Tooltip("Whichever way he faces, he notices Ruby this close, if in sight, so she can't stand right behind him.")]
    [SerializeField, Min(0f)] private float _closeRadius = 2f;
    [Tooltip("Height above the enemy's centre that sight is cast from.")]
    [SerializeField] private float _eyeHeight = 0.5f;

    [Header("Senses")]
    [Tooltip("Seconds between his sweeps for moving bodies, corpses and loose guns. Each enemy starts at a random offset so a squad doesn't sweep on the same step.")]
    [SerializeField, Min(0.05f)] private float _scanInterval = 0.2f;
    [Tooltip("How far he notices a loose body (thrown, tumbling or held) moving at Notice Speed or faster, with a clear line to it. Corpses and dropped guns aren't limited by this: see Body Notice Range and View Range.")]
    [SerializeField, Min(0f)] private float _motionRadius = 15f;
    [Tooltip("How close a corpse must be for him to notice it, in the same view as for Ruby (cone plus close circle). A corpse is looked at closer than Ruby is. A dropped gun is noticed out to View Range instead.")]
    [SerializeField, Min(0f)] private float _bodyNoticeRange = 6f;
    [Tooltip("How far a body on the chain must be moved from where it was grabbed, in metres measured flat, before he takes it as Ruby dragging it and goes for her. Keep it above the charging throw's jiggle (0.1 m).")]
    [SerializeField, Min(0f)] private float _draggedDistance = 0.5f;

    [Header("Fighting")]
    [Tooltip("Aggro keeps contact inside this radius, if in sight. Sits outside View Range.")]
    [SerializeField] private float _giveUpRadius = 18f;
    [Tooltip("Seconds out of contact (out of sight, or beyond Give Up Radius) before aggro turns to searching.")]
    [SerializeField] private float _aggroTime = 1.5f;
    [Tooltip("For this long after he loses sight of Ruby in a fight, in seconds, he still knows where she is, because he watched where she went. So running round a crate in plain view doesn't lose him at once. Higher is harder to shake. 0: he only ever knows where he last saw her.")]
    [SerializeField, Min(0f)] private float _sightFollowThrough = 1f;
    [Tooltip("How long he holds at the spot he knew if he hasn't seen Ruby at all in this fight, in seconds, before he starts searching. Getting up, or hurt by something thrown, he is hunting nothing he saw. Once he has seen her in the fight he holds for Aggro Time instead.")]
    [SerializeField, Min(0f)] private float _unseenHoldTime = 1f;
    [Tooltip("How close he must get to where he last knew Ruby was, in metres measured flat and with a clear line to it, to count as there and hold. A place and not a point, so several hunters don't push at each other to stand on one spot.")]
    [SerializeField, Min(0f)] private float _knownSpotRadius = 3f;
    [Tooltip("While he sees Ruby, he calls out her position this often, in seconds (and at once on first sight). Speech reaches others immediately; the shared pool takes longer.")]
    [SerializeField, Min(0.1f)] private float _speakInterval = 1f;

    [Tooltip("How much farther back than the main squad an armed standby enemy holds, in metres: his band starts at the farthest main member's distance from Ruby plus this. With no main member yet he keeps his gun's own band.")]
    [SerializeField, Min(0f)] private float _standbyDistance = 3f;

    [Header("Suspicion")]
    [Tooltip("A disturbance within this distance of where Ruby was last reported is searched for as an attack, not a problem, while that report is recent (Attack Near Time). Measured flat, in metres.")]
    [SerializeField, Min(0f)] private float _attackNearRadius = 20f;
    [Tooltip("How old the newest report of Ruby may be, in seconds, for a disturbance near it to count as an attack. Older and it's just a problem again. An enemy who has seen her ignores this and treats everything as an attack.")]
    [SerializeField, Min(0f)] private float _attackNearTime = 15f;
    [Tooltip("The time he is sent to look at something that raises his job to an attack, so a team goes aggressive together. Counted per enemy for good: each time he is put on a job he wasn't on (started, joined, called in). Distractions and glances don't count.")]
    [SerializeField, Min(1)] private int _sentToLookThreshold = 3;

    [Header("Searching")]
    [Tooltip("The longest a search lasts once he has reached the spot, in seconds. The walk there doesn't count, and a team that has looked at the whole area finishes sooner.")]
    [SerializeField, Min(0f)] private float _searchDuration = 5f;
    [Tooltip("How close he gets to a search spot, in metres. If something blocks the way once he's this near (another enemy already standing on the spot, say), he looks from where he stands instead of pushing at it.")]
    [SerializeField, Min(0.5f)] private float _searchReach = 1.5f;
    [Tooltip("The longest he walks to a search spot before searching wherever he has got to, in seconds. Keeps one he can't reach (blocked, or a crowded doorway) from walking forever.")]
    [SerializeField, Min(1f)] private float _searchTravelLimit = 20f;
    [Tooltip("How far, in degrees, his body sweeps to each side of his way while he clears a Search's area. He keeps walking his way, and his view goes with his body.")]
    [SerializeField, Range(0f, 90f)] private float _walkSweepAngle = 45f;
    [Tooltip("How much each look varies while he waits in a search for somewhere free to go, as a share of Look Time: at 0.5 each lasts from half to one and a half times Look Time.")]
    [SerializeField, Range(0f, 0.9f)] private float _roamLookVariation = 0.5f;
    [Tooltip("How close a follower keeps behind the enemy who goes to check, in metres. He stops here and holds, facing the spot, so the team trails the checker and never crowds him.")]
    [SerializeField, Min(1f)] private float _followDistance = 3f;

    [Header("Looking")]
    [Tooltip("How long he looks around a spot he went to check, in seconds, before standing down. A search for an attack looks for Search Duration instead.")]
    [SerializeField, Min(0.1f)] private float _lookTime = 1.5f;
    [Tooltip("How long he holds still after finding nothing, in seconds: the gesture, until there is an animation for it.")]
    [SerializeField, Min(0f)] private float _standDownTime = 1f;
    [Tooltip("How far, in degrees, his body turns to each side of where he was facing while he looks around.")]
    [SerializeField, Range(0f, 180f)] private float _lookSweepAngle = 70f;
    [Tooltip("Seconds for one full sweep, left to right and back.")]
    [SerializeField, Min(0.2f)] private float _lookSweepPeriod = 2f;

    [Header("Provoked")]
    [Tooltip("How long he turns to look at a lesser disturbance while searching, in seconds. The search waits meanwhile, so he doesn't lose search time.")]
    [SerializeField, Min(0.1f)] private float _distractionTime = 1f;
    [Tooltip("How many provocations inside Distraction Memory it takes before he has had enough of Ruby and goes after her, knowing where she is at that moment. Only things she is doing count, not a body or gun lying there.")]
    [SerializeField, Min(1)] private int _distractionThreshold = 3;
    [Tooltip("How long a distraction counts toward the jig, in seconds.")]
    [SerializeField, Min(1f)] private float _distractionMemory = 12f;
    [Tooltip("A disturbance within this long of the last counted one is the same disturbance, in seconds: a thrown body thuds about ten times as it tumbles, and a firefight is a run of shots.")]
    [SerializeField, Min(0f)] private float _distractionGap = 2f;

    [Header("Patrol")]
    [Tooltip("Walked while unaware. Empty = he idles at his post. One point 4 m or more across (a circle's diameter, a rectangle's longer side) is wandered rather than stood on. Edit with the Scene-view handles when this enemy is selected.")]
    [SerializeField] private PatrolRoute _safeRoute = new();
    [Tooltip("Walked instead of the Safe route once he's aggressive: after he has seen Ruby or been sent after her (an attack search, or a glance at a body). Edit it with the same Scene-view handles (the toggle below picks which route they edit). Empty: a patroller keeps walking his Safe route, and an idle enemy stays at his post, alert.")]
    [SerializeField] private PatrolRoute _aggressiveRoute = new();
    [Tooltip("Default wait at each patrol point; a point's own Pause Time overrides it.")]
    [SerializeField, Min(0f)] private float _nodePauseTime = 1.5f;
    [Tooltip("An idle enemy this close to his post, in metres, counts as at it. Where he starts is his post. Never below 0.6: the navigator reports arrival within 0.5 m, and a tighter tolerance would leave him 'away' from a post he has reached.")]
    [SerializeField, Min(0.6f)] private float _postTolerance = 1f;
    [Tooltip("When every patrol point fails in a row, wait this long before trying the route again.")]
    [SerializeField, Min(0f)] private float _routeRetryTime = 3f;

    [Header("Body")]
    [Tooltip("Seconds a surviving enemy stands getting up after a fall, unable to move or shoot, before it hunts again.")]
    [SerializeField, Min(0f)] private float _getUpTime = 1f;
    [Tooltip("How bright a corpse's colours are, as a share of the living enemy's, so the dead read at a glance.")]
    [SerializeField, Range(0f, 1f)] private float _corpseBrightness = 0.35f;
    [Tooltip("How fast the gun pops up out of the enemy's hand when the chain grabs him, in m/s: a small hop, to sell the grab.")]
    [SerializeField, Min(0f)] private float _grabPopUp = 3f;
    [Tooltip("How fast the popped gun moves away from his body, in m/s, so it clears his hands.")]
    [SerializeField, Min(0f)] private float _grabPopOut = 2f;

    [Header("Gun")]
    [Tooltip("Unarmed, an enemy goes for a free dropped gun within this radius. 0 = never re-arms.")]
    [SerializeField, Min(0f)] private float _rearmRadius = 10f;

    [Header("References")]
    [SerializeField] private PlayerController _player;

    private readonly StateMachine _state = new();
    private CapsuleCollider _capsule;
    private float _nextRearm;   // after giving up on a gun, no new attempt before this time

    public float MotionRadius => _motionRadius;
    public float DraggedDistance => _draggedDistance;
    public float KnownSpotRadius => _knownSpotRadius;
    public float PersonalSpace => _personalSpace;
    public float BodyNoticeRange => _bodyNoticeRange;
    public float ScanInterval => _scanInterval;
    internal float GrabPopUp => _grabPopUp;
    internal float GrabPopOut => _grabPopOut;
    internal float CorpseBrightness => _corpseBrightness;

    public float ChaseSpeed => _chaseSpeed;
    public float PatrolSpeed => _patrolSpeed;
    public float SearchSpeed => _searchSpeed;
    public float ClearingSpeed => _searchSpeed * _clearingSpeedFactor;
    internal float WalkSweepAngle => _walkSweepAngle;
    public float AggroTime => _aggroTime;
    public float SightFollowThrough => _sightFollowThrough;
    public float UnseenHoldTime => _unseenHoldTime;
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
    internal float SpeakInterval => _speakInterval;
    internal float AggressiveSpeedFactor => _aggressiveSpeedFactor;
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

    internal float TurnSpeed => _turnSpeed;
    internal float LookSweepAngle => _lookSweepAngle;
    internal float LookSweepPeriod => _lookSweepPeriod;

    internal Vector3 EyePosition => transform.position + Vector3.up * _eyeHeight;

    public float ViewRange => _viewRange;
    internal float ViewAngle => _viewAngle;
    internal float CloseRadius => _closeRadius;
    internal float GiveUpRadius => _giveUpRadius;
    // Start fills it after Awake builds the Vision, so the parts that read Ruby (EnemyMovement, EnemyAwareness, EnemyVision, EnemyGizmos) go through here on every call, never a copy taken in Awake.
    internal PlayerController Player => _player;

    public Rigidbody Body { get; private set; }
    /// <summary>What he knows about Ruby: his own sightings and what he was told.</summary>
    public EnemyMemory Memory { get; private set; }
    /// <summary>What he hears, and what else he notices without sight.</summary>
    public EnemySenses Senses { get; private set; }
    /// <summary>His view cone, close circle and line of sight.</summary>
    public EnemyVision Vision { get; private set; }
    public EnemyReactions Reactions { get; private set; }
    /// <summary>His walking, turning and facing, and the walks the states ask for.</summary>
    public EnemyMovement Movement { get; private set; }
    /// <summary>What he takes in each frame: sight of Ruby, what he learns and says, and the newest thing he knows.</summary>
    public EnemyAwareness Awareness { get; private set; }
    /// <summary>His record of the chain, and the gun and corpse handling that goes with it.</summary>
    public EnemyBodyState BodyState { get; private set; }
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

    private void Awake()
    {
        Body = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();
        Health = GetComponent<Health>();
        Memory = new EnemyMemory();
        Senses = new EnemySenses(this);
        Vision = new EnemyVision(this);
        Reactions = new EnemyReactions(this);
        Movement = new EnemyMovement(this, _capsule);
        Awareness = new EnemyAwareness(this);
        BodyState = new EnemyBodyState(this);

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
        Awareness.Update();
        DebugDrawPath();
    }

    private void FixedUpdate()
    {
        _state.FixedTick();
        if (!Health.IsDead && !IsOverridden)
            Senses.Scan();
    }

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

    private void OnDrawGizmosSelected() => EnemyGizmos.DrawSelected(this);

    private void OnDrawGizmos() => EnemyGizmos.DrawRoutes(this);

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

    private bool DetectsPlayer() => Vision.DetectsPlayer();

    /// <summary>
    /// After giving up on every gun in reach, wait Route Retry Time before looking again. Without
    /// it, an enemy with only unreachable guns around would flicker between hunting and re-arming.
    /// </summary>
    public void WaitBeforeRearming() => _nextRearm = Time.time + _routeRetryTime;

    private bool WantsGun() => Arms != null && !Arms.IsArmed && _rearmRadius > 0f && Time.time >= _nextRearm && Rearm.CanFindGun();

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
        BodyState.DropGun();
        Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity));
        _state.SetState(Ragdoll);
    }

    // Externally driven transitions. Nothing can poll for these, so they bypass the table.
    // EnemyReactions.Investigate is one too: a sound happens once, so there is nothing for the table to poll.
    /// <summary>Sends him into Aggro (hurt, or told by an enemy who sees Ruby). True when that moved him: not when he was already there, or can't act.</summary>
    public bool Alert()
    {
        if (IsOverridden) return false;
        if (_state.Current == Aggro) return false;
        _state.SetState(Aggro);
        return true;
    }

    private void HandleDamaged(float amount) => Alert();

    // Corpses too: a dead enemy stays a body the chain can swing and throw.
    public bool CanBeGrabbed => true;

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld => BodyState.IsHeld;

    /// <summary>
    /// The job he is on, or null: a handful of enemies dealing with one thing together (GroupJob).
    /// Only the job board sets it, as it adds and removes members, and SearchingState gives him up on exit.
    /// </summary>
    public GroupJob Job { get; internal set; }

    /// <summary>
    /// The fight he is in, or null: the squads of enemies after Ruby together (Fight). Only the fight
    /// board sets it, as it joins and drops members by polling their state.
    /// </summary>
    public Fight Fight { get; internal set; }

    /// <summary>In a fight and not on its main squad: he holds back. False out of any fight.</summary>
    public bool IsStandby => Fight != null && !Fight.IsMain(this);

    public float StandbyDistance => _standbyDistance;

    public void EnterGrabbed()
    {
        BodyState.PopGun();
        BodyState.NoteHeld();
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
        // Let go mid-spin he flies out along her aim, which is a throw to anyone watching. A plain drop isn't.
        BodyState.NoteReleased(wasSpun);
        if (Health.IsDead)
        {
            Dead.LetGo(Body.linearVelocity);
            return;
        }
        if (_state.Current != Grabbed) return;

        Ragdoll.Launch(VelocityUtil.Flatten(Body.linearVelocity));
        _state.SetState(Ragdoll);
    }

    public void Release(Vector3 velocity)
    {
        BodyState.NoteReleased(true);
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

    /// <summary>Forwards to EnemyMovement.MoveToward; IPathAgent needs it here.</summary>
    public void MoveToward(Vector3 target, float speed, bool mustReach) => Movement.MoveToward(target, speed, mustReach);

    /// <summary>Forwards to EnemyMovement.Stop; states keep calling Enemy.Stop().</summary>
    public void Stop() => Movement.Stop();

    /// <summary>A clear line at his eye height to a place on the floor. Raised to his eyes because a spot can lie at or under floor level, where the floor itself would block it.</summary>
    internal bool CanSeeSpot(Vector3 point)
    {
        point.y = EyePosition.y;
        return Vision.HasClearSight(point, null);
    }

    // The one way into Searching: whoever sends him there asks first (RequestSearch) and Enter takes
    // it. A default start at some remembered spot let a caller that forgot to ask search at the wrong place, silently.
    private SearchRequest? _pendingSearch;

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

    /// <summary>The speed of the last move it was told to make, 0 once stopped: how fast it's trying to go.</summary>
    public float MoveSpeed => Movement.IntendedSpeed;
    public WeaponHold Weapon => IsArmed ? WeaponHold.Pistol : WeaponHold.None;

    /// <summary>What its body is doing, for its animation. Firing, it aims; moving otherwise, it runs.</summary>
    public AnimStance Stance => _state.Current switch
    {
        AggroState => Aggro.Firing ? AnimStance.Aim : Movement.IntendedSpeed > 0f ? AnimStance.Run : AnimStance.Rest,
        RearmState => Movement.IntendedSpeed > 0f ? AnimStance.Run : AnimStance.Rest,
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

    /// <summary>The state he is in, for EnemyReactions and EnemyAwareness to read. Reaching the machine itself stays with the controller.</summary>
    internal IState CurrentState => _state.Current;

    /// <summary>Ruby, while it's aiming at her.</summary>
    public bool TryFindTarget(out Vector3 point)
    {
        point = _player != null ? _player.transform.position : default;
        return _player != null && Stance == AnimStance.Aim;
    }
}
