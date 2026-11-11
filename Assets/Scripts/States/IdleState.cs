using UnityEngine;

/// <summary>
/// At his post. Where he started is his post: after a search, a fall or a shove he walks back to
/// it and turns the way he started, so an idle enemy is never left standing wherever his last
/// job ended. Aggressive, he comes back at the aggressive patrol speed. Noticing Ruby, hearing something or wanting a gun
/// is the table's business, not this state's.
/// </summary>
public class IdleState : EnemyStateBase
{
    private float _walkTimer;   // how long he has been walking home
    private bool _gaveUp;       // the walk took too long: he stays where he is until he's next at his post

    private float _nextShot;            // Time.time of the next practice shot
    private bool _noGunNoted;           // the one log line for an unarmed enemy at a range post
    private bool _outOfRangeWarned;
    private bool _noTargetWarned;
    private bool _facingTarget;         // turned to the target from where he stands: the first shot waits for it

    /// <summary>At a range post, armed, shooting at the spot's target. Practising is something he does here, not a state.</summary>
    public bool Practising { get; private set; }

    public IdleState(EnemyController enemy) : base(enemy) { }

    private bool AwayFromPost => PathGrid.FlatDistance(Enemy.transform.position, Enemy.PostPosition) > Enemy.PostTolerance;

    public override void Enter()
    {
        _walkTimer = 0f;
        _gaveUp = false;
        _nextShot = 0f;
        Enemy.Stop();
    }

    public override void Exit() => EndPractice();

    private void EndPractice()
    {
        if (!Practising) return;
        Practising = false;
        _facingTarget = false;
        Enemy.Arms.Lower();
    }

    // Only a range post pays anything here: an enemy with no post spot returns on the first line.
    private void Practise()
    {
        RangeSpot spot = Enemy.PostSpot;
        if (spot == null) return;

        if (!Enemy.IsArmed)
        {
            if (!_noGunNoted)
            {
                _noGunNoted = true;
                DebugLog.Info($"{Enemy.name} is posted at a firing range with no gun, so he stands at his post and doesn't practise.", Enemy);
            }
            EndPractice();
            return;
        }
        _noGunNoted = false;

        if (spot.Target == null)
        {
            if (!_noTargetWarned)
            {
                _noTargetWarned = true;
                Debug.LogWarning($"{Enemy.name}: his range spot '{spot.name}' has no target, so he faces its forward and doesn't practise.", Enemy);
            }
            EndPractice();
            return;
        }

        Vector3 target = spot.Target.position;
        if (!Enemy.Arms.InRange(target))
        {
            if (!_outOfRangeWarned)
            {
                _outOfRangeWarned = true;
                Debug.LogWarning($"{Enemy.name}: the target of range spot '{spot.name}' is out of his gun's range, so he doesn't fire.", Enemy);
            }
            EndPractice();
            return;
        }
        _outOfRangeWarned = false;

        if (!Practising) _nextShot = Time.time + Random.Range(0.3f, 0.8f); // a beat to square up before the first shot
        Practising = true;
        Enemy.Arms.AimAt(target);
        if (!_facingTarget || Time.time < _nextShot) return;
        _nextShot = Time.time + Random.Range(Enemy.PracticeIntervalMin, Enemy.PracticeIntervalMax);

        // Noted first, so this shot's own Gunshot is already masked by the room's map.
        Room room = spot.Room;
        if (room != null) room.NotePractice();
        Enemy.Arms.TryFireAt(target);
    }

    public override void Tick()
    {
        if (!AwayFromPost)
        {
            Practise();
            _walkTimer = 0f;
            _gaveUp = false;
            return;
        }
        EndPractice();
        if (_gaveUp) return;

        // The same limit as a search's walk: a post another enemy stands on, or one behind a
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
            // Practising he faces the target from where he really stands (up to Post Tolerance off the
            // spot, and the target may move), not the yaw recorded from the spot at Start.
            if (Practising && Enemy.PostSpot != null && Enemy.PostSpot.Target != null)
            {
                Vector3 to = Enemy.PostSpot.Target.position - Enemy.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f)
                {
                    _facingTarget = Enemy.Movement.TurnToYaw(Quaternion.LookRotation(to).eulerAngles.y);
                    return;
                }
            }
            _facingTarget = false;
            Enemy.Movement.TurnToYaw(Enemy.PostYaw);
            return;
        }

        if (_gaveUp || Enemy.Movement.PathTo(Enemy.PostPosition, Enemy.CurrentPatrolSpeed) != NavResult.Moving)
            Enemy.Stop();
    }
}
