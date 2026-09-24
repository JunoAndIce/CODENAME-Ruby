using UnityEngine;

public class BulletController : MonoBehaviour
{
    public float _speed = 3f;
    public float _lifeTime = 3f; // Default value in case none is passed.

    void Update()
    {
        transform.Translate(Vector3.forward * _speed * Time.deltaTime);

        _lifeTime -= Time.deltaTime;
        if (_lifeTime <= 0)
        {
            // Destroy(gameObject);
            ObjectPoolManager.ReturnObjectToPool(gameObject);
        }
    }

    // A clean helper function your gun can call
    public void InitializeBullet(float customSpeed, float customLifeTime)
    {
        _speed = customSpeed;
        _lifeTime = customLifeTime;
    }
}
