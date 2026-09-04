using UnityEngine;

/// <summary>
/// Sees the player and is closing the distance. This is the old Alert behaviour, and it now
/// owns _outOfRangeTimer — which used to sit on EnemyController meaning nothing in four of
/// the five states.
/// </summary>
public class ChaseState : EnemyStateBase
{
    private float _outOfRangeTimer;
    private Vector3 _lastSeen;
    public ChaseState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Alert;

    public override void Enter()
    {
        _outOfRangeTimer = 0f;
        if (Enemy.PlayerTransform != null) _lastSeen = Enemy.PlayerTransform.position;
    }

    public override void Tick()
    {
        Transform player = Enemy.PlayerTransform;
        if (player == null) return;

        Enemy.FaceTarget();

        float distance = Enemy.DistanceToPlayer();

        if (distance <= Enemy.AttackRange)
        {
            Enemy.ChangeState(Enemy.Attack);
            return;
        }

        if (distance <= Enemy.GiveUpRadius)
        {
            _outOfRangeTimer = 0f;
            _lastSeen = player.position;   // recorded while we can still see them
            return;
        }

        _outOfRangeTimer += Time.deltaTime;
        if (_outOfRangeTimer < Enemy.AggroTime) return;

        Enemy.Searching.SetLastKnownPosition(_lastSeen);
        Enemy.ChangeState(Enemy.Searching);
    }

    public override void FixedTick()
    {
        Transform player = Enemy.PlayerTransform;
        if (player == null) return;
        
        Enemy.MoveToward(player.position, Enemy.ChaseSpeed);
    }
}
