using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Health))]
public class EnemyController : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 2f;
    [SerializeField] private float _alertRadius = 12f;
    [SerializeField] private PlayerController _player;
    [SerializeField] private float _giveUpRadius = 18f;  // get outside this -> stop
    [SerializeField] private float _aggroTime = 1.5f;

    private float _outOfRangeTimer;

    private Rigidbody _enemyRB;
    private Health _health;
    private IRagdollBody _ragdoll;
    private EnemyState _state = EnemyState.Idle;

    public EnemyState State => _state;

    private void Awake()
    {
        _enemyRB = GetComponent<Rigidbody>();
        _health = GetComponent<Health>();

        if (!TryGetComponent(out _ragdoll))
            Debug.LogError($"{name} has no IRagdollBody — it cannot be thrown.", this);
    }

    private void Start()
    {
        if (_player == null)
        {
            _player = FindAnyObjectByType<PlayerController>();
        }
    }

    private void OnEnable()
    {
        _health.OnDamaged += HandleDamaged;
        _health.OnDied += HandleDied;
    }

    private void OnDisable()
    {
        _health.OnDamaged -= HandleDamaged;
        _health.OnDied -= HandleDied;
    }

    public void Alert()
    {
        if (_state == EnemyState.Grabbed || _state == EnemyState.Dead) return;
        if (_state == EnemyState.Alert) return;

        SetState(EnemyState.Alert);
    }

    public void EnterGrabbed()
    {
        SetState(EnemyState.Grabbed);
    }

    public void Release(Vector3 velocity)
    {
        _ragdoll.Ragdoll(YConstraint.Flatten(velocity));
        SetState(EnemyState.Ragdoll);
    }

    private void Update()
    {
        switch (_state)
        {
            case EnemyState.Idle:
                if (DistanceToPlayer() <= _alertRadius) Alert();
                break;
            case EnemyState.Alert:
                FaceTarget();
                if (DistanceToPlayer() > _giveUpRadius)
                {
                    _outOfRangeTimer += Time.deltaTime;
                    if (_outOfRangeTimer >= _aggroTime) SetState(EnemyState.Idle);
                }
                else
                {
                    _outOfRangeTimer = 0f;
                }
                break;
            case EnemyState.Ragdoll:
                return;
            case EnemyState.Grabbed:
                return;
            case EnemyState.Dead:
                return;
        }
    }

    private void FixedUpdate()
    {

        if (_state != EnemyState.Alert) return;
        FollowPlayer();
    }

    private void FaceTarget()
    {
        Vector3 target = _player.transform.position;
        target.y = transform.position.y;
        transform.LookAt(target);
    }

    private void FollowPlayer()
    {
        Vector3 v = transform.forward * _moveSpeed;
        v.y = _enemyRB.linearVelocity.y;
        _enemyRB.linearVelocity = v;
    }

    private float DistanceToPlayer()
    {
        Vector3 delta = _player.transform.position - transform.position;
        delta.y = 0f;
        return delta.magnitude;
    }

    private void HandleDamaged(float amount)
    {
        // TODO: being shot from across the room wakes an Idle enemy -> Alert()
    }

    private void HandleDied()
    {
        // TODO: SetState(EnemyState.Dead)
    }

    // ------------------------------------------------------------------
    // State machine
    // ------------------------------------------------------------------

    private void SetState(EnemyState next)
{
    if (next == _state) return;
    if (_state == EnemyState.Dead) return;

    // EXIT
    switch (_state)
    {
        case EnemyState.Grabbed:
            break;
    }

    _state = next;

    // ENTER
    switch (_state)
    {
        case EnemyState.Idle:
            _enemyRB.linearVelocity = Vector3.zero;
            break;
        case EnemyState.Grabbed:
            _enemyRB.isKinematic = true;
            break;
        case EnemyState.Dead:
            Destroy(gameObject);
            break;
        default:
            break;
    }
}


    // ------------------------------------------------------------------
    // Editor
    // ------------------------------------------------------------------

    private void OnValidate()
    {
        _giveUpRadius = Mathf.Max(_giveUpRadius, _alertRadius + 1f);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, _alertRadius);

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, _giveUpRadius);
    }
}
