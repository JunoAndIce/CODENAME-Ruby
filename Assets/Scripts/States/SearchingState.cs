using UnityEngine;

/// <summary>
/// Lost the player and hunting the last place they were seen. This is what makes
/// disengagement read as deliberate rather than the enemy instantly forgetting you.
/// </summary>
public class SearchingState : EnemyStateBase
{
    // A search from Aggro outranks every map row, so a sound can't pull him off a lost player.
    public const int FromAggroPriority = 100;

    private Vector3 _target;
    private StimulusResponse _reason;
    private int _priority;
    private float _searchTimer;
    // Set by Aim before the transition into this state, so Enter keeps the staged target.
    private bool _staged;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Searching;

    public bool SearchExpired => _searchTimer >= Enemy.SearchDuration;
    public StimulusResponse Reason => _reason;
    public int Priority => _priority;

    /// <summary>Sets where to search and why. Restarts the search, so the new spot gets its full time.</summary>
    public void Aim(Vector3 point, StimulusResponse reason, int priority)
    {
        _target = point;
        _reason = reason;
        _priority = priority;
        _searchTimer = 0f;
        _staged = true;
    }

    public override void Enter()
    {
        _searchTimer = 0f;
        if (_staged)
        {
            _staged = false;
            return;
        }

        // Entered from Aggro (or any table transition): the player's last known spot.
        _target = Enemy.LastKnownPlayerPosition;
        _reason = StimulusResponse.SearchForAttack;
        _priority = FromAggroPriority;
    }

    public override void Tick() => _searchTimer += Time.deltaTime;

    // PathTo stops the enemy itself once it arrives, so arrival needs no handling here.
    public override void FixedTick() => Enemy.PathTo(_target, Enemy.SearchSpeed);
}
