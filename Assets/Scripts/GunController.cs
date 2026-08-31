using UnityEngine;

public class GunController : MonoBehaviour
{
    public bool isFiring;
    public BulletController bullet;
    public PlayerController player;
    public float bulletSpeed;

    public float timeBetweenShots;
    private float shotsCounter;

    public Transform firePoint;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {   
        

        if (isFiring)
        {
            shotsCounter -= Time.deltaTime;
            if (shotsCounter <= 0)
            {
                if (player._bulletCount > 0)
                {
                    shotsCounter = timeBetweenShots;
                    BulletController newBullet = Instantiate(bullet, firePoint.position, firePoint.rotation);
                    newBullet.speed = bulletSpeed;
                    player._bulletCount--;
                }
            }
        }
        else
        {
            shotsCounter = 0;
        }
        
        if (player.triggerHeld == true)
        {
            isFiring = false;
        }
    }
}
