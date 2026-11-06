using UnityEngine;

/// <summary>
/// How one enemy reacts to what it senses: whether a disturbance is a problem or an attack, what he
/// does with it in each state, and when the jig is up. Kept out of EnemyController so the controller
/// doesn't carry the most order-sensitive decisions in the enemy. The tuning (the Distraction and
/// Attack Near fields) and the state machine stay on the controller and are read on every call, so
/// retuning in the Inspector during Play keeps working. SetState stays there too, behind Alert,
/// FollowJob and BeginSearch.
/// </summary>
public class EnemyReactions
{
    private readonly EnemyController _enemy;

    public EnemyReactions(EnemyController enemy)
    {
        _enemy = enemy;
    }

    /// <summary>
    /// Told or heard something worth searching. Idle and patrolling enemies start searching
    /// there. One already searching moves only for a strictly higher priority: an equal one never
    /// does, so a shootout's alternating shots can't swap his target or re-flood his field, and a
    /// quiet sound can't pull him off a spot he's checking. But he **notices** it: an equal or
    /// lower one is a distraction (see Distract) rather than being ignored, which left an enemy
    /// searching on beside things flying past him. Aggro and the
    /// states that can't act on it ignore the call: he already knows where Ruby is. True when it
    /// was taken; every decision is logged. A search for an attack makes him wary for good, taken
    /// or not, so it always ends on an aggressive patrol. With walkThere false he looks from where
    /// he stands instead of going to the point (a glance at a body the squad already knows).
    /// On a job, a stimulus at the job's place goes to the job first (Intel.HandleAtOwnJob, BecomeWaryAt).
    /// </summary>
    public bool Investigate(Incident incident, bool walkThere = true)
    {
        if (incident.Reason == StimulusResponse.Ignore)
        {
            DebugLog.Info($"{_enemy.name} ignores {incident}: the map says to ignore it.", _enemy);
            return false;
        }
        if (incident.Reason == StimulusResponse.SearchForProblem && ShouldSearchForAttack(incident.Point, out string why))
        {
            DebugLog.Info($"{_enemy.name} treats the problem at {incident.Point} as an attack: {why}.", _enemy);
            incident = incident.WithReason(StimulusResponse.SearchForAttack);
        }
        if (incident.Reason == StimulusResponse.SearchForAttack) BecomeWaryAt(incident.Point, $"{_enemy.name} senses {incident}");
        if (_enemy.IsOverridden || _enemy.CurrentState is RearmState or AggroState)
        {
            DebugLog.Info($"{_enemy.name} ignores {incident} (priority {incident.Priority}): he is in {_enemy.CurrentState.GetType().Name}.", _enemy);
            return false;
        }

        if (_enemy.CurrentState is SearchingState)
        {
            // A gunshot goes to the squad whatever its priority. What it left over, a shot in the area
            // they are already searching or one right after the last, is one more thing Ruby is doing.
            if (incident.Mobilises && walkThere && _enemy.Intel != null)
                return _enemy.Intel.Mobilise(_enemy, incident) || Distract(incident);

            // On a job, anything at its place goes to the job first: a find or a flying enemy turns a check
            // into a search and a more serious thing upgrades it, for the whole team. Only what the job didn't take
            // is a distraction for him, and a glance never changes the job, so it never retargets him.
            GroupJob own = _enemy.Job;
            bool atOwnJob = own != null && _enemy.Intel != null && _enemy.Intel.Covers(own, incident.Point);
            if (atOwnJob && _enemy.Intel.HandleAtOwnJob(_enemy, new SearchRequest(incident, walkThere))) return true;
            if (atOwnJob || incident.Priority <= _enemy.Searching.Priority) return Distract(incident);
            if (own != null && !walkThere) return Distract(incident);

            // More serious than his search: the squad decides, as a job. A glance off a job (walkThere
            // false) is his own look from where he stands, and a scene with no Intel (already reported)
            // has no squad to ask, so both stay a search of his own. A null answer means a job
            // nearby already has it; that counts as taken, or the motion scan would ask again every sweep.
            if (walkThere && _enemy.Intel != null)
            {
                _enemy.Intel.Handle(_enemy, incident);
                // Still on the job he was on: the team that has it can't take him, so he stays and is distracted.
                if (own != null && _enemy.Job == own) return Distract(incident);
                return true;
            }

            _enemy.Searching.Retarget(new SearchRequest(incident, walkThere));
            DebugLog.Info($"{_enemy.name} retargets his search to {incident.Point}: {incident.Reason} at priority {incident.Priority}.", _enemy);
            return true;
        }

        if (walkThere && _enemy.Intel != null)
        {
            // Idle or patrolling: the squad decides whether this is already being handled, and who goes.
            // Left to a job nearby is as good as taken, for the reason above.
            _enemy.Intel.Handle(_enemy, incident);
            return true;
        }

        _enemy.BeginSearch(new SearchRequest(incident, walkThere));
        DebugLog.Info($"{_enemy.name} starts a search at {incident.Point}: {incident.Reason} at priority {incident.Priority}.", _enemy);
        return true;
    }

