using UnityEngine;
using System.Collections.Generic;

public class WeaponHitbox : MonoBehaviour
{
    [Header("Угол конуса атаки (градусы в одну сторону)")]
    public float coneHalfAngle = 60f; // итого 120° дуга

    private bool isActive;
    private float timer;
    private float duration;
    private float tickInterval;
    private float nextTickTime;

    private float range;
    private float radius;
    private float height;
    private Vector3 offset;
    private Vector3 direction;
    private float damage;
    private float stagger;
    private LayerMask layers;

    private Dictionary<GameObject, float> lastHitTime = new Dictionary<GameObject, float>();

    public SwordAttackVisual visual;
    public System.Action onHit;

    void Awake()
    {
        if (visual == null)
            visual = GetComponent<SwordAttackVisual>();
    }

    public void Activate(float range, float radius, float height, Vector3 offset,
                         Vector3 direction, float damage, float stagger,
                         LayerMask layers, float duration, float tickInterval, float chargePercent = 0f)
    {
        this.range = range;
        this.radius = radius;
        this.height = height;
        this.offset = offset;
        this.direction = direction.normalized;
        this.damage = damage;
        this.stagger = stagger;
        this.layers = layers;
        this.duration = duration;
        this.tickInterval = tickInterval;
        this.nextTickTime = 0f;

        isActive = true;
        timer = 0f;
        lastHitTime.Clear();

        if (visual != null)
            visual.ShowArc(direction, offset, duration, chargePercent);
    }

    void Update()
    {
        if (!isActive) return;

        timer += Time.deltaTime;
        if (timer >= duration)
        {
            isActive = false;
            if (visual != null) visual.HideArc();
            return;
        }

        if (Time.time >= nextTickTime)
        {
            DetectHits();
            nextTickTime = Time.time + tickInterval;
        }
    }

    void DetectHits()
    {
        Vector3 origin = transform.position + offset;
        Vector3 halfExtents = new Vector3(radius, height * 0.5f, range * 0.5f);
        Collider[] colliders = Physics.OverlapBox(
            origin + direction * (range * 0.5f),
            halfExtents,
            Quaternion.LookRotation(direction),
            layers
        );

        float requiredDot = Mathf.Cos(coneHalfAngle * Mathf.Deg2Rad);

        foreach (Collider col in colliders)
        {
            Vector3 dirToTarget = (col.transform.position - origin);
            dirToTarget.y = 0f;
            dirToTarget.Normalize();

            float dot = Vector3.Dot(direction, dirToTarget);
            if (dot < requiredDot) continue;

            // Проверка тик-интервала на цель
            if (lastHitTime.TryGetValue(col.gameObject, out float lastHit))
                if (Time.time - lastHit < tickInterval) continue;

            if (col.TryGetComponent<IDamageable>(out var damageable))
            {
                damageable.TakeDamage(damage);

                Vector3 knockback = (col.transform.position - transform.position).normalized;
                knockback.y = 0f;
                damageable.ApplyKnockback(knockback * stagger);

                lastHitTime[col.gameObject] = Time.time;

                // Вызов события onHit
                onHit?.Invoke();
            }
        }
    }

    // Gizmos — видны всегда в режиме выбора объекта, помогают настроить хитбокс
    void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position + offset;
        Vector3 dir = direction.magnitude > 0.01f ? direction : transform.forward;

        // Хитбокс (синий)
        Gizmos.color = isActive ? new Color(1f, 0f, 0f, 0.4f) : new Color(0f, 0.5f, 1f, 0.3f);
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(
            origin + dir * (range * 0.5f),
            Quaternion.LookRotation(dir),
            Vector3.one
        );
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(radius * 2f, height, range));
        Gizmos.matrix = oldMatrix;

        // Конус (жёлтый)
        Gizmos.color = Color.yellow;
        float halfAngleRad = coneHalfAngle * Mathf.Deg2Rad;
        Vector3 leftDir = Quaternion.Euler(0, -coneHalfAngle, 0) * dir;
        Vector3 rightDir = Quaternion.Euler(0, coneHalfAngle, 0) * dir;
        Gizmos.DrawRay(origin, leftDir * range);
        Gizmos.DrawRay(origin, rightDir * range);
        Gizmos.DrawRay(origin, dir * range);
    }
}
