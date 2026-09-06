using UnityEngine;

/// <summary>
/// HP летуна. Отдельно от WerewolfStats. Игрок бьёт через IDamageable / WeaponHitbox.
/// </summary>
public class GhostStats : MonoBehaviour, IDamageable
{
    [Header("Здоровье")]
    public float maxHealth = 12f;

    public float Health => _health;
    public float HealthPercent => maxHealth > 0f ? _health / maxHealth : 0f;
    public bool IsAlive => _health > 0f;

    public System.Action OnDeath;

    private float _health;
    private GhostFlyer _flyer;

    void Awake()
    {
        _health = maxHealth;
        _flyer = GetComponent<GhostFlyer>();
    }

    public void TakeDamage(float amount)
    {
        if (!IsAlive) return;
        _health = Mathf.Max(0f, _health - Mathf.Max(0f, amount));
        if (_health <= 0f)
            Die();
    }

    public void TakeDamage(float amount, Vector3 sourcePosition) => TakeDamage(amount);

    public void TakeHit(HitInfo hit)
    {
        float dmg = hit.finalDamage > 0f ? hit.finalDamage : hit.rawDamage;
        TakeDamage(dmg);
    }

    public void ApplyKnockback(Vector3 force)
    {
        if (_flyer == null || force.sqrMagnitude < 0.01f) return;
        transform.position += Vector3.ClampMagnitude(force, 1.2f);
    }

    void Die()
    {
        OnDeath?.Invoke();
        if (_flyer != null)
        {
            _flyer.Stop();
            _flyer.enabled = false;
        }
        var host = GetComponent<GhostHost>();
        if (host != null) host.enabled = false;
        enabled = false;
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;
    }
}
