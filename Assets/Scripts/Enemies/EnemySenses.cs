using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One enemy's senses beyond sight: what they hear, and what they notice moving, dead or
/// dropped. Kept out of EnemyController so its scans don't grow the controller further.
/// </summary>
public class EnemySenses
{
    // Shared, and grown whenever a sweep fills it (ScanMotion).
    private static Collider[] Nearby = new Collider[64];

    private readonly EnemyController _enemy;
    private readonly Dictionary<Rigidbody, float> _reactedAt = new();
    private readonly List<Rigidbody> _bodies = new();
    private readonly List<Rigidbody> _expired = new();
    private readonly HashSet<EnemyController> _noticedHeld = new();
    private readonly HashSet<EnemyController> _sawDie = new();
    private float _nextScan;

    public EnemySenses(EnemyController enemy)
    {
        _enemy = enemy;
        // A sweep every frame would be wasted: a body at notice speed moves a few centimetres in that
        // time, and the sweep is the costly part. Each enemy's first sweep is at a random offset so
        // a squad doesn't scan on the same physics step.
        _nextScan = Time.time + Random.Range(0f, enemy.ScanInterval);
    }

    /// <summary>
    /// A sound that reached them. The map decides first whether it's worth a search, and only a sound
    /// the map doesn't ignore teaches them anything: Ruby's own sounds then teach them where she is, as
    /// Heard. They go to where the sound happened, never to the thing's
    /// origin: an impact is checked where it landed, not at whoever threw it. Only a body they see moving
    /// leads back to its throw (Consider), because they can see which way it came from. A sound can't tell
    /// them that.
    /// </summary>
    public void Hear(in Stimulus stimulus)
    {
        // The row first: a sound the map ignores, or has no row for, was not heard as anything worth
        // knowing. Learning before the lookup had Ruby firing inside a busy range's mask teach every
        // hearer where she was, though the shot itself was masked as practice. TryGetRow only returns a
        // row that applies (Only While In Use is already judged there).
        if (!_enemy.Intel.TryGetRow(stimulus.Position, stimulus.Kind, true, out BehaviourMap.Row row)) return;

        PlayerController ruby = stimulus.Source != null ? stimulus.Source.GetComponentInParent<PlayerController>() : null;
        // Where Ruby was when she fired or whipped, not where the sound landed: a whip's stimulus
        // sits at its anchor (up to a chain's length from her, or on the enemy she grabbed), and
        // teaching that would send the hunt to the wrong place.
        if (ruby != null && row.Response != StimulusResponse.Ignore)
            _enemy.Memory.Learn(new RubyFact(ruby.transform.position, stimulus.Time, Knowledge.Heard));

        DebugLog.Info($"{_enemy.name} hears {stimulus.Kind} from {stimulus.Position}: {row.Response} at priority {row.Priority}.", _enemy);
        _enemy.Reactions.Investigate(new Incident(stimulus.Position, row.Response, row.Priority, mobilises: stimulus.Kind == StimulusKind.Gunshot, kind: stimulus.Kind, source: stimulus.Source));
    }

    /// <summary>
    /// Every Scan Interval: looks for enemies on the chain, for bodies moving (a fast one is FastObject, a
    /// slower one MovingObject), for corpses and for loose guns. Every one of them only in the cone out to View
    /// Range with a clear line (EnemyVision.InCone): what's behind them they don't see, though they may hear it
    /// land. The map decides what each is worth. A moving body is reacted to once per Origin Lifetime, so a
    /// settled thrown body doesn't keep them searching.
    /// </summary>
    public void Scan()
    {
        float now = Time.time;
        if (now < _nextScan) return;
        _nextScan = now + _enemy.ScanInterval;

        // No Intel was reported at Start; there is nothing to report to or read the map from.
        if (_enemy.Intel == null) return;

        ScanDeaths();
        ScanHeld();

        // Only the motion scan needs the stimulus rules (Notice and Alarm Speed, Origin Lifetime).
        // Intel reports their absence once, at enable; corpses and guns don't need them. Skipped while
        // they are too busy to act on it: nothing it finds is marked taken, so a body spun in front of them
        // in a fight was swept, judged and refused again every scan.
        if (Stimuli.Rules != null && !TooBusy) ScanMotion(now);
        ScanCorpses();
        ScanLooseGuns();
    }

