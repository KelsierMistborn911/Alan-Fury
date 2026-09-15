using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class Projectile : MonoBehaviour
{
    private float damage;
    private float staggerForce;
    private float lifetime;
    private LayerMask targetLayers;
    private float timer;
    private Transform owner;

    static GameObject _template;

    public static GameObject DefaultPrefab()
    {
        if (_template != null) return _template;

        _template = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _template.name = "RangedBolt";
        _template.transform.localScale = new Vector3(0.06f, 0.16f, 0.06f);
        var col = _template.GetComponent<Collider>();
        col.isTrigger = true;
        var rb = _template.GetComponent<Rigidbody>();
        if (rb == null) rb = _template.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        if (_template.GetComponent<Projectile>() == null)
            _template.AddComponent<Projectile>();
        _template.SetActive(false);
        Object.DontDestroyOnLoad(_template);
        return _template;
    }

    public void Initialize(float damage, float staggerForce, float speed,
                           float lifetime, LayerMask targetLayers)
    {
        Initialize(damage, staggerForce, speed, lifetime, targetLayers, null);
    }

    public void Initialize(float damage, float staggerForce, float speed,
                           float lifetime, LayerMask targetLayers, Transform owner)
    {
        this.damage = damage;
        this.staggerForce = staggerForce;
        this.lifetime = lifetime;
        this.targetLayers = targetLayers;
        this.owner = owner;
        timer = 0f;
        var rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.velocity = transform.forward * speed;
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= lifetime) Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (other == null) return;
        if (IsOwner(other.transform)) return;
        if (targetLayers.value != 0 && ((1 << other.gameObject.layer) & targetLayers) == 0)
            return;

        if (other.TryGetComponent<IDamageable>(out var damageable))
        {
            damageable.TakeDamage(damage, transform.position);
            Vector3 knockback = (other.transform.position - transform.position).normalized;
            knockback.y = 0f;
            damageable.ApplyKnockback(knockback * staggerForce);
        }
        Destroy(gameObject);
    }

    bool IsOwner(Transform t)
    {
        if (owner == null || t == null) return false;
        return t == owner || t.IsChildOf(owner) || owner.IsChildOf(t);
    }
}
