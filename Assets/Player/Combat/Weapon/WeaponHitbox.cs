using UnityEngine;
using System.Collections.Generic;

public class WeaponHitbox : MonoBehaviour
{
    [Header("Угол конуса атаки (градусы в одну сторону)")]
    public float coneHalfAngle = 60f; // итого 120° дуга

    [Header("Отладка")]
    [Tooltip("Показывать зону удара в игре плоским красным прямоугольником на время атаки.")]
    public bool debugShowZone = true;
    [Tooltip("Высота зоны над точкой origin (м), чтобы не тонула в земле.")]
    public float debugZoneY = 0.05f;

    private Transform _debugZone; // создаётся лениво
    private float _activeCone;    // полуугол текущей атаки: обычная берёт coneHalfAngle, вторичная передаёт свой

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
                         LayerMask layers, float duration, float tickInterval, float chargePercent = 0f,
                         int comboIndex = 0, float coneHalfAngleOverride = -1f)
    {
        _activeCone = coneHalfAngleOverride > 0f ? coneHalfAngleOverride : coneHalfAngle;
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
            visual.ShowArc(direction, offset, duration, chargePercent, comboIndex);

        if (debugShowZone) ShowDebugZone();
    }

    void Update()
    {
        if (!isActive) return;

        timer += Time.deltaTime;
        if (timer >= duration)
        {
            isActive = false;
            if (visual != null) visual.HideArc();
            if (_debugZone != null) _debugZone.gameObject.SetActive(false);
            return;
        }

        if (Time.time >= nextTickTime)
        {
            DetectHits();
            nextTickTime = Time.time + tickInterval;
        }

        // Зона едет вместе с существом (прыжок, рывок) — как и реальный OverlapBox.
        if (debugShowZone && _debugZone != null && _debugZone.gameObject.activeSelf)
            PlaceDebugZone();
    }

    private void PlaceDebugZone()
    {
        Vector3 origin = transform.position + offset;
        _debugZone.position = origin + Vector3.up * debugZoneY;
        _debugZone.rotation = Quaternion.LookRotation(direction);
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

        float requiredDot = Mathf.Cos(_activeCone * Mathf.Deg2Rad);

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
                damageable.TakeDamage(damage, transform.position);

                Vector3 knockback = (col.transform.position - transform.position).normalized;
                knockback.y = 0f;
                damageable.ApplyKnockback(knockback * stagger);

                lastHitTime[col.gameObject] = Time.time;

                onHit?.Invoke();
            }
        }
    }

    // ===================== Дебаг-зона в игре =====================

    // Плоская красная зона удара (вид сверху): расширяется от существа вперёд.
    // Форма = горизонтальное пересечение коробки OverlapBox и конуса coneHalfAngle.
    private void ShowDebugZone()
    {
        if (_debugZone == null)
        {
            var go = new GameObject("DebugHitZone");
            go.AddComponent<MeshFilter>().mesh = new Mesh();
            var mr = go.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetFloat("_Surface", 1f); // Transparent
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
            mat.color = new Color(1f, 0f, 0f, 0.35f);
            mr.material = mat;
            _debugZone = go.transform;
        }

        BuildZoneMesh(_debugZone.GetComponent<MeshFilter>().mesh);
        PlaceDebugZone();
        _debugZone.localScale = Vector3.one;
        _debugZone.gameObject.SetActive(true);
    }

    // Трапеция-веер в локальных координатах: вершина в нуле, вперёд по +Z.
    // Полуширина на дистанции d = min(tan(coneHalfAngle)·d, radius).
    private void BuildZoneMesh(Mesh mesh)
    {
        const int segments = 8;
        float tanA = Mathf.Tan(Mathf.Clamp(_activeCone, 1f, 89f) * Mathf.Deg2Rad);

        var verts = new Vector3[(segments + 1) * 2];
        var tris = new int[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float d = range * i / segments;
            float hw = Mathf.Min(tanA * d, radius);
            verts[i * 2] = new Vector3(-hw, 0f, d);
            verts[i * 2 + 1] = new Vector3(hw, 0f, d);
        }
        for (int i = 0; i < segments; i++)
        {
            int v = i * 2, t = i * 6;
            tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
            tris[t + 3] = v + 1; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
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
