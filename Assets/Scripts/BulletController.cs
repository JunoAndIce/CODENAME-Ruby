using UnityEngine;

public class BulletController : MonoBehaviour
{
    public float _speed = 3f;
    public float _lifeTime = 3f;

    void Update()
    {
        transform.Translate(Vector3.forward * _speed * Time.deltaTime);

        _lifeTime -= Time.deltaTime;
        if (_lifeTime <= 0f) ObjectPoolManager.ReturnObjectToPool(gameObject);
    }

    public void InitializeBullet(float speed, float lifeTime)
    {
        _speed = speed;
        _lifeTime = lifeTime;
    }
}
