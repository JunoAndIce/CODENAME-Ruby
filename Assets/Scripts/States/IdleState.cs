using UnityEngine;

/// <summary>
/// At his post. Where he started is his post: after a search, a fall or a shove he walks back to
/// it and turns the way he started, so an idle officer is never left standing wherever his last
/// job ended. Aggressive, he comes back faster. Noticing Ruby, hearing something or wanting a gun
/// is the table's business, not this state's.
/// </summary>
public class IdleState : EnemyStateBase
{
    private float _walkTimer;   // how long he has been walking home
    private bool _gaveUp;       // the walk took too long: he stays where he is until he's next at his post

    public IdleState(EnemyController enemy) : base(enemy) { }

    private bool AwayFromPost => PathGrid.FlatDistance(Enemy.transform.position, Enemy.PostPosition) > Enemy.PostTolerance;

    public override void Enter()
    {
        _walkTimer = 0f;
        _gaveUp = false;
        Enemy.Stop();
    }

    public override void Tick()
    {
        if (!AwayFromPost)
        {
            _walkTimer = 0f;
            _gaveUp = false;
            return;
        }
        if (_gaveUp) return;

        // The same limit as a search's walk: a post another officer stands on, or one behind a
        // jam, mustn't have him pushing at it for good.
        _walkTimer += Time.deltaTime;
        if (_walkTimer < Enemy.SearchTravelLimit) return;

        _gaveUp = true;
        DebugLog.Info($"{Enemy.name} hasn't got back to his post in {Enemy.SearchTravelLimit:0.#}s; he stays where he is.", Enemy);
    }

    // PathTo stops him on arrival, and warns once itself when there's no way back. A post he
    // can't reach is that warning, then standing: not a silent walk to somewhere else.
    public override void FixedTick()
    {
        if (!AwayFromPost)
        {
            Enemy.Stop();
            Enemy.TurnToYaw(Enemy.PostYaw);
            return;
        }

        if (_gaveUp || Enemy.PathTo(Enemy.PostPosition, Enemy.CurrentPatrolSpeed) != NavResult.Moving)
            Enemy.Stop();
    }
}
