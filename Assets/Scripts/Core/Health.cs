using System;
using UnityEngine;

public class Health : MonoBehaviour
{
    [SerializeField] private float _maxHealth = 100f;

    private float _currentHealth;

    public bool IsDead { get; private set; }
    public event Action<float> OnDamaged;

    private void Awake() => _currentHealth = _maxHealth;

    public void ApplyDamage(float amount)
    {
        if (IsDead) return;

        float damageTaken = Mathf.Min(amount, _currentHealth);
        _currentHealth -= damageTaken;
        OnDamaged?.Invoke(damageTaken);

        if (_currentHealth <= 0f) IsDead = true;
    }

    /// <summary>Alive again at full health: a character that comes back after dying (Ruby's revive).</summary>
    public void Revive()
    {
        _currentHealth = _maxHealth;
        IsDead = false;
    }
}
