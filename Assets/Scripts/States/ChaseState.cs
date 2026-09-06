using UnityEngine;

public class ChaseState : EnemyStateBase
{
    private float _outOfRangeTimer;
    private Vector3 _lastSeen;
    
    public ChaseState(EnemyController enemy) : base(enemy) { }
    public override EnemyState Id => EnemyState.Alert;
    public bool LostPlayer => _outOfRangeTimer >= Enemy.AggroTime;
    public Vector3 LastSeen => _lastSeen;


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

        if (Enemy.DistanceToPlayer() <= Enemy.GiveUpRadius)
        {
            _outOfRangeTimer = 0f;
            // _lastSeen = player.position;   // recorded while we can still see them
            return;
        }

        _outOfRangeTimer += Time.deltaTime;
    }

    public override void FixedTick()
    {
        Transform player = Enemy.PlayerTransform;
        if (player == null) return;

        Enemy.MoveToward(player.position, Enemy.ChaseSpeed);
    }
}
