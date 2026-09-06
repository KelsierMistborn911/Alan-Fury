using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class Projectile : MonoBehaviour
{
    private float damage;
    private float staggerForce;
    private float lifetime;
    private LayerMask targetLayers;
    private float timer;

    public void Initialize(float damage, float staggerForce, float speed,
                           float lifetime, LayerMask targetLayers)
    {
        this.damage = damage;
        this.staggerForce = staggerForce;
        this.lifetime = lifetime;
        this.targetLayers = targetLayers;
        timer = 0f;
        GetComponent<Rigidbody>().velocity = transform.forward * speed;
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= lifetime) Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (((1 << other.gameObject.layer) & targetLayers) == 0) return;

        if (other.TryGetComponent<IDamageable>(out var damageable))
        {
            damageable.TakeDamage(damage);
            Vector3 knockback = (other.transform.position - transform.position).normalized;
            knockback.y = 0f;
            damageable.ApplyKnockback(knockback * staggerForce);
        }
        Destroy(gameObject);
    }
}