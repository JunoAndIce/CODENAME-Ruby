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
    [Tooltip("Patrol speed once they're aggressive, as a multiple of Patrol Speed: above 1 hurries them, below 1 slows them to a wary creep (0.5 is half speed). Applies to whichever route they walk, and to an idle enemy walking back to their post.")]
    [SerializeField, Range(0.1f, 3f)] private float _aggressiveSpeedFactor = 1.25f;
    [SerializeField] private float _searchSpeed = 1.5f;
    [Tooltip("Their speed while they clear a Search's area or trail behind the checker, as a multiple of Search Speed: below 1, so they creep and look. Walking in to the area stays at full Search Speed. One factor for both, since both are a searcher slowed to look.")]
    [FormerlySerializedAs("_clearingSpeedFactor")] [SerializeField, Range(0.1f, 1f)] private float _searchCreepFactor = 0.75f;
    [Tooltip("How fast the body turns while walking a path, in degrees a second: lower makes wider arcs round bends. The legs' own turn on the model (CharacterAnimator's Turn Speed) is separate.")]
    [SerializeField, Min(1f)] private float _turnSpeed = 720f;
    [Tooltip("The room they keep round other enemies, in metres from centre to centre. Walking any path they curve away from anyone inside this, harder the closer they are, and they don't choose a roaming or wandering spot inside it. Capsules touch at about 0.7 m, so below that it does nothing. 0 turns it off.")]
    [SerializeField, Min(0f)] private float _personalSpace = 1.5f;
    [Tooltip("Seconds without getting closer before a patrol move counts as blocked (another enemy on the spot, a prop, a jammed doorway).")]
    [SerializeField, Min(0.1f)] private float _stuckTime = 1.5f;

    [Header("Sight")]
    [Tooltip("How far they see in front of them, within View Angle, if in sight: Ruby, and everything else they notice by sight (corpses, loose guns, moving bodies, someone on the chain). Only the cone: nothing behind them, except Ruby inside Close Radius.")]
    [FormerlySerializedAs("_alertRadius")] [SerializeField, Min(0f)] private float _viewRange = 12f;
    [Tooltip("How wide the view in front of them is, in degrees, centred on their facing.")]
    [SerializeField, Range(1f, 360f)] private float _viewAngle = 100f;
    [Tooltip("Whichever way they face, they notice Ruby this close, if in sight, so she can't stand right behind them. Ruby only: a corpse or gun behind them goes unseen.")]
    [SerializeField, Min(0f)] private float _closeRadius = 2f;
    [Tooltip("Height above the enemy's centre that sight is cast from.")]
    [SerializeField] private float _eyeHeight = 0.5f;

    [Header("Senses")]
    [Tooltip("How far a body on the chain must be moved from where it was grabbed, in metres measured flat, before they take it as Ruby dragging it and go for her. Keep it above the charging throw's jiggle (0.1 m).")]
    [SerializeField, Min(0f)] private float _draggedDistance = 0.5f;

    [Header("Fighting")]
    [Tooltip("Seconds without seeing Ruby, theirs or a fight mate's, before the hunt turns to a search. Out of sight they hunt where she was heading meanwhile, and look round there. Any glimpse starts it over.")]
    [SerializeField, Min(0.5f)] private float _loseSightTime = 4f;
    [Tooltip("Seconds they turn and draw on first seeing Ruby, coming in from calm, before they may fire: her window to act first. The Draw slot's clip is stretched to fit. 0 = they fire as soon as the gun allows.")]
    [SerializeField, Min(0f)] private float _drawTime = 0.5f;
    [Tooltip("While they see Ruby, they call out her position this often, in seconds (and at once on first sight). Speech reaches others immediately; the shared pool takes longer.")]
    [SerializeField, Min(0.1f)] private float _speakInterval = 1f;
    [Tooltip("How far ahead of where they lost Ruby they guess she has gone, in path metres along her heading: through the doorway she ran at, behind the side of the crate she was making for. Kept short on purpose: Ruby runs fast, and a guess derived from her speed landed rooms away.")]
    [SerializeField, Min(1f)] private float _guessDistance = 8f;

    [Tooltip("The least distance between two fighters' firing spots, in metres, measured flat. Each armed enemy in a fight claims a spot of their own round Ruby and nobody else takes one this close to it. Also how far past their band Ruby moves before they pick again (half of it).")]
    [FormerlySerializedAs("_squadSpacing")] [SerializeField, Min(0.5f)] private float _spotSpacing = 2f;

    [Header("Suspicion")]
    [Tooltip("A disturbance within this distance of where Ruby was last reported is searched for as an attack, not a problem, while that report is recent (Attack Near Time). Measured flat, in metres.")]
    [SerializeField, Min(0f)] private float _attackNearRadius = 20f;
    [Tooltip("How old the newest report of Ruby may be, in seconds, for a disturbance near it to count as an attack. Older and it's just a problem again. An enemy who has seen her ignores this and treats everything as an attack.")]
    [SerializeField, Min(0f)] private float _attackNearTime = 15f;
    [Tooltip("From this look on, every job they are put on becomes a Search for an attack, for the whole team, so a team goes aggressive together. Counted per enemy for good: each time they are put on a job they weren't on (started, joined, called in). Distractions and glances don't count.")]
    [SerializeField, Min(1)] private int _sentToLookThreshold = 3;

    [Header("Searching")]
    [Tooltip("How long a search of their own for an attack lasts once they have reached the spot, in seconds: a glance at something already known, or any search in a scene with no Intel. The walk there doesn't count. A group Search has no time cap: it ends once its area is cleared and Intel's Search Minimum Time has passed.")]
    [SerializeField, Min(0f)] private float _searchDuration = 5f;
    [Tooltip("How close they get to a search spot, in metres. If something blocks the way once they're this near (another enemy already standing on the spot, say), they look from where they stand instead of pushing at it.")]
    [SerializeField, Min(0.5f)] private float _searchReach = 1.5f;
    [Tooltip("The longest they walk to a search spot before searching wherever they have got to, in seconds. Keeps one they can't reach (blocked, or a crowded doorway) from walking forever. Also how long a provoked enemy runs at where they were told Ruby is before Lose Sight Time counts down again.")]
    [SerializeField, Min(1f)] private float _searchTravelLimit = 20f;
    [Tooltip("How far, in degrees, their body sweeps to each side of their way while they clear a Search's area. They keep walking their way, and their view goes with their body.")]
    [SerializeField, Range(0f, 90f)] private float _walkSweepAngle = 45f;
    [Tooltip("How close a follower keeps behind the enemy who goes to check, in metres. They stop here and hold, facing the spot, so the team trails the checker and never crowds them.")]
    [SerializeField, Min(1f)] private float _followDistance = 3f;

    [Header("Looking")]
    [Tooltip("How long they look around a spot they went to check, in seconds, before standing down. A search for an attack looks for Search Duration instead.")]
    [SerializeField, Min(0.1f)] private float _lookTime = 1.5f;
    [Tooltip("How long they hold the stand-down beat after finding nothing, in seconds. Its clip is stretched to fit.")]
    [SerializeField, Min(0f)] private float _standDownTime = 1f;
    [Tooltip("Seconds they stop and turn toward something before going to check it, coming in from calm. The Notice slot's clip is stretched to fit.")]
    [SerializeField, Min(0f)] private float _noticeTime = 0.6f;
    [Tooltip("Seconds they hold their gun on where they last saw Ruby, after losing her, before the search starts. The Lose slot's clip is stretched to fit.")]
    [SerializeField, Min(0f)] private float _loseTime = 0.5f;
    [Tooltip("How far, in degrees, their body turns to each side of where they were facing while they look around.")]
    [SerializeField, Range(0f, 180f)] private float _lookSweepAngle = 70f;
    [Tooltip("Seconds for one full sweep, left to right and back.")]
    [SerializeField, Min(0.2f)] private float _lookSweepPeriod = 2f;

    [Header("Provoked")]
    [Tooltip("How long they turn to look at a lesser disturbance while searching, in seconds. The search waits meanwhile, so they don't lose search time.")]
    [SerializeField, Min(0.1f)] private float _distractionTime = 1f;
    [Tooltip("How many provocations inside Distraction Memory it takes before they have had enough of Ruby and go after her, knowing where she is at that moment. Only things she is doing count, not a body or gun lying there.")]
    [SerializeField, Min(1)] private int _distractionThreshold = 3;
    [Tooltip("How long a distraction counts toward the jig, in seconds.")]
    [SerializeField, Min(1f)] private float _distractionMemory = 12f;
    [Tooltip("A disturbance within this long of the last counted one is the same disturbance, in seconds: a thrown body thuds about ten times as it tumbles, and a firefight is a run of shots.")]
    [SerializeField, Min(0f)] private float _distractionGap = 2f;

    [Header("Patrol")]
    [Tooltip("Walked while unaware. Empty = they idle at their post. One point 4 m or more across (a circle's diameter, a rectangle's longer side) is wandered rather than stood on. Edit with the Scene-view handles when this enemy is selected.")]
    [SerializeField] private PatrolRoute _safeRoute = new();
    [Tooltip("Walked instead of the Safe route once they're aggressive: after they have seen Ruby or been sent after her (an attack search, or a glance at a body). Edit it with the same Scene-view handles (the toggle below picks which route they edit). Empty: a patroller keeps walking their Safe route, and an idle enemy stays at their post, alert.")]
    [SerializeField] private PatrolRoute _aggressiveRoute = new();
    [Tooltip("Default wait at each patrol point; a point's own Pause Time overrides it.")]
    [SerializeField, Min(0f)] private float _nodePauseTime = 1.5f;
    [Tooltip("A pause at a patrol point this long or longer, in seconds, isn't spent standing still: they move about inside the point's area for the length of it.")]
    [SerializeField, Min(0f)] private float _longPauseTime = 10f;
    [Tooltip("In a long pause, the shortest they stay at one spot before moving to another in the point's area, in seconds.")]
    [SerializeField, Min(0.1f)] private float _pauseRoamMin = 3f;
    [Tooltip("In a long pause, the longest they stay at one spot before moving to another in the point's area, in seconds. Never below the shortest.")]
    [SerializeField, Min(0.1f)] private float _pauseRoamMax = 6f;
    [Tooltip("A firing-range spot to use as their post instead of where they stand. They walk there, face its target and practise when armed. Used only when they have no Safe route to walk: with one, the table sends them to Patrol and they never idle at the spot. Empty means their post is where they start.")]
    [SerializeField] private RangeSpot _postSpot;
    [Tooltip("At a range post, the shortest wait between practice shots, in seconds.")]
    [SerializeField, Min(0.1f)] private float _practiceIntervalMin = 1.2f;
    [Tooltip("At a range post, the longest wait between practice shots, in seconds. Never below the shortest.")]
    [SerializeField, Min(0.1f)] private float _practiceIntervalMax = 2.5f;

    [Header("Body")]
    [Tooltip("Seconds a surviving enemy stands getting up after a fall, unable to move or shoot, before it hunts again.")]
    [SerializeField, Min(0f)] private float _getUpTime = 1f;
    [Tooltip("How bright a corpse's colours are, as a share of the living enemy's, so the dead read at a glance.")]
    [SerializeField, Range(0f, 1f)] private float _corpseBrightness = 0.35f;
    [Tooltip("Held on the chain, alive or dead, an enemy's feet still drag on the floor: their slide slows by this much, in m/s every second, so a yank or a drag doesn't send them gliding on. Their capsule has no friction (so they don't drag crates along by rubbing them), and nothing else slows them while held.")]
    [SerializeField, Min(0f)] private float _heldFriction = 10f;

    [Header("Gun")]
    [Tooltip("Unarmed, an enemy goes for a free dropped gun within this radius. 0 = never re-arms.")]
    [SerializeField, Min(0f)] private float _rearmRadius = 10f;

    [Header("References")]
    [SerializeField] private PlayerController _player;

    private readonly StateMachine _state = new();
    private CapsuleCollider _capsule;
    private float _nextRearm;   // after giving up on a gun, no new attempt before this time

    // Plumbing, not design: none of these was ever tuned per enemy or per level, so they were taken off the Inspector.
    private const float ScanIntervalSeconds = 0.2f;   // seconds between sweeps for moving bodies, corpses and guns: a cost knob, each enemy offset at random
    private const float PostToleranceMetres = 1f;     // this close to their post counts as at it: above the navigator's 0.5 m arrival, or a reached post read as "away"
    private const float RouteRetrySeconds = 3f;       // every patrol point failed in a row: wait this long before trying the route again
    private const float GrabPopUpSpeed = 3f;          // m/s the gun hops up out of their hand when the chain grabs them, to sell the grab
    private const float GrabPopOutSpeed = 2f;         // m/s the popped gun moves away from their body, so it clears their hands

    public float DraggedDistance => _draggedDistance;
    public float PersonalSpace => _personalSpace;
    public float ScanInterval => ScanIntervalSeconds;
    internal float GrabPopUp => GrabPopUpSpeed;
    internal float GrabPopOut => GrabPopOutSpeed;
    internal float CorpseBrightness => _corpseBrightness;

    public float ChaseSpeed => _chaseSpeed;
    public float PatrolSpeed => _patrolSpeed;
    public float SearchSpeed => _searchSpeed;
    public float SearchCreepSpeed => _searchSpeed * _searchCreepFactor;
    internal float WalkSweepAngle => _walkSweepAngle;
    public float LoseSightTime => _loseSightTime;
    public float SearchDuration => _searchDuration;
    public float SearchTravelLimit => _searchTravelLimit;
    public float SearchReach => _searchReach;
    public float LookTime => _lookTime;
    public float DrawTime => _drawTime;
    public float NoticeTime => _noticeTime;
    public float LoseTime => _loseTime;
    public float StandDownTime => _standDownTime;
    public float FollowDistance => _followDistance;
    public float DistractionTime => _distractionTime;
    internal int DistractionThreshold => _distractionThreshold;
    internal float DistractionMemory => _distractionMemory;
    internal float DistractionGap => _distractionGap;
    internal float AttackNearTime => _attackNearTime;
    internal float SpeakInterval => _speakInterval;
    internal float GuessDistance => _guessDistance;
    internal float AggressiveSpeedFactor => _aggressiveSpeedFactor;
    internal float AttackNearRadius => _attackNearRadius;
    internal int SentToLookThreshold => _sentToLookThreshold;
    public PatrolRoute SafeRoute => _safeRoute;
    public PatrolRoute AggressiveRoute => _aggressiveRoute;
    public bool HasAggressiveRoute => _aggressiveRoute != null && _aggressiveRoute.Count > 0;

    /// <summary>
    /// Whether they patrol aggressively: they have been after Ruby (an attack search, or a glance at a
    /// body), have seen her, or have been told of her by a teammate who had (on a job). Derived on every
    /// read and never stored, and none is ever cleared, so they never go back.
    /// </summary>
    public bool IsAggressive => Memory.Wary || Memory.KnowsRed;

    /// <summary>
    /// The route they walk now: the Aggressive one when they're aggressive and have one, else the Safe
    /// one. An aggressive enemy with no Aggressive route walking their Safe route is a rule, not a
    /// fallback, and the Inspector says so.
    /// </summary>
    public PatrolRoute ActiveRoute => IsAggressive && HasAggressiveRoute ? _aggressiveRoute : _safeRoute;
    public bool HasActiveRoute => ActiveRoute != null && ActiveRoute.Count > 0;
    public float CurrentPatrolSpeed => IsAggressive ? _patrolSpeed * _aggressiveSpeedFactor : _patrolSpeed;
    /// <summary>Where they started: where an idle enemy stands, and goes back to after a search or a fall.</summary>
    public Vector3 PostPosition { get; private set; }
    public float PostYaw { get; private set; }
    public float PostTolerance => PostToleranceMetres;
    public RangeSpot PostSpot => _postSpot;
    public float PracticeIntervalMin => _practiceIntervalMin;
    public float PracticeIntervalMax => Mathf.Max(_practiceIntervalMin, _practiceIntervalMax);
    public float NodePauseTime => _nodePauseTime;
    public float LongPauseTime => _longPauseTime;
    public float PauseRoamMin => _pauseRoamMin;
    public float PauseRoamMax => _pauseRoamMax;
    public float StuckTime => _stuckTime;

    /// <summary>
    /// How their routes and legs treat props: their state's rule (Forceful in Aggro and Rearm). Careful
    /// before the machine has a state, as at a fresh spawn.
    /// </summary>
    public PropRule PropRule => _state.Current is EnemyStateBase state ? state.PropRule : PropRule.Careful;
    public float RouteRetryTime => RouteRetrySeconds;
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
    // Start fills it after Awake builds the Vision, so the parts that read Ruby (EnemyMovement, EnemyAwareness, EnemyVision, EnemyGizmos) go through here on every call, never a copy taken in Awake.
    internal PlayerController Player => _player;

    public Rigidbody Body { get; private set; }
    /// <summary>What they know about Ruby: their own sightings and what they were told.</summary>
    public EnemyMemory Memory { get; private set; }
    /// <summary>What they hear, and what else they notice without sight.</summary>
    public EnemySenses Senses { get; private set; }
    /// <summary>Their view cone, close circle and line of sight.</summary>
    public EnemyVision Vision { get; private set; }
    public EnemyReactions Reactions { get; private set; }
    /// <summary>Their walking, turning and facing, and the walks the states ask for.</summary>
    public EnemyMovement Movement { get; private set; }
    /// <summary>What they take in each frame: sight of Ruby, what they learn and say, and the newest thing they know.</summary>
    public EnemyAwareness Awareness { get; private set; }
    /// <summary>Their record of the chain, and the gun and corpse handling that goes with it.</summary>
    public EnemyBodyState BodyState { get; private set; }
    /// <summary>The beat they roll through between states (EnemyBeat): cancelled on every change of state.</summary>
    public EnemyBeat Beat { get; private set; }

    /// <summary>The state they were in before the last change: whether Aggro and Searching start a beat depends on it.</summary>
    internal IState PreviousState { get; private set; }

    /// <summary>The beat the last change of state cut short, None when none was running. Seeing Ruby during the Lose beat skips the draw.</summary>
    internal AnimBeat BeatCutShort { get; private set; }

    /// <summary>Coming in from calm: the draw only comes from here, never back from Rearm or a fall, nor on a glimpse mid-fight.</summary>
    internal bool CameFromCalm => PreviousState is IdleState or PatrolState or SearchingState;
    /// <summary>The scene's shared pool, or null when there is none (an error was logged at Start).</summary>
    public Intel Intel { get; private set; }
    public Health Health { get; private set; }
    public IRagdollBody RagdollBody { get; private set; }
    public GridNavigator Navigator { get; private set; }
    public PatrolState Patrol { get; private set; }
    public SearchingState Searching { get; private set; }
    public IdleState Idle { get; private set; }
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
        Beat = new EnemyBeat(this);

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

        _state.Changed += OnStateChanged;
        BuildTransitions();
    }

    // Every change of state comes through here, the table's and SetState's alike, so no route out of the
    // hunt (a lost fight, a grab, a knockdown, death, or one added later) can leave ProvokedUnseen set and
    // make a later fight blind: getting up from a throw is meant to bring back all-round sight. Rearm
    // keeps it: an unarmed provoked enemy going for a gun hasn't seen her, and they hand straight back to
    // Aggro, where clearing it would have given them all-round sight on hearsay.
    private void OnStateChanged(IState from, IState to)
    {
        // A beat belongs to the state that started it: any change ends it, which is what lets a hit, a grab or
        // Ruby cut one short through the table with no rule of the beat's own. Before the new state's Enter, so
        // that Enter can start its own and read what was cut.
        PreviousState = from;
        BeatCutShort = Beat.Cancel();

        if (!ProvokedUnseen || to is AggroState or RearmState) return;
        ProvokedUnseen = false;
        DebugLog.Info($"{name} leaves the hunt for {to.GetType().Name} without having laid eyes on Ruby: they are no longer provoked, and their next fight sees her as any does.", this);
    }

    private void Start()
    {
        if (_player == null) _player = FindAnyObjectByType<PlayerController>();
        // A range post is the spot, so they walk there at the start of play.
        PostPosition = _postSpot != null ? _postSpot.Position : transform.position;
        PostYaw = _postSpot != null ? _postSpot.FacingYaw : transform.eulerAngles.y;
        _state.SetState(HasActiveRoute ? Patrol : (EnemyStateBase)Idle);

        Intel = FindAnyObjectByType<Intel>();
        if (Intel == null) Debug.LogError($"{name}: no Intel in the scene — officers can't share what they see or hear each other.", this);
        else Intel.Register(this);
    }

    private void OnDestroy()
    {
        // The fight board can still poll them (AnyInContact) until its next prune: a destroyed enemy has
        // left the hunt like any other route out, so nothing they leave behind reads as provoked.
        ProvokedUnseen = false;
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
        // After the chain has solved this step (the grapple runs first), as the floor would.
        if (IsHeld) Movement.DragFeet(_heldFriction);
        if (!Health.IsDead && !IsOverridden)
        {
            // After the state, which waits while a beat runs, so the beat has the last word on the feet and facing.
            Beat.FixedTick();
            Senses.Scan();
        }
    }

    private void OnValidate()
    {
        _pauseRoamMax = Mathf.Max(_pauseRoamMax, _pauseRoamMin);
        if (_postSpot != null && _safeRoute != null && _safeRoute.Count > 0)
            Debug.LogWarning($"{name}: has a Post Spot and a Safe route with points. They walk the route (ActiveRoute falls back to the Safe route), so they idle at the spot, and practises, only when they have no Safe route.", this);
    }

    // Play-mode path overlay: visible in the Scene view without selecting the enemy.
    private void DebugDrawPath()
    {
        Vector3[] corners = Navigator.Corners;
        if (corners == null) return;

        // Cyan: a Careful route, round every prop. Orange: a Forceful one, which may shove through light props.
        Color colour = PropRule == PropRule.Forceful ? new Color(1f, 0.55f, 0.1f) : Color.cyan;
        for (int i = 1; i < corners.Length; i++)
            Debug.DrawLine(corners[i - 1], corners[i], colour);
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

        // Unarmed with a free gun in reach: they go for it from any AI state. The rows above are listed
        // first, so they win: an unarmed enemy who notices Ruby takes Aggro first, and Aggro hands on to Rearm.
        At(Idle, Rearm, new FuncPredicate(WantsGun));
        At(Patrol, Rearm, new FuncPredicate(WantsGun));
        At(Searching, Rearm, new FuncPredicate(WantsGun));
        // Not while drawing: an unarmed officer who has just seen Ruby flinches and turns first.
        At(Aggro, Rearm, new FuncPredicate(() => WantsGun() && !Aggro.Drawing));
        // Always back to the hunt: an enemy only loses its gun to Ruby, so it knows she's around.
        At(Rearm, Aggro, new FuncPredicate(() => Rearm.Armed || Rearm.GaveUp));

        At(Searching, Patrol, new FuncPredicate(() => Searching.Finished && HasActiveRoute));
        At(Searching, Idle, new FuncPredicate(() => Searching.Finished && !HasActiveRoute));

        // The route they walk follows how aggressive they are (IsAggressive), so becoming aggressive can hand an
        // idle enemy a route to walk, or leave a patroller with none for their mode and send them to their post.
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
    public void WaitBeforeRearming() => _nextRearm = Time.time + RouteRetrySeconds;

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
    /// <summary>Sends them into Aggro (hurt, or told by an enemy who sees Ruby). True when that moved them: not when they were already there, or can't act.</summary>
    public bool Alert()
    {
        if (IsOverridden) return false;
        if (_state.Current == Aggro) return false;
        _state.SetState(Aggro);
        return true;
    }

    // A hit that doesn't floor them gives back all-round sight: they fight as any damaged enemy does (user
    // ruling). One that floors them clears it through the state change anyway.
    private void HandleDamaged(float amount)
    {
        if (ProvokedUnseen)
        {
            ProvokedUnseen = false;
            DebugLog.Info($"{name} is hit while provoked: they are no longer blind to Ruby and fights as any damaged enemy does.", this);
        }
        IState before = _state.Current;
        Alert();
        // A hit cuts any beat (spec 2026-11-20-smooth-transitions). A change of state has already cut it, and a draw
        // the hit started from calm is the new state's own; only one still in the same state, shot mid-draw in
        // Aggro, has a beat left running here, and it ends: they fight back at once.
        if (_state.Current == before && Beat.Running)
        {
            DebugLog.Info($"{name} is hit during their {Beat.Kind} beat: it ends.", this);
            Beat.Cancel();
        }
    }

    // Corpses too: a dead enemy stays a body the chain can swing and throw.
    public bool CanBeGrabbed => true;

    /// <summary>On the chain right now. A death mid-swing reads this to stay held.</summary>
    public bool IsHeld => BodyState.IsHeld;

    /// <summary>
    /// The job they are on, or null: a handful of enemies dealing with one thing together (GroupJob).
    /// Only the job board sets it, as it adds and removes members, and SearchingState gives them up on exit.
    /// </summary>
    public GroupJob Job { get; internal set; }

    /// <summary>
    /// The fight they are in, or null: the enemies after Ruby together on their floor (Fight). Only the fight
    /// board sets it, as it joins and drops members by polling their state.
    /// </summary>
    public Fight Fight { get; internal set; }

    public float SpotSpacing => _spotSpacing;

    /// <summary>
    /// Provoked to their limit and sent after Ruby (EnemyReactions.GoAfterRed), they have not yet had her in
    /// their view cone with a clear line. Until then they count as seeing her only with their own eyes
    /// (spec: "At their own limit they run at Ruby, and see her only with their own eyes"): Aggro's
    /// all-round sight doesn't apply, so they run at what they were told, learn no Seen fact, call nothing
    /// out, track nothing live, hold no firing spot and don't fire. Only the provoked: every other
    /// way into Aggro keeps all-round sight. GoAfterRed is the only writer that sets it. EnemyVision
    /// clears it the first time DetectsPlayer holds (every sight reader goes through it there, so the
    /// first to ask that frame clears it for all), and OnStateChanged when they leave the hunt by any route.
    /// </summary>
    public bool ProvokedUnseen { get; internal set; }

    /// <summary>The firing spot they have claimed in their fight, for the gizmo and the state. False with none.</summary>
    public bool TryGetFiringSpot(out Vector3 spot)
    {
        spot = default;
        return Fight != null && Fight.TryGetClaim(this, out spot);
    }

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
        // Let go mid-spin they fly out along her aim, which is a throw to anyone watching. A plain drop isn't.
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

    /// <summary>A clear line at their eye height to a place on the floor. Raised to their eyes because a spot can lie at or under floor level, where the floor itself would block it.</summary>
    internal bool CanSeeSpot(Vector3 point)
    {
        point.y = EyePosition.y;
        return Vision.HasClearSight(point, null);
    }

    // The one way into Searching: whoever sends them there asks first (RequestSearch) and Enter takes
    // it. A default start at some remembered spot let a caller that forgot to ask search at the wrong place, silently.
    private SearchRequest? _pendingSearch;

    /// <summary>
    /// Sends them to a job's place for its reason, as a search: requested from Idle or Patrol, a
    /// retarget if they are already searching (which includes SearchingState.Enter itself: the machine has
    /// made Searching current before calling it, so a job joined from there retargets and never calls
    /// SetState). The job board has already put them on the job's member list.
    /// A search for an attack makes them wary, as any other does.
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
    /// Asks for the search the next entry into Searching will begin: the one way in. Whoever moves them
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
    AnimBeat IAnimatedCharacter.Beat => Beat.Kind;
    public float BeatProgress => Beat.Progress;

    /// <summary>What its body is doing, for its animation. Firing, it aims; moving otherwise, it runs.</summary>
    public AnimStance Stance => _state.Current switch
    {
        AggroState => Aggro.Aiming ? AnimStance.Aim : Movement.IntendedSpeed > 0f ? AnimStance.Run : AnimStance.Rest,
        RearmState => Movement.IntendedSpeed > 0f ? AnimStance.Run : AnimStance.Rest,
        GrabbedState => AnimStance.Restrained,
        RagdollState => Ragdoll.Standing ? AnimStance.GettingUp : AnimStance.Frozen,
        // Tied upright on the chain, a corpse is restrained like the living; off it, it keeps its pose.
        DeadState => IsHeld ? AnimStance.Restrained : AnimStance.Frozen,
        IdleState => Idle.Practising ? AnimStance.Aim : AnimStance.Rest,
        // Having lost Ruby, they hold the gun on where they last saw her for the Lose beat, then lower it to search.
        SearchingState => HoldingLastSpot ? AnimStance.Aim : AnimStance.Rest,
        _ => AnimStance.Rest,
    };

    /// <summary>In the Lose beat with the gun in range of where they last saw Ruby: they hold it on that spot.</summary>
    internal bool HoldingLastSpot => Beat.Kind == AnimBeat.Lose && Arms != null && Arms.InRange(Beat.FacePoint);

    /// <summary>
    /// Held, knocked down or dead: an override state (EnemyStateBase.IsOverride) is running, so the
    /// AI neither senses nor reacts. The one test for it. KnockDown keeps its own set.
    /// </summary>
    public bool IsOverridden => _state.Current is EnemyStateBase { IsOverride: true };

    /// <summary>In the Searching state right now. The job board reads it to check that a member of a job is searching.</summary>
    public bool IsSearching => _state.Current is SearchingState;

    /// <summary>The state they are in, for EnemyReactions and EnemyAwareness to read. Reaching the machine itself stays with the controller.</summary>
    internal IState CurrentState => _state.Current;

    /// <summary>
    /// What their gun points at, or the range target while they practise. In Aggro it is the hunt's own aim point:
    /// Ruby in sight, where they last saw her in the glimpse grace, never her true position through a crate.
    /// </summary>
    public bool TryFindTarget(out Vector3 point)
    {
        if (Idle.Practising && _postSpot != null && _postSpot.Target != null)
        {
            point = _postSpot.Target.position;
            return true;
        }
        if (_state.Current == Aggro)
        {
            point = Aggro.AimPoint;
            return Aggro.Aiming;
        }
        if (HoldingLastSpot)
        {
            point = Beat.FacePoint;
            return true;
        }
        point = _player != null ? _player.transform.position : default;
        return _player != null && Stance == AnimStance.Aim;
    }
}
