using UnityEngine;

public class EnemyController : MonoBehaviour
{

    public float _moveSpeed;
    private Rigidbody _enemyRB;

    
    [SerializeField] private PlayerController _player;
    
    void Start()
    {
        _enemyRB = GetComponent<Rigidbody>();
        _player = FindAnyObjectByType<PlayerController>();
    }

    void FixedUpdate()
    {
        _enemyRB.linearVelocity = transform.forward * _moveSpeed;
    }

    // Update is called once per frame
    void Update()
    {
        transform.LookAt(_player.transform);
    }
}
