using UnityEngine;

/// <summary>
/// A semi-auto revolver: it fires on the trigger's rising edge, so holding does nothing. Shots
/// leave the Fire Point (the muzzle of the gun in her hand) and fly flat toward her aim.
/// </summary>
public class GunController : MonoBehaviour
{
    // Closer than this, the cursor is on top of her, and the line from the muzzle to it could point anywhere.
    private const float MinAimDistance = 0.5f;
    // How far off her aim, in degrees seen from her centre, the muzzle can be and still fire. Aimed,
    // her gun sits about 15 degrees off it, being in her hand to one side.
    private const float MuzzleCone = 30f;

    public GameObject _bulletPrefab;
    [Tooltip("Left empty, the PlayerController above this object.")]
    public PlayerController _player;
    public float _bulletSpeed;
    public float _bulletLifeTime = 2f;
    public float _timeBetweenShots;
    public Transform _firePoint;

    private float _shotsCounter;
    private bool _triggerWasPressed;

    void Awake()
    {
        // The gun rides her hand bone, under the player, so it can find her rather than need wiring.
        if (_player == null) _player = GetComponentInParent<PlayerController>();
        if (_player == null || _firePoint == null)
        {
            Debug.LogError($"{name}: GunController needs a PlayerController above it (or in Player) and a Fire Point — it can't fire.", this);
            enabled = false;
        }
    }

    void Update()
    {
        _shotsCounter -= Time.deltaTime;

        bool pulled = _player.TriggerPressed && !_triggerWasPressed;
        _triggerWasPressed = _player.TriggerPressed;

        // A pull during a charge is dropped, not queued: semi-auto means pulling again after.
        if (!pulled || _shotsCounter > 0f || _player._bulletCount <= 0 || !_player.CanShoot) return;

        Fire();
    }

    void Fire()
    {
        _shotsCounter = _timeBetweenShots;

        Vector3 from = FirePosition();
        GameObject bulletObj = ObjectPoolManager.SpawnObject(_bulletPrefab, from, Quaternion.LookRotation(AimDirection(from)));
        if (bulletObj.TryGetComponent(out BulletController bullet))
            bullet.InitializeBullet(_bulletSpeed, _bulletLifeTime);

        _player._bulletCount--;
    }

    // The muzzle, while it lies about where she aims. Her body turns to the cursor at once, but her
    // arm takes a moment to swing round after a flick, and a shot from where the muzzle still was
    // left from her old side, or from behind her through her own body. Then it leaves from her aim
    // line instead, as far out as the muzzle and at its height.
    Vector3 FirePosition()
    {
        Vector3 muzzle = _firePoint.position;
        Vector3 root = _player.transform.position;
        Vector3 reach = muzzle - root;
        reach.y = 0f;
        Vector3 aim = AimDirection(root);
        if (Vector3.Angle(reach, aim) <= MuzzleCone) return muzzle;

        Vector3 from = root + aim * reach.magnitude;
        from.y = muzzle.y;
        return from;
    }

    // Flat, from where the shot leaves to her aim point. The muzzle rides an animated hand, so its
    // own facing swings with her walk and tilts, and a bullet flown along it would hit the floor.
    // From the muzzle rather than her centre, so shots meet the cursor although the gun is off to
    // her side.
    Vector3 AimDirection(Vector3 from)
    {
        Vector3 aim = _player.AimPoint - from;
        aim.y = 0f;
        if (aim.sqrMagnitude >= MinAimDistance * MinAimDistance) return aim.normalized;

        Vector3 facing = _player.transform.forward;
        facing.y = 0f;
        return facing.normalized;
    }
}
