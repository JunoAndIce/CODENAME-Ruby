using UnityEngine;

/// <summary>
/// Lost the player and hunting the last place they were seen. This is what makes
/// disengagement read as deliberate rather than the enemy instantly forgetting you.
/// </summary>
public class SearchingState : EnemyStateBase
{
    private Vector3 _lastKnownPosition;
    private float _searchTimer;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Searching;

    public bool SearchExpired => _searchTimer >= Enemy.SearchDuration;

    public override void Enter()
    {
        _searchTimer = 0f;
        _lastKnownPosition = Enemy.Chase.LastSeen;
    }

    public override void Tick() => _searchTimer += Time.deltaTime;

    public override void FixedTick()
    {
        if (HasArrived) { Enemy.Stop(); return; }

        Enemy.MoveToward(_lastKnownPosition, Enemy.SearchSpeed);
    }

    private bool HasArrived
    {
        get
        {
            Vector3 delta = _lastKnownPosition - Enemy.transform.position;
            delta.y = 0f;
            return delta.sqrMagnitude < 0.25f;   // within 0.5m
        }
    }
}
