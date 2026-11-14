using UnityEngine;

/// <summary>
/// How one enemy reacts to what it senses: whether a disturbance is a problem or an attack, what they
/// do with it in each state, and when the jig is up. Kept out of EnemyController so the controller
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
    /// does, so a shootout's alternating shots can't swap their target or re-flood their field, and a
    /// quiet sound can't pull them off a spot they're checking. But they **notices** it: an equal or
    /// lower one is a distraction (see Distract) rather than being ignored, which left an enemy
    /// searching on beside things flying past them. Aggro and the
    /// states that can't act on it ignore the call: they already know where Ruby is. True when it
    /// was taken; every decision is logged. A search for an attack makes them wary for good, taken
    /// or not, so it always ends on an aggressive patrol. With walkThere false they look from where
    /// they stand instead of going to the point (a glance at a body the squad already knows).
    /// On a job, a serious thing brings the whole job (Intel.Mobilise). On a running Search anything else is
    /// ordinary whatever its priority: one goes to check it, the search grows there, and it counts toward their
    /// own limit (Distract). On a Check a stimulus at the job's place goes to the job first (Intel.HandleAtOwnJob,
    /// BecomeWaryAt), and one elsewhere that outranks their search goes to the squad (Intel.Handle).
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
            DebugLog.Info($"{_enemy.name} ignores {incident} (priority {incident.Priority}): they are in {_enemy.CurrentState.GetType().Name}.", _enemy);
            return false;
        }

        if (_enemy.CurrentState is SearchingState)
        {
            // A thing another open job covers (its place, or its search area) is that team's: they turn toward it and
            // it counts for them, but their job doesn't grow there and nobody is sent, whichever way it is handled below
            // (growing into their ground would have the next Intel Interval merge the two jobs).
            GroupJob own = _enemy.Job;
            bool othersHaveIt = own != null && _enemy.Intel != null && _enemy.Intel.AnotherJobHas(own, incident.Point);

            // A serious thing (a gunshot, a killing, a body nobody had booked, an enemy seen flying) brings their
            // whole job, whatever its priority, before anything else: priority let a gunshot pass as a distraction,
            // and a team about to finish walked off from it. With no running job only a gunshot or a killing goes
            // to the squad, as before; anything else serious is handled below as it always was. What it leaves
            // over (right after the last move, or off the job's floor) only turns them. Every enemy who notices it
            // counts it toward their own limit first, taken by the job or not (user, 2026-11-11): the count is their,
            // and a team that goes shares nothing. One who has had enough goes after Ruby and the job doesn't
            // handle them as a member. Counted once, here: a second NoteDistraction in the frame would read as a
            // repeat and they wouldn't turn.
            bool onRunningJob = own != null && own.Step != JobStep.Done;
            if (walkThere && _enemy.Intel != null && (incident.Mobilises || (onRunningJob && _enemy.Intel.Serious(incident))))
            {
                bool fresh = CountDistraction(incident, out int count, out bool hadEnough);
                if (hadEnough && _enemy.CurrentState is not SearchingState) return true;
                if (_enemy.Intel.Mobilise(_enemy, incident)) return true;
                _enemy.Searching.Distract(incident.Point, fresh);
                DebugLog.Info($"{_enemy.name} {(fresh ? "is distracted by" : "glances again at")} {incident} (serious, but {(_enemy.Job != null ? _enemy.Job.ToString() : "the squad")} doesn't go there now): {(incident.SaysNothingOfRed ? "not counted, it isn't Ruby doing anything" : $"{count} of {_enemy.DistractionThreshold}")}.", _enemy);
                return true;
            }

            // On a running Search anything else is ordinary at any priority (user, 2026-11-11): they, or whoever of the
            // team noticed it first, goes to check it, the search grows there and it counts toward their own limit
            // (Distract). Outranking their search used to upgrade or move the job (HandleAtOwnJob) or take them off it to
            // lead a new one (Handle), and nobody counted it. A glance comes here too, and only turns them, as before.
            // Whatever one member knows the team knows, so an attack-reason thing raises the job's reason where it
            // now reaches (its place, or the patch it grew there): cheap, and it neither moves nor restarts the job.
            // Its priority doesn't rise, since an ordinary thing doesn't change what the team is searching for.
            if (onRunningJob && own.Kind == JobKind.Search && _enemy.Intel != null)
            {
                bool taken = Distract(incident, othersHaveIt);
                if (incident.Reason == StimulusResponse.SearchForAttack && _enemy.Job == own)
                    _enemy.Intel.RaiseJobAt(_enemy, incident.Point, $"{_enemy.name} noticed {incident}");
                return taken;
            }

            // On a Check, anything at its place goes to the job first: a find or a flying enemy turns the check
            // into a search and a more serious thing upgrades it, for the whole team. Only what the job didn't take
            // is a distraction for them, and a glance never changes the job, so it never retargets them.
            bool atOwnJob = own != null && _enemy.Intel != null && _enemy.Intel.Covers(own, incident.Point);
            if (atOwnJob && _enemy.Intel.HandleAtOwnJob(_enemy, new SearchRequest(incident, walkThere))) return true;
            if (atOwnJob || incident.Priority <= _enemy.Searching.Priority) return Distract(incident, othersHaveIt);
            if (own != null && !walkThere) return Distract(incident, othersHaveIt);

            // More serious than their search: the squad decides, as a job. A glance off a job (walkThere
            // false) is their own look from where they stand, and a scene with no Intel (already reported)
            // has no squad to ask, so both stay a search of their own. A null answer means a job
            // nearby already has it; that counts as taken, or the motion scan would ask again every sweep.
            if (walkThere && _enemy.Intel != null)
            {
                _enemy.Intel.Handle(_enemy, incident);
                // Still on the job they were on: the team that has it can't take them, so they stay and are distracted.
                // Only turned: another job has it, and checking it would walk them onto that team's ground and
                // grow their own search into theirs.
                if (own != null && _enemy.Job == own) return Distract(incident, othersHaveIt: true);
                return true;
            }

            _enemy.Searching.Retarget(new SearchRequest(incident, walkThere));
            DebugLog.Info($"{_enemy.name} retargets their search to {incident.Point}: {incident.Reason} at priority {incident.Priority}.", _enemy);
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
    /// They have reason to think Ruby is about, because of something at `at`. On a job, at the job's place,
    /// the job takes it and everyone on it goes wary together, so one enemy can't latch wary alone
    /// while the job says all clear. Anywhere else (no job, or a thing away from their job's place) it
    /// is their own knowledge and they latch wary themselves.
    /// </summary>
    internal void BecomeWaryAt(Vector3 at, string why)
    {
        if (_enemy.Job != null && _enemy.Intel != null && _enemy.Intel.RaiseJobAt(_enemy, at, why)) return;
        BecomeWary();
    }

    /// <summary>
    /// Latches them wary. The one place that writes EnemyMemory.Wary, so there is one place to change
    /// or log it, and an enemy can't latch wary by a route nobody can find.
    /// </summary>
    public void BecomeWary() => _enemy.Memory.BecomeWary();

    /// <summary>
    /// They see the chain drag or throw an enemy: someone is plainly on the other end of it, so they go
    /// for her. Taught where she is right now, which is also the chain's source: they don't go on knowing
    /// where she goes after they look away. A grab that isn't moving, a knockdown or a death is searched
    /// instead (EnemySenses).
    /// </summary>
    internal void SeeAttack(EnemyController victim, string how)
    {
        Vector3 ruby = _enemy.Player.transform.position;
        _enemy.Memory.Learn(new RubyFact(ruby, Time.time, Knowledge.Heard));
        BecomeWary();
        if (_enemy.Alert())
            DebugLog.Info($"{_enemy.name} sees {victim.name} {how}: they go for Ruby at {ruby}.", _enemy);
    }

    // Something lesser happened while they were searching. It isn't dropped or retargeted, but it is noticed.
    // Enough of them close together and they have had enough of Ruby provoking them: she keeps throwing things
    // and threatening them, so they go after her. So what counts is whether she is doing something, not how
    // serious it is: a low-priority thud during a body search is one more thing thrown at them, on the walk
    // there as much as once they've arrived. A body or gun lying there isn't her doing anything: they turn to
    // look, and it doesn't count. The count is their own, always, before anything else: what the job then does
    // with it shares nothing (spec: "Counts stay personal").
    //
    // **On a job** a new one goes to the job (JobBoard.Noticed): it grows the search there, and they, or
    // whoever of the team noticed the same thing first, walks over to check it and comes back (spec:
    // "Distractions on a search"). One who doesn't go turns toward it and the search waits a moment, as
    // before. `othersHaveIt` is a thing another job has: they only turn. **Off a job** (a glance, or no
    // Intel) they only turn, as before (spec: "Off a job").
    private bool Distract(Incident incident, bool othersHaveIt = false)
    {
        bool fresh = CountDistraction(incident, out int count, out bool hadEnough);
        if (hadEnough) return true;

        bool counts = !incident.SaysNothingOfRed;

        // Only a fresh one: the same tumbling body thudding again within Distraction Gap is one thing, so it
        // can't send them twice or grow the search per thud.
        bool sent = fresh && !othersHaveIt && _enemy.Job != null && _enemy.Intel != null && _enemy.Intel.Noticed(_enemy, incident);
        // The one who goes sets off at once: a pause first would have them stand facing it for Distraction Time.
        if (!sent) _enemy.Searching.Distract(incident.Point, fresh);
        string tally = counts ? $"{count} of {_enemy.DistractionThreshold}" : "not counted, it isn't Ruby doing anything";
        DebugLog.Info($"{_enemy.name} {(fresh ? "is distracted by" : "glances again at")} {incident} (priority {incident.Priority}, their search is {_enemy.Searching.Priority}){(sent ? " and goes to check it" : "")}: {tally}.", _enemy);
        return true;
    }

    // Their own count of a distraction, split out of Distract so a serious thing is counted by everyone who notices
    // it, before the job handles it. Returns whether it is fresh (the pause, the job and the log read that:
    // either clock). `hadEnough` is the threshold: the count has cleared and GoAfterRed has run.
    private bool CountDistraction(Incident incident, out int count, out bool hadEnough)
    {
        bool counts = !incident.SaysNothingOfRed;
        count = _enemy.Memory.NoteDistraction(Time.time, _enemy.DistractionMemory, _enemy.DistractionGap, counts, out bool isNew, out bool counted);
        // The threshold reads the counted clock; the pause, the job and the log read either, so a thud that
        // only counted still turns them and goes to the job.
        hadEnough = counted && count >= _enemy.DistractionThreshold;
        if (hadEnough)
        {
            GroupJob job = _enemy.Job;
            _enemy.Memory.ClearDistractions();
            GoAfterRed(count, incident);
            // Only the provoked one reacts (user, 2026-11-11): what tipped them over, serious or not, never reaches their
            // job, so a thing only they noticed brings nobody else and grows nothing. Said, so it isn't read as dropped.
            if (job != null && _enemy.Job != job)
                DebugLog.Info($"{job} isn't told of {incident}: {_enemy.name} has gone after Ruby over it, and the team carries on without them.", _enemy);
        }
        return isNew || counted;
    }

    // They are going after her and not after the last thing she threw, so they are taught where she is right
    // now, once: going to where a crate landed would have them charge the crate. They don't go on knowing
    // where she goes after this. With no Ruby in the scene there is no one to go after.
    //
    // They see her only with their own eyes until she is in their view (EnemyController.ProvokedUnseen; spec: "At
    // their own limit they run at Ruby, and see her only with their own eyes"), so a provoked enemy who hasn't
    // looked at her can be slipped past from behind and can't spread her position on hearsay. Set before
    // Alert, so AggroState.Enter already reads them as blind. Only here: word of mouth, a hit, getting up and
    // seeing the chain drag someone all keep Aggro's all-round sight.
    private void GoAfterRed(int count, Incident last)
    {
        PlayerController ruby = _enemy.Player;
        if (ruby == null)
        {
            Debug.LogWarning($"{_enemy.name} has had enough of being provoked, but the scene has no player to go after, so they carry on searching.", _enemy);
            return;
        }

        _enemy.Memory.Learn(new RubyFact(ruby.transform.position, Time.time, Knowledge.Heard));
        DebugLog.Info($"{_enemy.name}: Enemies have been disturbed too much! ({count} disturbances in {_enemy.DistractionMemory:0.#}s, the last {last}); they go after Ruby at {ruby.transform.position}, and sees her only once she is in their view.", _enemy);
        _enemy.ProvokedUnseen = true;
        if (_enemy.Alert()) return;

        // Investigate only counts in Searching, so Alert can't refuse them; if a later route ever calls this
        // where it can, the flag mustn't wait on a state change that isn't coming.
        _enemy.ProvokedUnseen = false;
        Debug.LogWarning($"{_enemy.name} has had enough of being provoked, but can't go after Ruby from {_enemy.CurrentState?.GetType().Name}: they stay where they are.", _enemy);
    }

    /// <summary>
    /// Whether the squad may call them onto a job at this priority: they are idle or patrolling, or they are
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

    // A disturbance is an attack, not just a problem, to someone who knows she's about: they have seen
    // her, or the newest news of her is recent and close to it. The age is the fact's own time, so
    // an old report can't turn every later thud into an attack for good.
    private bool ShouldSearchForAttack(Vector3 point, out string why)
    {
        if (_enemy.Memory.KnowsRed)
        {
            why = _enemy.Memory.HasSeenRed ? "they have seen Ruby" : "they have been told of Ruby";
            return true;
        }

        if (_enemy.Awareness.TryGetNewestRedFact(out RubyFact fact))
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
