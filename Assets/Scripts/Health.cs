using System;
using UnityEngine;

public class Health : MonoBehaviour
{
    [SerializeField] private float _maxHealth = 100f;
    private float _currentHealth;
    private bool _isDead;
    public float MaxHealth => _maxHealth;
    public float CurrentHealth => _currentHealth;
    public float Normalized => _maxHealth <= 0f ? 0f : _currentHealth / _maxHealth;
    public bool IsDead => _isDead;
    public event Action<float> OnDamaged;
    public event Action OnDied;

    private void Awake()
    {
        _currentHealth = _maxHealth;
    }

    public void ApplyDamage(float amount)
    {
        if (_isDead || _currentHealth <= 0f)
        {
            return;
        }

        float damageTaken = MathF.Min(amount, _currentHealth);

        _currentHealth -= damageTaken;

        OnDamaged?.Invoke(damageTaken);

        if (_currentHealth <= 0f)
        {
            _isDead = true;
            OnDied?.Invoke();
        }
    }

    /// <summary>Full restore. Needed once enemies come out of ObjectPoolManager.</summary>
    public void ResetHealth()
    {
        _currentHealth = _maxHealth;
        _isDead = false;
    }
}
