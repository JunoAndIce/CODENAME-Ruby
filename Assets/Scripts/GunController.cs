using UnityEngine;

public class GunController : MonoBehaviour
{
    public GameObject _bulletPrefab;
    public PlayerController _player;
    public float _bulletSpeed;
    public float _bulletLifeTime = 2f;

    public float _timeBetweenShots;
    private float _shotsCounter;
    public Transform _firePoint;

    private bool _triggerPressedBefore;
    private GrappleController _grapple;

    void Start()
    {
        _grapple = GetComponentInParent<GrappleController>();
    }

    void Update()
    {
        _shotsCounter -= Time.deltaTime;

        bool _triggerPressedNow = _player.TriggerPressed && !_triggerPressedBefore;
        _triggerPressedBefore = _player.TriggerPressed;

        if (!_triggerPressedNow) return;
        // The chain owns the trigger while tethered — AND on the frame it used it:
        // a push detaches mid-press, so IsAttached alone would let the same click
        // also fire the gun.
        if (_grapple != null && (_grapple.IsAttached || _grapple.ConsumedTriggerThisFrame)) return;
        if (_shotsCounter > 0f) return;           // hammer still cycling
        if (_player._bulletCount <= 0) return;    // empty cylinder

        Fire();
    }

    void Fire()
    {
        _shotsCounter = _timeBetweenShots;

        GameObject bulletObj = ObjectPoolManager.SpawnObject(_bulletPrefab, _firePoint.position, _firePoint.rotation);

        if (bulletObj.TryGetComponent(out BulletController bullet))
            bullet.InitializeBullet(_bulletSpeed, _bulletLifeTime);

        _player._bulletCount--;
    }
}
