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

    void Update()
    {
        _shotsCounter -= Time.deltaTime;

        bool _triggerPressedNow = _player.TriggerPressed && !_triggerPressedBefore;
        _triggerPressedBefore = _player.TriggerPressed;

        if (!_triggerPressedNow) return;
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
