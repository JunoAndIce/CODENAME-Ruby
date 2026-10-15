using UnityEngine;

/// <summary>
/// A pooled projectile. Each physics step it sweeps a thin sphere from where it is to where it
/// will be, so a fast bullet can't skip through a thin wall. It stops at the nearest thing that
/// stops bullets (IBulletTarget, or anything solid without it), damages the Health there, and
/// knocks down what that kills. It never pushes anything: a bullet's rule is fixed damage, not the
/// impact-damage formula, under which a light, 30 m/s bullet would do almost nothing.
/// </summary>
public class Bullet : MonoBehaviour
{
    private const float Radius = 0.05f;
    // Room for a crowd: hits come back unsorted, and a buffer that fills drops some at random,
    // which could be the nearest one, letting a bullet pass something it should have stopped on.
    private static readonly RaycastHit[] Hits = new RaycastHit[64];

    private Vector3 _direction;
    private float _speed;
    private float _damage;
    private float _lifeLeft;
    private Transform _shooter;

    /// <summary>Sends a bullet just taken from the pool on its way. The shooter's own colliders never stop it.</summary>
    public void Launch(Vector3 direction, float speed, float damage, float lifetime, Transform shooter)
    {
        _direction = direction.normalized;
        _speed = speed;
        _damage = damage;
        _lifeLeft = lifetime;
        _shooter = shooter;
        transform.rotation = Quaternion.LookRotation(_direction);
    }

    private void FixedUpdate()
    {
        float step = _speed * Time.fixedDeltaTime;
        Vector3 from = transform.position;

        if (TryFindStop(from, step, out RaycastHit stop))
        {
            transform.position = from + _direction * stop.distance;
            Strike(stop.collider);
            ObjectPoolManager.ReturnObjectToPool(gameObject);
            return;
        }

        transform.position = from + _direction * step;
        _lifeLeft -= Time.fixedDeltaTime;
        if (_lifeLeft <= 0f) ObjectPoolManager.ReturnObjectToPool(gameObject);
    }

    // The nearest hit along this step that stops bullets. SphereCastNonAlloc returns hits unsorted,
    // and things started inside come back at distance 0, so a muzzle poking into a wall stops at once.
    private bool TryFindStop(Vector3 from, float step, out RaycastHit stop)
    {
        stop = default;
        int count = Physics.SphereCastNonAlloc(from, Radius, _direction, Hits, step, ~0, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (Hits[i].distance >= nearest || !Stops(Hits[i].collider)) continue;
            nearest = Hits[i].distance;
            stop = Hits[i];
        }
        return nearest != float.MaxValue;
    }

    private bool Stops(Collider collider)
    {
        // Its own colliders: a primitive mesh left with one would otherwise stop the bullet at the muzzle.
        if (collider.transform.IsChildOf(transform)) return false;
        if (_shooter != null && collider.transform.IsChildOf(_shooter)) return false;

        IBulletTarget target = collider.GetComponentInParent<IBulletTarget>();
        return target == null || target.StopsBullets;
    }

    private void Strike(Collider collider)
    {
        Health health = collider.GetComponentInParent<Health>();
        if (health == null || health.IsDead) return;

        health.ApplyDamage(_damage);
        if (health.IsDead) collider.GetComponentInParent<IKnockdown>()?.KnockDown();
    }
}