    /// <summary>
    /// He has reason to think Ruby is about, because of something at `at`. On a job, at the job's place,
    /// the job takes it and everyone on it goes wary together, so one enemy can't latch wary alone
    /// while the job says all clear. Anywhere else (no job, or a thing away from his job's place) it
    /// is his own knowledge and he latches wary himself.
    /// </summary>
    internal void BecomeWaryAt(Vector3 at, string why)
    {
        if (_enemy.Job != null && _enemy.Intel != null && _enemy.Intel.RaiseJobAt(_enemy, at, why)) return;
        BecomeWary();
    }

    /// <summary>
    /// Latches him wary. The one place that writes EnemyMemory.Wary, so there is one place to change
    /// or log it, and an enemy can't latch wary by a route nobody can find.
    /// </summary>
    public void BecomeWary() => _enemy.Memory.BecomeWary();

    /// <summary>
    /// He sees an enemy killed, or the chain drag or throw one: someone is plainly doing this, so he goes
    /// for her. Taught where she is right now, which is also the chain's source: he doesn't go on knowing
    /// where she goes after he looks away. A grab that isn't moving, or a knockdown, is searched
    /// instead (EnemySenses).
    /// </summary>
    internal void SeeAttack(EnemyController victim, string how)
    {
        Vector3 ruby = _enemy.Player.transform.position;
        _enemy.Memory.Learn(new RubyFact(ruby, Time.time, Knowledge.Heard));
        BecomeWary();
        if (_enemy.Alert())
            DebugLog.Info($"{_enemy.name} sees {victim.name} {how}: he goes for Ruby at {ruby}.", _enemy);
    }

    // Something lesser happened while he was searching. He turns to look at it and the search waits
    // a moment, but it isn't dropped or retargeted. Enough of them close together and he has had
    // enough of Ruby provoking him: she keeps throwing things and threatening them, so he goes after her.
    // So what counts is whether she is doing something, not how serious it is: a low-priority thud
    // during a body search is one more thing thrown at them. A body or gun lying there isn't her doing
    // anything, and nothing counts until he has reached the search, because what he passes on the way is
    // why he is going. He still turns to look at both.
    private bool Distract(Incident incident)
    {
        bool arrived = _enemy.Searching.ReachedSearch;
        bool counts = arrived && !incident.SaysNothingOfRuby;
        int count = _enemy.Memory.NoteDistraction(Time.time, _enemy.DistractionMemory, _enemy.DistractionGap, counts, out bool isNew, out bool counted);
        // The threshold reads the counted clock; the pause, the scout and the log read either, so a thud
        // that only counted still turns him and sends a scout.
        bool fresh = isNew || counted;
        if (counted && count >= _enemy.DistractionThreshold)
        {
            _enemy.Memory.ClearDistractions();
            GoAfterRuby(count, incident);
            return true;
        }

        _enemy.Searching.Distract(incident.Point, fresh);
        // Turning to look isn't all of it: one of the team goes to see, if the job can spare him.
        if (fresh && _enemy.Intel != null) _enemy.Intel.SendScout(_enemy, incident);
        string tally = counts ? $"{count} of {_enemy.DistractionThreshold}" : arrived ? "not counted, it isn't Ruby doing anything" : "not counted, he hasn't reached the search";
        DebugLog.Info($"{_enemy.name} {(fresh ? "is distracted by" : "glances again at")} {incident} (priority {incident.Priority}, his search is {_enemy.Searching.Priority}): {tally}.", _enemy);
        return true;
    }

