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
    private float _searchTimer;   // runs only from arrival: the walk there is not part of the search
    private float _travelTimer;
    private bool _arrived;
    // Set by Aim before the transition into this state, so Enter keeps the staged target.
    private bool _staged;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Searching;

    public bool SearchExpired => _searchTimer >= Enemy.SearchDuration;
    public StimulusResponse Reason => _reason;
    public int Priority => _priority;

    /// <summary>Sets where to search and why. Restarts the search from the walk, so the new spot gets its full time on arrival.</summary>
    public void Aim(Vector3 point, StimulusResponse reason, int priority)
    {
        _target = point;
        _reason = reason;
        _priority = priority;
        ResetTimers();
        _staged = true;
    }

    private void ResetTimers()
    {
        _searchTimer = 0f;
        _travelTimer = 0f;
        _arrived = false;
    }

    public override void Enter()
    {
        ResetTimers();
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

    public override void Tick()
    {
        if (_arrived)
        {
            _searchTimer += Time.deltaTime;
            return;
        }

        _travelTimer += Time.deltaTime;
        if (_travelTimer >= Enemy.SearchTravelLimit)
        {
            DebugLog.Info($"{Enemy.name} hasn't reached his search spot in {Enemy.SearchTravelLimit:0.#}s; searching where he stands.", Enemy);
            _arrived = true;
        }
    }

    // PathTo stops the enemy itself once it arrives. Unreachable (no path, already warned) counts
    // as arrived too, or the search would never start and he'd stand there for good.
    public override void FixedTick()
    {
        NavResult result = Enemy.PathTo(_target, Enemy.SearchSpeed);
        if (_arrived || result == NavResult.Moving) return;

        DebugLog.Info($"{Enemy.name} {(result == NavResult.Arrived ? "reaches" : "can't reach")} his search spot; searching for {Enemy.SearchDuration:0.#}s.", Enemy);
        _arrived = true;
    }
}
