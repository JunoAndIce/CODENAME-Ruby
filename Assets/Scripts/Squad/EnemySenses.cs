using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One officer's senses beyond sight: what he hears, and what he notices moving, dead or
/// dropped. Kept out of EnemyController so its scans don't grow the controller further.
/// </summary>
public class EnemySenses
{
    // A sweep every frame would be wasted: a body at notice speed moves a few centimetres in that
    // time, and the sweep is the costly part. Each officer's first sweep is at a random offset so
    // a squad doesn't scan on the same physics step.
    private const float ScanInterval = 0.2f;
    private static readonly Collider[] Nearby = new Collider[64];

    private readonly EnemyController _enemy;
    private readonly Dictionary<Rigidbody, float> _reactedAt = new();
    private readonly List<Rigidbody> _bodies = new();
    private readonly List<Rigidbody> _expired = new();
    private float _nextScan;

    public EnemySenses(EnemyController enemy)
    {
        _enemy = enemy;
        _nextScan = Time.time + Random.Range(0f, ScanInterval);
    }

    /// <summary>
    /// A sound that reached him. Ruby's own sounds teach him where she is, as Heard. Then the map
    /// decides whether it's worth a search, and from where.
    /// </summary>
    public void Hear(in Stimulus stimulus)
    {
        bool fromRuby = stimulus.Source != null && stimulus.Source.GetComponentInParent<PlayerController>() != null;
        if (fromRuby)
            _enemy.Memory.Learn(new RubyFact(stimulus.Position, stimulus.Time, Knowledge.Heard));

        if (!_enemy.Intel.MapAt(stimulus.Origin).TryGetRow(stimulus.Kind, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} hears {stimulus.Kind} from {stimulus.Origin}: {row.Response} at priority {row.Priority}.", _enemy);
        _enemy.Investigate(stimulus.Origin, row.Response, row.Priority);
    }

    /// <summary>
    /// Looks for bodies moving within Motion Radius that he can see clearly. A fast one is
    /// FastObject, a slower one MovingObject; the map decides what each is worth. Each body is
    /// reacted to once per Origin Lifetime, so a settled thrown body doesn't keep him searching.
    /// </summary>
    public void Scan()
    {
        float now = Time.time;
        if (now < _nextScan) return;
        _nextScan = now + ScanInterval;

        if (Stimuli.Rules == null || _enemy.Intel == null) return;

        float lifetime = Stimuli.Rules.OriginLifetime;
        Forget(now, lifetime);

        int count = Physics.OverlapSphereNonAlloc(_enemy.transform.position, _enemy.MotionRadius, Nearby, ~0, QueryTriggerInteraction.Ignore);
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

        ScanCorpses();
        ScanLooseGuns();
    }

    // Corpses rest on trigger colliders, which the motion sweep above ignores, so they get their
    // own sweep. Each body is looked at once: remembering it before the next collider of the same
    // officer is reached stops the rest of his colliders from counting again.
    private void ScanCorpses()
    {
        int count = Physics.OverlapSphereNonAlloc(_enemy.transform.position, _enemy.MotionRadius, Nearby, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
        {
            EnemyController officer = Nearby[i].GetComponentInParent<EnemyController>();
            if (officer == null || officer == _enemy || officer.Health == null || !officer.Health.IsDead) continue;
            if (_enemy.Memory.KnowsBody(officer)) continue;

            Vector3 at = officer.transform.position;
            if (!_enemy.InView(at, CorpseViewRange) || !_enemy.HasClearSight(at, officer.transform)) continue;

            // Known to the squad but not to him: he learns it and doesn't go looking. Unknown to both:
            // it goes on the squad's books and he searches where it lies.
            bool squadKnows = _enemy.Intel.PoolKnowsBody(officer);
            _enemy.Intel.ReportBody(officer);
            _enemy.Memory.RememberBody(officer);
            if (squadKnows)
            {
                DebugLog.Info($"{_enemy.name} sees {officer.name} down, already on the squad's books; not searching.", _enemy);
                continue;
            }
            Notice(at, StimulusKind.Body, $"the body of {officer.name}");
        }
    }

    // Only an armed officer goes looking for a gun: an unarmed one has no use for it here (Rearm
    // is the one that would take it). Resting guns only, since a tumbling gun is still in flight.
    private void ScanLooseGuns()
    {
        if (!_enemy.IsArmed) return;

        foreach (Gun gun in Gun.Loose)
        {
            if (gun == null || !gun.IsResting || _enemy.Memory.KnowsGun(gun)) continue;

            Vector3 at = gun.transform.position;
            if (!_enemy.InView(at, _enemy.ViewRange) || !_enemy.HasClearSight(at, gun.transform)) continue;

            bool squadKnows = _enemy.Intel.PoolKnowsGun(gun);
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

    private void Notice(Vector3 origin, StimulusKind kind, string what)
    {
        if (!_enemy.Intel.MapAt(origin).TryGetRow(kind, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} notices {what}: {row.Response} at priority {row.Priority}.", _enemy);
        _enemy.Investigate(origin, row.Response, row.Priority);
    }

    // A corpse or a gun on the floor is looked at closer than Ruby is, so it's a shorter sight.
    private const float CorpseViewRange = 6f;

    // A character is one body for this purpose, so a bone of a tumbling officer isn't a body of its
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

        // An officer on his feet walks about; that's his own business, not a sound. Only one
        // that's held on the chain or tumbling counts.
        EnemyController officer = body.GetComponentInParent<EnemyController>();
        if (officer != null && !officer.IsHeld && !IsTumbling(body)) return;

        float speed = body.linearVelocity.magnitude;
        if (speed < Stimuli.Rules.NoticeSpeed) return;
        if (!_enemy.HasClearSight(body.worldCenterOfMass, body.transform)) return;
        if (_reactedAt.TryGetValue(body, out float reactedAt) && now - reactedAt < lifetime) return;
        _reactedAt[body] = now;

        var kind = speed >= Stimuli.Rules.AlarmSpeed ? StimulusKind.FastObject : StimulusKind.MovingObject;
        Vector3 origin = Disturbance.TryGetOrigin(body, out Vector3 thrownFrom) ? thrownFrom : body.position;
        if (!_enemy.Intel.MapAt(origin).TryGetRow(kind, out BehaviourMap.Row row)) return;

        DebugLog.Info($"{_enemy.name} notices {kind} {body.name} at {speed:F1} m/s: {row.Response} at priority {row.Priority}.", _enemy);
        _enemy.Investigate(origin, row.Response, row.Priority);
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
