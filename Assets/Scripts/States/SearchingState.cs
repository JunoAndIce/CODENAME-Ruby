using UnityEngine;

/// <summary>How far through a search he is: going there, looking about, and for a check that
/// found nothing, standing down.</summary>
public enum SearchStep { Walk, Look, StandDown, Done }

/// <summary>
/// Went to look for something: Ruby, after a lost fight or a gunshot, or something odd. He walks
/// to the spot, looks around it, and is done. The reason sets how long and how it ends: a search
/// for an attack looks for Search Duration; a check on a problem looks for Look Time and then
/// stands down with a gesture, because nothing came of it. Which patrol he goes back to is the
/// table's business (EnemyController.IsAggressive), not this state's.
/// </summary>
public class SearchingState : EnemyStateBase
{
    // A search from Aggro outranks every map row, so a sound can't pull him off a lost player.
    public const int FromAggroPriority = 100;

    private Vector3 _target;
    private StimulusResponse _reason;
    private int _priority;
    private SearchStep _step;
    private float _stepTimer;      // seconds in the current step. The walk there isn't part of the search: the look is.
    private float _lookBaseYaw;    // where he faced when the look began; the sweep turns about it
    private bool _walk = true;     // false: look from where he stands (a glance at something already known)
    // Set by Stage before the transition into this state, so Enter keeps the staged target.
    private bool _staged;

    public SearchingState(EnemyController enemy) : base(enemy) { }

    public override EnemyState Id => EnemyState.Searching;

    public SearchStep Step => _step;
    public bool Finished => _step == SearchStep.Done;
    public StimulusResponse Reason => _reason;
    public int Priority => _priority;

    /// <summary>
    /// Sets the target for a search about to begin from Idle or Patrol. Only staging, never a
    /// retarget: a search already running takes Retarget, which has no Enter to clear a stage, and
    /// a stage left standing would later hijack the search that follows Aggro.
    /// </summary>
    public void Stage(Vector3 point, StimulusResponse reason, int priority, bool walk)
    {
        _target = point;
        _reason = reason;
        _priority = priority;
        _walk = walk;
        _staged = true;
    }

    /// <summary>Moves a search already in progress to a new spot and reason. It starts over from the walk, so the new spot gets its full look on arrival.</summary>
    public void Retarget(Vector3 point, StimulusResponse reason, int priority, bool walk)
    {
        _target = point;
        _reason = reason;
        _priority = priority;
        _walk = walk;
        Begin();
    }

    public override void Enter()
    {
        if (_staged)
        {
            _staged = false;
        }
        else
        {
            // Entered from Aggro (or any table transition): the player's last known spot.
            _target = Enemy.LastKnownPlayerPosition;
            _reason = StimulusResponse.SearchForAttack;
            _priority = FromAggroPriority;
            _walk = true;
        }
        Begin();
    }

    public override void Exit() => _staged = false;

    private void Begin()
    {
        _stepTimer = 0f;
        if (_walk)
        {
            _step = SearchStep.Walk;
            return;
        }

        Enemy.FaceToward(_target);
        BeginLook($"{Enemy.name} looks from where he stands");
    }

    private void BeginLook(string why)
    {
        _step = SearchStep.Look;
        _stepTimer = 0f;
        _lookBaseYaw = Enemy.transform.eulerAngles.y;
        DebugLog.Info($"{why}: looking around for {LookDuration:0.#}s ({_reason}).", Enemy);
    }

    // An attack is searched for the whole Search Duration, a problem only glanced into.
    private float LookDuration => _reason == StimulusResponse.SearchForAttack ? Enemy.SearchDuration : Enemy.LookTime;

    public override void Tick()
    {
        switch (_step)
        {
            case SearchStep.Walk:
                _stepTimer += Time.deltaTime;
                // Keeps one he can't reach (blocked, a crowded doorway) from walking for good.
                if (_stepTimer >= Enemy.SearchTravelLimit)
                    BeginLook($"{Enemy.name} hasn't reached his search spot in {Enemy.SearchTravelLimit:0.#}s; he searches where he stands");
                break;

            case SearchStep.Look:
                _stepTimer += Time.deltaTime;
                if (_stepTimer < LookDuration) break;
                if (_reason == StimulusResponse.SearchForAttack) Finish("finishes searching");
                else BeginStandDown();
                break;

            case SearchStep.StandDown:
                _stepTimer += Time.deltaTime;
                if (_stepTimer >= Enemy.StandDownTime) Finish("stands down");
                break;
        }
    }

    // The gesture is a pause and a line until there is an animation for it.
    private void BeginStandDown()
    {
        _step = SearchStep.StandDown;
        _stepTimer = 0f;
        DebugLog.Info($"{Enemy.name} finds nothing and gestures: all clear.", Enemy);
    }

    private void Finish(string what)
    {
        _step = SearchStep.Done;
        DebugLog.Info($"{Enemy.name} {what}.", Enemy);
    }

    // An area move, not a point: officers of equal weight block each other (the push rule), so two
    // sent to one spot used to leave the second pushing at the first until the travel limit. The
    // navigator arrives once progress stalls inside the area, which is how he ends up beside the
    // first officer and looks from there. No path at all is a setup problem and warns; a stall
    // outside the area is only crowding, and is logged.
    public override void FixedTick()
    {
        switch (_step)
        {
            case SearchStep.Walk:
                NavResult result = Enemy.WalkTo(_target, Enemy.SearchReach, Enemy.SearchSpeed);
                if (result == NavResult.Moving) return;
                if (result == NavResult.Arrived)
                {
                    BeginLook($"{Enemy.name} reaches his search spot");
                    break;
                }
                if (result == NavResult.Unreachable)
                    Debug.LogWarning($"{Enemy.name} can't reach his search spot ({Enemy.Navigator.FailureReason}); he searches where he stands.", Enemy);
                BeginLook($"{Enemy.name} {(result == NavResult.Blocked ? "is blocked short of his search spot" : "can't reach his search spot")}");
                break;

            case SearchStep.Look:
                Enemy.LookAround(_lookBaseYaw, _stepTimer, LookDuration);
                break;

            default:
                Enemy.Stop();
                break;
        }
    }
}
