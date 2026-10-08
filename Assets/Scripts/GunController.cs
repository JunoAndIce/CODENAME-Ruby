using UnityEngine;

/// <summary>A semi-auto revolver: it fires on the trigger's rising edge, so holding does nothing.</summary>
public class GunController : MonoBehaviour
{
    public GameObject _bulletPrefab;
    public PlayerController _player;
    public float _bulletSpeed;
    public float _bulletLifeTime = 2f;
    public float _timeBetweenShots;
    public Transform _firePoint;

    private float _shotsCounter;
    private bool _triggerWasPressed;

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

        GameObject bulletObj = ObjectPoolManager.SpawnObject(_bulletPrefab, _firePoint.position, _firePoint.rotation);
        if (bulletObj.TryGetComponent(out BulletController bullet))
            bullet.InitializeBullet(_bulletSpeed, _bulletLifeTime);

        _player._bulletCount--;
    }
}
