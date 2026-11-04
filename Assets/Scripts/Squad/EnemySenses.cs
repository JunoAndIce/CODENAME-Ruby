using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One enemy's senses beyond sight: what he hears, and what he notices moving, dead or
/// dropped. Kept out of EnemyController so its scans don't grow the controller further.
/// </summary>
public class EnemySenses
{
    private static readonly Collider[] Nearby = new Collider[64];

    private readonly EnemyController _enemy;
    private readonly Dictionary<Rigidbody, float> _reactedAt = new();
    private readonly List<Rigidbody> _bodies = new();
    private readonly List<Rigidbody> _expired = new();
    private float _nextScan;
    private bool _warnedFullBuffer;

    public EnemySenses(EnemyController enemy)
    {
        _enemy = enemy;
        // A sweep every frame would be wasted: a body at notice speed moves a few centimetres in that
        // time, and the sweep is the costly part. Each enemy's first sweep is at a random offset so
        // a squad doesn't scan on the same physics step.
        _nextScan = Time.time + Random.Range(0f, enemy.ScanInterval);
    }

    /// <summary>
    /// A sound that reached him. Ruby's own sounds teach him where she is, as Heard. Then the map
    /// decides whether it's worth a search, and from where.
    /// </summary>
    public void Hear(in Stimulus stimulus)
    {
        PlayerController ruby = stimulus.Source != null ? stimulus.Source.GetComponentInParent<PlayerController>() : null;
        // Where Ruby was when she fired or whipped, not where the sound landed: a whip's stimulus
        // sits at its anchor (up to a chain's length from her, or on the enemy she grabbed), and
        // teaching that would send the hunt to the wrong place.
        if (ruby != null)
            _enemy.Memory.Learn(new RubyFact(ruby.transform.position, stimulus.Time, Knowledge.Heard));

        if (!_enemy.Intel.TryGetRow(stimulus.Origin, stimulus.Kind, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} hears {stimulus.Kind} from {stimulus.Origin}: {row.Response} at priority {row.Priority}.", _enemy);
        _enemy.Investigate(new Incident(stimulus.Origin, row.Response, row.Priority));
    }

    /// <summary>
    /// Every Scan Interval: looks for bodies moving within Motion Radius that he can see clearly
    /// (a fast one is FastObject, a slower one MovingObject), for corpses and for loose guns. The
    /// map decides what each is worth. A moving body is reacted to once per Origin Lifetime, so a
    /// settled thrown body doesn't keep him searching.
    /// </summary>
    public void Scan()
    {
        float now = Time.time;
        if (now < _nextScan) return;
        _nextScan = now + _enemy.ScanInterval;

        // No Intel was reported at Start; there is nothing to report to or read the map from.
        if (_enemy.Intel == null) return;

        // Only the motion scan needs the stimulus rules (Notice and Alarm Speed, Origin Lifetime).
        // Intel reports their absence once, at enable; corpses and guns don't need them.
        if (Stimuli.Rules != null) ScanMotion(now);
        ScanCorpses();
        ScanLooseGuns();
    }

    private void ScanMotion(float now)
    {
        float lifetime = Stimuli.Rules.OriginLifetime;
        Forget(now, lifetime);

        int count = Physics.OverlapSphereNonAlloc(_enemy.transform.position, _enemy.MotionRadius, Nearby, ~0, QueryTriggerInteraction.Ignore);
        if (count == Nearby.Length && !_warnedFullBuffer)
        {
            _warnedFullBuffer = true;
            Debug.LogWarning($"{_enemy.name}: {Nearby.Length} colliders are within Motion Radius, which fills his sense buffer, so some nearby bodies weren't considered. Reduce Motion Radius or the clutter.", _enemy);
        }

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
    // tests (dead, known) run first, so the one sight ray is only for a corpse he could notice.
    private void ScanCorpses()
    {
        IReadOnlyList<EnemyController> enemies = _enemy.Intel.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyController enemy = enemies[i];
            if (enemy == null || enemy == _enemy || !enemy.Health.IsDead) continue;
            if (_enemy.Memory.KnowsBody(enemy)) continue;

            Vector3 at = enemy.transform.position;
            if (!_enemy.InView(at, _enemy.BodyNoticeRange) || !_enemy.HasClearSight(at, enemy.transform)) continue;

            // Known to the squad but not to him: he stops and looks at it where he stands, and is wary
            // from then on, but doesn't go to it: someone has already been. Unknown to both: it goes
            // on the squad's books and he searches where it lies. The pool is read as published, a
            // little stale, like everything else he reads from it.
            bool squadKnows = _enemy.Intel.Published.KnowsBody(enemy);
            _enemy.Intel.ReportBody(enemy);
            _enemy.Memory.RememberBody(enemy);
            if (squadKnows)
            {
                Glance(at, StimulusKind.Body, $"the body of {enemy.name}, already on the squad's books");
                continue;
            }
            Notice(at, StimulusKind.Body, $"the body of {enemy.name}");
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
            if (!_enemy.InView(at, _enemy.ViewRange) || !_enemy.HasClearSight(at, gun.transform)) continue;

            bool squadKnows = _enemy.Intel.Published.KnowsGun(gun);
            _enemy.Intel.ReportGun(gun);
            _enemy.Memory.RememberGun(gun);
            if (squadKnows)
            {
                DebugLog.Info($"{_enemy.name} sees {gun.name} on the floor, already on the squad's books; not searching.", _enemy);
                continue;
            }
            Notice(at, StimulusKind.LooseGun, $"the loose gun {gun.name}");
        }
    }

    // A look from where he stands at something the squad already knows about: it isn't worth a
    // walk, but he mustn't pass it by, and it tells him she's about. Checked as a problem at the
    // kind's own priority, so it can't pull him off anything that outranks it.
    private void Glance(Vector3 at, StimulusKind kind, string what)
    {
        if (!_enemy.Intel.TryGetRow(at, kind, out BehaviourMap.Row row)) return;
        // A map that says to ignore this kind (an armory, a morgue) means no glance either.
        if (row.Response == StimulusResponse.Ignore) return;

        // On a job at its place, the job goes wary for everyone, not just the one who looked.
        _enemy.BecomeWary(at, $"{_enemy.name} glances at {what}");
        DebugLog.Info($"{_enemy.name} sees {what}: he looks at it from where he stands and is wary.", _enemy);
        _enemy.Investigate(new Incident(at, StimulusResponse.SearchForProblem, row.Priority, isFind: true), walkThere: false);
    }

    private void Notice(Vector3 origin, StimulusKind kind, string what)
    {
        if (!_enemy.Intel.TryGetRow(origin, kind, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} notices {what}: {row.Response} at priority {row.Priority}.", _enemy);
        // A body or a gun is found, not heard: it says nothing about where Ruby is.
        _enemy.Investigate(new Incident(origin, row.Response, row.Priority, isFind: kind is StimulusKind.Body or StimulusKind.LooseGun));
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

        // An enemy on his feet walks about; that's his own business, not a sound. Only one
        // that's held on the chain or tumbling counts.
        EnemyController enemy = body.GetComponentInParent<EnemyController>();
        if (enemy != null && !enemy.IsHeld && !IsTumbling(body)) return;

        float speed = body.linearVelocity.magnitude;
        if (speed < Stimuli.Rules.NoticeSpeed) return;
        if (!_enemy.HasClearSight(body.worldCenterOfMass, body.transform)) return;
        if (_reactedAt.TryGetValue(body, out float reactedAt) && now - reactedAt < lifetime) return;

        var kind = speed >= Stimuli.Rules.AlarmSpeed ? StimulusKind.FastObject : StimulusKind.MovingObject;
        Vector3 origin = Disturbance.TryGetOrigin(body, out Vector3 thrownFrom) ? thrownFrom : body.position;

        // A flying pistol or enemy is judged by what it is as well as how fast it moves: speed alone
        // would call a flung pistol a mere moving object, and a thrown enemy (a body) one too. Anything
        // else (a crate) has no second row and keeps the speed row alone.
        bool isEnemy = enemy != null;
        if (isEnemy || body.TryGetComponent(out Gun _))
        {
            StimulusKind whatKind = isEnemy ? StimulusKind.Body : StimulusKind.LooseGun;
            bool haveSpeed = _enemy.Intel.TryGetRow(origin, kind, out BehaviourMap.Row speedRow);
            bool haveWhat = _enemy.Intel.TryGetRow(origin, whatKind, out BehaviourMap.Row whatRow);
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
            // Not a find, though it is a body or a gun: its origin is where Ruby threw it from, so
            // the search goes there, as a check. A find would send him to where it landed.
            if (_enemy.Investigate(new Incident(origin, response, priority))) _reactedAt[body] = now;
            return;
        }

        if (!_enemy.Intel.TryGetRow(origin, kind, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} notices {kind} {body.name} at {speed:F1} m/s: {row.Response} at priority {row.Priority}.", _enemy);
        // Recorded only when he took it: one he ignored (mid-fight, say) may still send him later.
        if (_enemy.Investigate(new Incident(origin, row.Response, row.Priority))) _reactedAt[body] = now;
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
