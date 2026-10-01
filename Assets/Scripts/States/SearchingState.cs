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
        _lastKnownPosition = Enemy.LastKnownPlayerPosition;
    }

    public override void Tick() => _searchTimer += Time.deltaTime;

    // PathTo stops the enemy itself once it arrives, so arrival needs no handling here.
    public override void FixedTick() => Enemy.PathTo(_lastKnownPosition, Enemy.SearchSpeed);
}
