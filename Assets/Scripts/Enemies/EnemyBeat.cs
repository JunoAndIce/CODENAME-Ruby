using UnityEngine;

/// <summary>
/// The beat an officer rolls through between states, so they never snap from one to the next (spec
/// 2026-11-20-smooth-transitions): the draw on first sight of Ruby, a notice before checking something, a hold
/// on her last spot before searching, and the stand-down at a search's end. One at a time. The state that
/// starts a beat waits while it runs; this owns the timer, the turn toward what caused it and the feet.
///
/// EnemyController cancels it on every change of state (StateMachine.Changed), so a beat never outlives the
/// state that started it, and the interrupts the user chose (overrides and Ruby) come from the transition
/// table with no rule of their own here. The lengths are tuned on the controller and passed in by the state.
/// </summary>
public class EnemyBeat
{
    private readonly EnemyController _enemy;
    private float _duration;
    private float _elapsed;
    private Vector3 _startVelocity;

    public EnemyBeat(EnemyController enemy)
    {
        _enemy = enemy;
    }

    public bool Running { get; private set; }

    /// <summary>The running beat, None when there is none.</summary>
    public AnimBeat Kind { get; private set; }

    /// <summary>How far through the beat they are, 0 to 1: what the animation scrubs its clip by.</summary>
    public float Progress => !Running ? 0f : _duration <= 0f ? 1f : Mathf.Clamp01(_elapsed / _duration);

    /// <summary>What they turn toward while it runs.</summary>
    public Vector3 FacePoint { get; private set; }

    /// <summary>
    /// Starts a beat, replacing any running one. A duration of 0 or less is allowed (Draw Time 0 means no
    /// reaction window): it ends on the next step, having turned them once.
    /// </summary>
    public void Start(AnimBeat kind, Vector3 face, float duration)
    {
        if (kind == AnimBeat.None)
        {
            Debug.LogError($"{_enemy.name} was asked to start a beat of kind None: nothing starts, and whoever asked has a bug.", _enemy);
            return;
        }
        if (Running) EndRunning();

        Kind = kind;
        FacePoint = face;
        _duration = duration;
        _elapsed = 0f;
        _startVelocity = VelocityUtil.Flatten(_enemy.Body.linearVelocity);
        Running = true;
        DebugLog.Info($"{_enemy.name} begins the {kind} beat for {Mathf.Max(0f, duration):0.##}s.", _enemy);
    }

    /// <summary>Moves what they turn toward: Ruby moving during the draw, a disturbance merged into a notice.</summary>
    public void Face(Vector3 point) => FacePoint = point;

    /// <summary>Ends the running beat at once. Returns the kind it cut short, None when nothing was running.</summary>
    public AnimBeat Cancel()
    {
        if (!Running) return AnimBeat.None;
        AnimBeat cut = Kind;
        DebugLog.Info($"{_enemy.name}'s {cut} beat is cut short.", _enemy);
        EndRunning();
        return cut;
    }

    public void FixedTick()
    {
        if (!Running) return;

        _enemy.Movement.TurnToward(FacePoint);
        if (Kind == AnimBeat.Draw)
        {
            // The draw keeps the momentum they had and runs it down inside the beat, so a walking officer
            // doesn't freeze mid-stride. Every other beat plants them (user, 2026-11-20).
            _enemy.Movement.Coast(_startVelocity * (1f - Progress));
        }
        else
        {
            _enemy.Stop();
        }

        _elapsed += Time.fixedDeltaTime;
        if (_elapsed < _duration) return;
        DebugLog.Info($"{_enemy.name} ends the {Kind} beat.", _enemy);
        EndRunning();
    }

    private void EndRunning()
    {
        Running = false;
        Kind = AnimBeat.None;
        _elapsed = 0f;
        _duration = 0f;
    }
}
