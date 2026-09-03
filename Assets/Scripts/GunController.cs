using UnityEngine;

public class GunController : MonoBehaviour
{
    public bool _isFiring;
    public GameObject _bulletPrefab; 
    public PlayerController _player;
    public float _bulletSpeed;
    public float _bulletLifeTime = 2f; // Added this variable!

    public float _timeBetweenShots;
    private float _shotsCounter;
    public Transform _firePoint;

    void Update()
    {   
        if (_isFiring)
        {
            _shotsCounter -= Time.deltaTime;
            if (_shotsCounter <= 0)
            {
                if (_player._bulletCount > 0)
                {
                    _shotsCounter = _timeBetweenShots;
                    
                    // GameObject bulletObj = Instantiate(_bulletPrefab, _firePoint.position, _firePoint.rotation);
                    GameObject bulletObj = ObjectPoolManager.SpawnObject(_bulletPrefab, _firePoint.position, _firePoint.rotation);
                    Debug.Log("Bullet Spawned from Pool: " + bulletObj.name);
                    BulletController newBullet = bulletObj.GetComponent<BulletController>();
                    
                    if (newBullet != null)
                    {
                        // Pass both speed AND lifetime safely right here
                        newBullet.InitializeBullet(_bulletSpeed, _bulletLifeTime);
                    }

                    _player._bulletCount--;
                }
            }
        }
        else
        {
            _shotsCounter = 0;
        }
        
        if (_player._triggerHeld == true)
        {
            _isFiring = false;
        }
    }
}