    // An enemy killed in their view with a clear line: they always go to check where what killed them came from,
    // on a job together or not, as a gunshot takes anyone searching. A partner dying beside them and them only
    // searching the body read as not caring. No Aggro: they haven't seen who did it. No origin means nothing
    // shows where it came from, so they check where the enemy died. A knockdown doesn't do this: it stays
    // Consider's Search, which keeps a step between a fall and a death. One scan's window, tested at the
    // spot they died, as for a throw (ScanHeld), and once each, since the window can span two scans.
    private void ScanDeaths()
    {
        float window = _enemy.ScanInterval + Time.fixedDeltaTime;
        IReadOnlyList<EnemyController> enemies = _enemy.Intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController other = enemies[i];
            if (other == null || other == _enemy || !other.Health.IsDead || _sawDie.Contains(other)) continue;
            if (!other.BodyState.WasKilledWithin(window, out Vector3 where)) continue;
            if (!_enemy.Vision.InCone(where) || !_enemy.Vision.HasClearSight(where, other.transform)) continue;

            _sawDie.Add(other);
            bool hasOrigin = Disturbance.TryGetOrigin(other.Body, out Vector3 source);
            Vector3 point = hasOrigin ? source : where;
            if (!_enemy.Intel.TryGetRow(point, StimulusKind.Body, false, out BehaviourMap.Row row)) continue;

            DebugLog.Info($"{_enemy.name} sees {other.name} killed: they go to check {(hasOrigin ? "where it came from" : "where they died, since nothing shows where it came from")}, {point}.", _enemy);
            _enemy.Reactions.Investigate(new Incident(point, row.Response, row.Priority, makesSearch: true, saysNothingOfRuby: !hasOrigin, mobilises: true, kind: StimulusKind.Body, source: other.transform));
        }
    }

    // An enemy on the chain, in the cone with a clear line. Standing where they were grabbed, they are walked
    // over to and searched around; dragged or thrown, someone is plainly on the other end, and that is
    // the one thing short of seeing Ruby that sends them to Aggro. Its own record rather than _reactedAt,
    // or a grab they had taken would blind them to what follows for Origin Lifetime.
    private void ScanHeld()
    {
        // One more physics step than the scan's interval, so a throw between two of their scans falls
        // inside one of them. The spot it left from, not the body: at 90 m/s it is 18 m off by then. A
        // flying body they only saw after that isn't a throw they saw: that is Consider's Search at its origin.
        float throwWindow = _enemy.ScanInterval + Time.fixedDeltaTime;

        IReadOnlyList<EnemyController> enemies = _enemy.Intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController other = enemies[i];
            if (other == null || other == _enemy) continue;
            if (!other.IsHeld)
            {
                _noticedHeld.Remove(other);
                if (other.BodyState.WasThrownWithin(throwWindow, out Vector3 from)
                    && _enemy.Vision.InCone(from) && _enemy.Vision.HasClearSight(from, other.transform))
                {
                    _enemy.Reactions.SeeAttack(other, "thrown off the chain");
                    return;
                }
                continue;
            }

            Vector3 at = other.transform.position;
            if (!_enemy.Vision.InCone(at) || !_enemy.Vision.HasClearSight(other.Body.worldCenterOfMass, other.transform)) continue;

            if (other.BodyState.HeldMovedBy > _enemy.DraggedDistance)
            {
                _enemy.Reactions.SeeAttack(other, "being dragged on the chain");
                return;
            }
            // Marked only when taken: one they couldn't act on (in a fight, fetching a gun) may still send them.
            // A living one standing on the chain shows exactly where Ruby is, so it says something of her (a job
            // member goes to check it, and it counts toward their limit); a held corpse is still only a find.
            if (!TooBusy && !_noticedHeld.Contains(other) && Notice(at, StimulusKind.Body, $"{other.name} on the chain", other.transform, showsRuby: !other.Health.IsDead))
                _noticedHeld.Add(other);
        }
    }

    // In a fight or fetching a gun: Investigate would refuse whatever a search could come of.
    private bool TooBusy => _enemy.CurrentState is AggroState or RearmState;

    private void ScanMotion(float now)
    {
        float lifetime = Stimuli.Rules.OriginLifetime;
        Forget(now, lifetime);

        // Out to View Range, the cone's reach: Consider keeps only what is in the cone. Walls and floors share
        // the Default layer with the bodies, so no mask can leave them out and they fill the buffer too. A full
        // buffer may have dropped the one body that matters, so it grows and sweeps again; it only allocates
        // when it grows, and the size is kept for every enemy after.
        int count;
        while ((count = Physics.OverlapSphereNonAlloc(_enemy.transform.position, _enemy.ViewRange, Nearby, ~0, QueryTriggerInteraction.Ignore)) == Nearby.Length)
            Nearby = new Collider[Nearby.Length * 2];

        _bodies.Clear();
        for (int i = 0; i < count; i++)
        {
            Rigidbody body = Nearby[i].attachedRigidbody;
            if (body == null) continue;

            Rigidbody key = BodyOf(body);
            if (key == null || _bodies.Contains(key)) continue;
            _bodies.Add(key);
            Consider(key, now, lifetime);
        }
    }

    // Corpses come from the squad's registered enemies, not a physics sweep: a body at rest has
    // trigger colliders, and the registry is short and needs no buffer to overflow. The cheap
    // tests (dead, known) run first, so the one sight ray is only for a corpse they could notice.
    private void ScanCorpses()
    {
        IReadOnlyList<EnemyController> enemies = _enemy.Intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController enemy = enemies[i];
            if (enemy == null || enemy == _enemy || !enemy.Health.IsDead) continue;
            if (_enemy.Memory.KnowsBody(enemy)) continue;

            Vector3 at = enemy.transform.position;
            if (!_enemy.Vision.InCone(at) || !_enemy.Vision.HasClearSight(at, enemy.transform)) continue;

            // Known to the squad but not to them: they stop and look at it where they stand, and are wary
            // from then on, but doesn't go to it: someone has already been. Unknown to both: it goes
            // on the squad's books and they search where it lies. The pool is read as published, a
            // little stale, like everything else they read from it.
            bool squadKnows = _enemy.Intel.Published.KnowsBody(enemy);
            _enemy.Intel.ReportBody(enemy);
            _enemy.Memory.RememberBody(enemy);
            if (squadKnows)
            {
                Glance(at, StimulusKind.Body, $"the body of {enemy.name}, already on the squad's books");
                continue;
            }
            Notice(at, StimulusKind.Body, $"the body of {enemy.name}", enemy.transform);
        }
    }

    // Only an armed enemy goes looking for a gun: an unarmed one has no use for it here (Rearm
    // is the one that would take it). Resting guns only, since a tumbling gun is still in flight.
    private void ScanLooseGuns()
    {
        if (!_enemy.IsArmed) return;

        foreach (Gun gun in Gun.Loose)
        {
            if (gun == null || !gun.IsResting || _enemy.Memory.KnowsGun(gun)) continue;

            Vector3 at = gun.transform.position;
            // Judged first, by the room the gun lies in: a gun the map ignores (an armory's rack) must not
            // be booked, or it would count as known once Ruby whips it out into a corridor and nobody
            // would react. It also saves the view and sight checks for it.
            if (_enemy.Intel.TryGetRow(at, StimulusKind.LooseGun, false, out BehaviourMap.Row gunRow) && gunRow.Response == StimulusResponse.Ignore) continue;
            if (!_enemy.Vision.InCone(at) || !_enemy.Vision.HasClearSight(at, gun.transform)) continue;

            bool squadKnows = _enemy.Intel.Published.KnowsGun(gun);
            _enemy.Intel.ReportGun(gun);
            _enemy.Memory.RememberGun(gun);
            if (squadKnows)
            {
                DebugLog.Info($"{_enemy.name} sees {gun.name} on the floor, already on the squad's books; not searching.", _enemy);
                continue;
            }
            Notice(at, StimulusKind.LooseGun, $"the loose gun {gun.name}", gun.transform);
        }
    }

    // A look from where they stand at something the squad already knows about: it isn't worth a
    // walk, but they mustn't pass it by, and it tells them she's about. Checked as a problem at the
    // kind's own priority, so it can't pull them off anything that outranks it.
    private void Glance(Vector3 at, StimulusKind kind, string what)
    {
        if (!_enemy.Intel.TryGetRow(at, kind, false, out BehaviourMap.Row row)) return;
        // A map that says to ignore this kind (an armory's loose guns) means no glance either.
        if (row.Response == StimulusResponse.Ignore) return;

        // On a job at its place, the job goes wary for everyone, not just the one who looked.
        _enemy.Reactions.BecomeWaryAt(at, $"{_enemy.name} glances at {what}");
        DebugLog.Info($"{_enemy.name} sees {what}: they look at it from where they stand and are wary.", _enemy);
        _enemy.Reactions.Investigate(new Incident(at, StimulusResponse.SearchForProblem, row.Priority, makesSearch: true, saysNothingOfRuby: true, kind: kind), walkThere: false);
    }

    // True when they took it. `source` is the thing itself (the body, the gun), so two members noticing it are one notice.
    private bool Notice(Vector3 origin, StimulusKind kind, string what, Object source, bool showsRuby = false)
    {
        if (!_enemy.Intel.TryGetRow(origin, kind, false, out BehaviourMap.Row row)) return false;

        DebugLog.Info($"{_enemy.name} notices {what}: {row.Response} at priority {row.Priority}.", _enemy);
        // A body or a gun is found, not heard: there is nothing to check, so it is a Search, and it says
        // nothing about where Ruby is.
        bool found = kind is StimulusKind.Body or StimulusKind.LooseGun;
        return _enemy.Reactions.Investigate(new Incident(origin, row.Response, row.Priority, makesSearch: found, saysNothingOfRuby: found && !showsRuby, kind: kind, source: source));
    }

    // A character is one body for this purpose, so a bone of a tumbling enemy isn't a body of its
    // own. Ruby is never a body to react to: she's the one being hunted. Null means skip.
    private static Rigidbody BodyOf(Rigidbody body)
    {
        if (body.GetComponentInParent<PlayerController>() != null) return null;
        EnemyController character = body.GetComponentInParent<EnemyController>();
        return character != null ? character.Body : body;
    }

    private void Consider(Rigidbody body, float now, float lifetime)
    {
        if (body == _enemy.Body || body.isKinematic) return;

        // An enemy on their feet walks about; that's their own business, not a sound. Only one
        // that's held on the chain or tumbling counts.
        EnemyController enemy = body.GetComponentInParent<EnemyController>();
        if (enemy != null && !enemy.IsHeld && !IsTumbling(body)) return;

        // Only what is in front of them: a body thrown past behind them is heard when it lands, not seen.
        if (!_enemy.Vision.InCone(body.position)) return;

        // A body too slow to catch the eye still counts when it is an enemy down in front of them: one
        // knocked down or killed may hardly move. Held and slow is ScanHeld's, which walks them over.
        float speed = body.linearVelocity.magnitude;
        if (speed < Stimuli.Rules.NoticeSpeed && (enemy == null || enemy.IsHeld)) return;
        if (!_enemy.Vision.HasClearSight(body.worldCenterOfMass, body.transform)) return;
        if (_reactedAt.TryGetValue(body, out float reactedAt) && now - reactedAt < lifetime) return;

        var kind = speed >= Stimuli.Rules.AlarmSpeed ? StimulusKind.FastObject : StimulusKind.MovingObject;
        bool hasOrigin = Disturbance.TryGetOrigin(body, out Vector3 thrownFrom);
        Vector3 origin = hasOrigin ? thrownFrom : body.position;

        // The map is that of where they see it, so the rows are read at the body's own position: judged at
        // the origin, a crate thrown out of a room that ignores moving objects would be ignored in the
        // corridor too. The origin stays the point they investigate and what it teaches of Ruby.
        Vector3 judgedAt = body.position;
        // A flying pistol or enemy is judged by what it is as well as how fast it moves: speed alone
        // would call a flung pistol a mere moving object, and a thrown enemy (a body) one too. Anything
        // else (a crate) has no second row and keeps the speed row alone.
        bool isEnemy = enemy != null;
        if (isEnemy || body.TryGetComponent(out Gun _))
        {
            StimulusKind whatKind = isEnemy ? StimulusKind.Body : StimulusKind.LooseGun;
            bool haveSpeed = _enemy.Intel.TryGetRow(judgedAt, kind, false, out BehaviourMap.Row speedRow);
            bool haveWhat = _enemy.Intel.TryGetRow(judgedAt, whatKind, false, out BehaviourMap.Row whatRow);
            // A missing row was warned about once by the map. One missing: the other stands alone.
            // Both missing: nothing to react to, as a single missing row is today.
            if (!haveSpeed && !haveWhat) return;

            StimulusResponse response;
            int priority;
            StimulusKind responseFrom, priorityFrom;
            if (!haveWhat) Stronger(speedRow, speedRow, kind, kind, out response, out priority, out responseFrom, out priorityFrom);
            else if (!haveSpeed) Stronger(whatRow, whatRow, whatKind, whatKind, out response, out priority, out responseFrom, out priorityFrom);
            else Stronger(speedRow, whatRow, kind, whatKind, out response, out priority, out responseFrom, out priorityFrom);

            DebugLog.Info($"{_enemy.name} notices {kind} {body.name} at {speed:F1} m/s: it is a {whatKind}. {response} (from {responseFrom}) at priority {priority} (from {priorityFrom}).", _enemy);
            // Its origin is where Ruby threw it from, so that is where they go, and it teaches where she
            // was: unlike a body lying found, it does say something about her. An enemy thrown past them
            // needs more than one enemy's short look, so it makes a Search there. So does anything at
            // Alarm Speed. A slower flying gun stays a Check: one knocked out of a hand is far less alarming.
            // With no origin the point is only where it is, which says nothing about her.
            if (_enemy.Reactions.Investigate(new Incident(origin, response, priority, makesSearch: isEnemy || kind == StimulusKind.FastObject, saysNothingOfRuby: !hasOrigin, kind: kind, source: body.transform))) _reactedAt[body] = now;
            return;
        }

        if (!_enemy.Intel.TryGetRow(judgedAt, kind, false, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} notices {kind} {body.name} at {speed:F1} m/s: {row.Response} at priority {row.Priority}.", _enemy);
        // Something this fast could kill, and no ordinary person throws it, so it is worth more than one
        // enemy's short look: a Search, wherever it started. Slower, it's a Check.
        // Recorded only when they took it: one they ignored (mid-fight, say) may still send them later.
        if (_enemy.Reactions.Investigate(new Incident(origin, row.Response, row.Priority, makesSearch: kind == StimulusKind.FastObject, kind: kind, source: body.transform))) _reactedAt[body] = now;
    }

    // The stronger of two rows: response by severity, priority by number, each taken on its own. An
    // Ignore row contributes nothing, neither response nor priority, or an Ignore at priority 5
    // would lift a real row's priority. Both Ignore stays Ignore. Ties name the first (speed) row.
    private static void Stronger(BehaviourMap.Row first, BehaviourMap.Row second, StimulusKind firstKind, StimulusKind secondKind,
        out StimulusResponse response, out int priority, out StimulusKind responseFrom, out StimulusKind priorityFrom)
    {
        bool firstIgnored = first.Response == StimulusResponse.Ignore;
        bool secondIgnored = second.Response == StimulusResponse.Ignore;
        if (secondIgnored)
        {
            // Second adds nothing (or both ignore): the first row, whole.
            response = first.Response; priority = first.Priority; responseFrom = priorityFrom = firstKind;
            return;
        }
        if (firstIgnored)
        {
            response = second.Response; priority = second.Priority; responseFrom = priorityFrom = secondKind;
            return;
        }

        bool secondWorse = BehaviourMap.Severity(second.Response) > BehaviourMap.Severity(first.Response);
        response = secondWorse ? second.Response : first.Response;
        responseFrom = secondWorse ? secondKind : firstKind;
        bool secondHigher = second.Priority > first.Priority;
        priority = secondHigher ? second.Priority : first.Priority;
        priorityFrom = secondHigher ? secondKind : firstKind;
    }

    private static bool IsTumbling(Rigidbody body)
    {
        IRagdollBody ragdoll = body.GetComponentInParent<IRagdollBody>();
        return ragdoll != null && ragdoll.IsRagdolling;
    }

    private void Forget(float now, float lifetime)
    {
        _expired.Clear();
        foreach (KeyValuePair<Rigidbody, float> pair in _reactedAt)
            if (now - pair.Value >= lifetime) _expired.Add(pair.Key);
        foreach (Rigidbody body in _expired) _reactedAt.Remove(body);
    }
}
