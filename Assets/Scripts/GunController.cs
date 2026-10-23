using UnityEngine;

/// <summary>
/// A semi-auto revolver: it fires on the trigger's rising edge, so holding does nothing. Shots
/// leave the Fire Point (the muzzle of the gun in her hand) and fly flat toward her aim.
///
/// The pull snaps her arm onto the cursor (CharacterAnimator.SnapToTarget) and the bullet leaves in
/// LateUpdate, once that frame's pose is on. Her arm otherwise swings after her aim, and a shot from
/// wherever the muzzle had got to left from her side or behind her, crossing the barrel, so the
/// player couldn't tell where a bullet would come from or go.
/// </summary>
public class GunController : MonoBehaviour
{
    // Closer than this, the cursor is on top of her, and the line from the muzzle to it could point anywhere.
    private const float MinAimDistance = 0.5f;

    public GameObject _bulletPrefab;
    [Tooltip("Left empty, the PlayerController above this object.")]
    public PlayerController _player;
    public float _bulletSpeed;
    public float _bulletLifeTime = 2f;
    public float _timeBetweenShots;
    public Transform _firePoint;

    private CharacterAnimator _pose;
    private float _shotsCounter;
    private bool _triggerWasPressed;
    private bool _firing;   // pulled this frame: the bullet leaves in LateUpdate

    void Awake()
    {
        // The gun rides her hand bone, under the player, so it can find her rather than need wiring.
        if (_player == null) _player = GetComponentInParent<PlayerController>();
        if (_player == null || _firePoint == null)
        {
            Debug.LogError($"{name}: GunController needs a PlayerController above it (or in Player) and a Fire Point — it can't fire.", this);
            enabled = false;
            return;
        }
        // Her model, which the hand bone is under. Without one, shots leave the muzzle where it is.
        _pose = GetComponentInParent<CharacterAnimator>();
    }

    void Update()
    {
        _shotsCounter -= Time.deltaTime;

        bool pulled = _player.TriggerPressed && !_triggerWasPressed;
        _triggerWasPressed = _player.TriggerPressed;

        // A pull during a charge is dropped, not queued: semi-auto means pulling again after.
        if (!pulled || _shotsCounter > 0f || _player._bulletCount <= 0 || !_player.CanShoot) return;

        // Before the animation runs this frame, so the arm is on the cursor when the bullet leaves.
        if (_pose != null) _pose.SnapToTarget();
        _firing = true;
    }

    void LateUpdate()
    {
        if (!_firing) return;
        _firing = false;
        Fire();
    }

    void Fire()
    {
        _shotsCounter = _timeBetweenShots;

        GameObject bulletObj = ObjectPoolManager.SpawnObject(_bulletPrefab, _firePoint.position, Quaternion.LookRotation(AimDirection()));
        if (bulletObj.TryGetComponent(out BulletController bullet))
            bullet.InitializeBullet(_bulletSpeed, _bulletLifeTime);

        _player._bulletCount--;
    }

    // Flat, from the muzzle to her aim point. The muzzle rides an animated hand, so its own facing
    // swings with her walk and tilts, and a bullet flown along it would hit the floor. From the
    // muzzle rather than her centre, so shots meet the cursor although the gun is off to her side.
    Vector3 AimDirection()
    {
        Vector3 aim = _player.AimPoint - _firePoint.position;
        aim.y = 0f;
        if (aim.sqrMagnitude >= MinAimDistance * MinAimDistance) return aim.normalized;

        Vector3 facing = _player.transform.forward;
        facing.y = 0f;
        return facing.normalized;
    }
}