    // He is going after her and not after the last thing she threw, so he is taught where she is right
    // now, once: going to where a crate landed would have him charge the crate. He doesn't go on knowing
    // where she goes after this. With no Ruby in the scene there is no one to go after.
    private void GoAfterRuby(int count, Incident last)
    {
        PlayerController ruby = _enemy.Player;
        if (ruby == null)
        {
            Debug.LogWarning($"{_enemy.name} has had enough of being provoked, but the scene has no player to go after, so he carries on searching.", _enemy);
            return;
        }

        _enemy.Memory.Learn(new RubyFact(ruby.transform.position, Time.time, Knowledge.Heard));
        DebugLog.Info($"{_enemy.name}: Enemies have been disturbed too much! ({count} disturbances in {_enemy.DistractionMemory:0.#}s, the last {last}); he goes after Ruby at {ruby.transform.position}.", _enemy);
        _enemy.Alert();
    }

    /// <summary>
    /// Whether the squad may call him onto a job at this priority: he is idle or patrolling, or he is
    /// searching alone (no job, or a job of one) at a lower priority than this. Never in a fight, down,
    /// dead or fetching a gun, and never off a team that has two or more.
    /// </summary>
    public bool CanJoinJob(int priority)
    {
        if (_enemy.Health.IsDead) return false;
        if (_enemy.CurrentState is IdleState or PatrolState) return true;
        return _enemy.CurrentState is SearchingState
            && _enemy.Searching.Priority < priority
            && (_enemy.Job == null || _enemy.Job.Count == 1);
    }

    // A disturbance is an attack, not just a problem, to someone who knows she's about: he has seen
    // her, or the newest news of her is recent and close to it. The age is the fact's own time, so
    // an old report can't turn every later thud into an attack for good.
    private bool ShouldSearchForAttack(Vector3 point, out string why)
    {
        if (_enemy.Memory.KnowsRuby)
        {
            why = _enemy.Memory.HasSeenRuby ? "he has seen Ruby" : "he has been told of Ruby";
            return true;
        }

        if (_enemy.Awareness.TryGetNewestRubyFact(out RubyFact fact))
        {
            float age = Time.time - fact.Time;
            float distance = PathGrid.FlatDistance(fact.Position, point);
            // Floors are independent: news of Ruby upstairs says nothing about a thud downstairs, whatever
            // their flat distance. A fact or point on no grid isn't near anything. That isn't warned here
            // because it can't arise unwarned: a point off every grid was warned where it came from (Hear,
            // Handle, the walk), and so was a fact (Hear and Say warn about a speaker, listener or sound
            // off a grid, and Aggro's chase after a Ruby seen off the grid warns in the navigator). The
            // floor lookups run last, after the cheap tests.
            if (age <= _enemy.AttackNearTime && distance <= _enemy.AttackNearRadius
                && PathGrid.At(fact.Position) is PathGrid factFloor && factFloor == PathGrid.At(point))
            {
                why = $"Ruby was {fact.Source} {distance:0.#} m from it, {age:0.#}s ago";
                return true;
            }
        }

        why = null;
        return false;
    }
}
