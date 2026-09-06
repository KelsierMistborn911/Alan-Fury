using UnityEngine;
using System.Collections.Generic;

public enum HitZoneShape
{
    BoxCone = 0, // legacy: OverlapBox + cone
    Sector,      // annulus sector (slash / heavy / swipe)
    Capsule,     // thin forward stadium (thrust)
    Ellipse      // oval around origin (jump / bite)
}

public class WeaponHitbox : MonoBehaviour
{
    [Header("Угол конуса атаки (градусы в одну сторону)")]
    public float coneHalfAngle = 60f; // итого 120° дуга; BoxCone и fallback

    [Header("Отладка")]
    [Tooltip("Показывать зону удара в игре плоским мешем на время атаки.")]
    public bool debugShowZone = true;
    [Tooltip("Высота зоны над точкой origin (м), чтобы не тонула в земле.")]
    public float debugZoneY = 0.05f;

    private Transform _debugZone;
    private float _activeCone;
    private HitZoneShape _shape;
    private float _innerRadius;
    private float _yawOffset;

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
    private HitInfo _hitInfo;
    private bool _hasHitInfo;

    private Dictionary<GameObject, float> lastHitTime = new Dictionary<GameObject, float>();

    public SwordAttackVisual visual;
    public System.Action onHit;

    void Awake()
    {
        if (visual == null)
            visual = GetComponent<SwordAttackVisual>();
    }

    /// <summary>Контекст удара (зона, intent, charge). Задать до/вместе с Activate.</summary>
    public void SetHitInfo(HitInfo info)
    {
        _hitInfo = info;
        _hasHitInfo = true;
    }

    public void Activate(float range, float radius, float height, Vector3 offset,
                         Vector3 direction, float damage, float stagger,
                         LayerMask layers, float duration, float tickInterval, float chargePercent = 0f,
                         int comboIndex = 0, float coneHalfAngleOverride = -1f,
                         HitZoneShape shape = HitZoneShape.BoxCone, float innerRadius = -1f, float yawOffsetDeg = 0f)
    {
        _activeCone = coneHalfAngleOverride > 0f ? coneHalfAngleOverride : coneHalfAngle;
        _shape = shape;
        _yawOffset = yawOffsetDeg;
        this.range = range;
        this.radius = radius;
        this.height = height > 0.2f ? height : 2.2f;
        this.offset = offset;
        this.direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
        this.damage = damage;
        this.stagger = stagger;
        this.layers = ResolveLayers(layers);
        this.duration = duration;
        this.tickInterval = tickInterval;
        this.nextTickTime = 0f;
        _innerRadius = innerRadius >= 0f
            ? innerRadius
            : (shape == HitZoneShape.Sector ? this.range * 0.24f : 0f);

        if (!_hasHitInfo)
        {
            _hitInfo = HitInfo.Basic(damage, transform.position);
            _hitInfo.stagger = stagger;
            _hitInfo.hitDirection = this.direction;
            _hitInfo.chargePercent = chargePercent;
        }
        else
        {
            _hitInfo.rawDamage = damage;
            _hitInfo.stagger = stagger;
            _hitInfo.hitDirection = this.direction;
            _hitInfo.sourcePosition = transform.position;
        }

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
            _hasHitInfo = false;
            if (visual != null) visual.HideArc();
            if (_debugZone != null) _debugZone.gameObject.SetActive(false);
            return;
        }

        if (Time.time >= nextTickTime)
        {
            DetectHits();
            nextTickTime = Time.time + tickInterval;
        }

        if (debugShowZone && _debugZone != null && _debugZone.gameObject.activeSelf)
            PlaceDebugZone();
    }

    private void PlaceDebugZone()
    {
        Vector3 origin = HitOrigin();
        _debugZone.position = origin + Vector3.up * debugZoneY;
        Vector3 dir = direction.sqrMagnitude > 0.01f ? direction : transform.forward;
        _debugZone.rotation = Quaternion.LookRotation(dir);
    }

    void DetectHits()
    {
        Vector3 origin = HitOrigin();
        Collider[] colliders = QueryColliders(origin);
        if (colliders == null || colliders.Length == 0)
            colliders = QueryColliders(origin, Physics.AllLayers);

        int applied = 0;
        foreach (Collider col in colliders)
        {
            if (!InsideZone(origin, col.transform.position)) continue;

            if (lastHitTime.TryGetValue(col.gameObject, out float lastHit))
                if (Time.time - lastHit < tickInterval) continue;

            if (col.transform == transform || col.transform.IsChildOf(transform))
                continue;

            IDamageable damageable = col.GetComponentInParent<IDamageable>();
            if (damageable == null) continue;

            var host = damageable as Component;
            if (host != null && (host.transform == transform || transform.IsChildOf(host.transform)))
                continue;

            ApplyHit(damageable, col.transform);
            applied++;
        }

        if (applied == 0)
            ProbeKnownTargets(origin);
    }

    void ProbeKnownTargets(Vector3 origin)
    {
        if (PlayerRegistry.Instance != null)
        {
            var players = PlayerRegistry.Instance.Players;
            for (int i = 0; i < players.Count; i++)
                TryDirect(origin, players[i]);
        }

        var wolves = FindObjectsOfType<WerewolfStats>();
        for (int i = 0; i < wolves.Length; i++)
        {
            if (wolves[i] != null && wolves[i].IsAlive)
                TryDirect(origin, wolves[i].transform);
        }

        var resources = FindObjectsOfType<PlayerResources>();
        for (int i = 0; i < resources.Length; i++)
        {
            if (resources[i] != null && resources[i].IsAlive)
                TryDirect(origin, resources[i].transform);
        }
    }

    void TryDirect(Vector3 origin, Transform target)
    {
        if (target == null) return;
        if (target == transform || target.IsChildOf(transform) || transform.IsChildOf(target))
            return;
        if (!InsideZone(origin, target.position)) return;

        if (lastHitTime.TryGetValue(target.gameObject, out float lastHit) &&
            Time.time - lastHit < tickInterval)
            return;

        var damageable = target.GetComponentInParent<IDamageable>();
        if (damageable == null) return;
        var host = damageable as Component;
        if (host != null && (host.transform == transform || transform.IsChildOf(host.transform)))
            return;

        ApplyHit(damageable, target);
    }

    void ApplyHit(IDamageable damageable, Transform target)
    {
        HitInfo hit = _hitInfo;
        hit.rawDamage = damage;
        hit.sourcePosition = transform.position;
        hit.hitDirection = direction;
        hit.stagger = stagger;
        hit.finalDamage = damage;

        damageable.TakeHit(hit);

        Vector3 knockback = (target.position - transform.position).normalized;
        knockback.y = 0f;
        damageable.ApplyKnockback(knockback * stagger);

        lastHitTime[target.gameObject] = Time.time;
        onHit?.Invoke();
    }

    Collider[] QueryColliders(Vector3 origin, int mask = -1)
    {
        if (mask < 0) mask = layers.value == 0 ? Physics.AllLayers : layers.value;
        float hy = Mathf.Max(height * 0.5f, 1.2f);

        if (_shape == HitZoneShape.BoxCone)
        {
            Vector3 halfExtents = new Vector3(radius, hy, range * 0.5f);
            return Physics.OverlapBox(
                origin + direction * (range * 0.5f),
                halfExtents,
                Quaternion.LookRotation(direction),
                mask
            );
        }

        if (_shape == HitZoneShape.Capsule)
        {
            Vector3 halfExtents = new Vector3(Mathf.Max(radius, 0.35f), hy, range * 0.5f + Mathf.Max(radius, 0.35f));
            return Physics.OverlapBox(
                origin + direction * (range * 0.5f),
                halfExtents,
                Quaternion.LookRotation(direction),
                mask
            );
        }

        float queryR = _shape == HitZoneShape.Ellipse
            ? Mathf.Max(range, radius)
            : range;
        return Physics.OverlapSphere(origin, Mathf.Max(queryR, 0.5f), mask);
    }

    Vector3 HitOrigin()
    {
        Vector3 dir = direction.sqrMagnitude > 0.01f ? direction : transform.forward;
        return transform.position + Quaternion.LookRotation(dir) * offset;
    }

    static LayerMask ResolveLayers(LayerMask mask)
    {
        if (mask.value != 0) return mask;
        int fury = LayerMask.NameToLayer("Fury");
        int player = LayerMask.NameToLayer("Player");
        int bits = 0;
        if (fury >= 0) bits |= 1 << fury;
        if (player >= 0) bits |= 1 << player;
        return bits != 0 ? (LayerMask)bits : (LayerMask)Physics.AllLayers;
    }

    bool InsideZone(Vector3 origin, Vector3 target)
    {
        if (Mathf.Abs(target.y - origin.y) > Mathf.Max(height * 0.5f, 1.2f) + 0.6f)
            return false;

        Vector3 flat = target - origin;
        flat.y = 0f;
        float dist = flat.magnitude;

        switch (_shape)
        {
            case HitZoneShape.Capsule:
                {
                    Vector3 end = origin + direction * range;
                    return PointToSegment(target, origin, end) <= radius;
                }
            case HitZoneShape.Ellipse:
                {
                    if (dist < 0.0001f) return true;
                    Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(direction));
                    Vector3 local = inv * flat;
                    float nx = radius > 0.01f ? local.x / radius : local.x;
                    float nz = range > 0.01f ? local.z / range : local.z;
                    return nx * nx + nz * nz <= 1f;
                }
            case HitZoneShape.Sector:
                {
                    if (dist > range + 0.15f) return false;
                    if (dist < 0.0001f) return true;
                    Vector3 fwd = Quaternion.Euler(0f, _yawOffset, 0f) * direction;
                    return Vector3.Angle(fwd, flat) <= _activeCone;
                }
            default:
                {
                    if (dist > range + 0.01f) return false;
                    float requiredDot = Mathf.Cos(_activeCone * Mathf.Deg2Rad);
                    if (dist < 0.0001f) return true;
                    return Vector3.Dot(direction, flat / dist) >= requiredDot;
                }
        }
    }

    static float PointToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a; ab.y = 0f;
        Vector3 ap = p - a; ap.y = 0f;
        float ab2 = ab.sqrMagnitude;
        float t = ab2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / ab2) : 0f;
        Vector3 closest = a + ab * t;
        Vector3 d = p - closest; d.y = 0f;
        return d.magnitude;
    }

    private void ShowDebugZone()
    {
        if (_debugZone == null)
        {
            var go = new GameObject("DebugHitZone");
            go.AddComponent<MeshFilter>().mesh = new Mesh();
            var mr = go.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetFloat("_Surface", 1f);
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

    private void BuildZoneMesh(Mesh mesh)
    {
        switch (_shape)
        {
            case HitZoneShape.Sector:
                BuildSectorMesh(mesh);
                break;
            case HitZoneShape.Capsule:
                BuildCapsuleMesh(mesh);
                break;
            case HitZoneShape.Ellipse:
                BuildEllipseMesh(mesh);
                break;
            default:
                BuildBoxConeMesh(mesh);
                break;
        }
    }

    private void BuildBoxConeMesh(Mesh mesh)
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

    private void BuildSectorMesh(Mesh mesh)
    {
        const int segments = 16;
        float inner = Mathf.Min(_innerRadius, range * 0.95f);
        float a0 = (_yawOffset - _activeCone) * Mathf.Deg2Rad;
        float a1 = (_yawOffset + _activeCone) * Mathf.Deg2Rad;

        var verts = new Vector3[(segments + 1) * 2];
        var tris = new int[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float a = Mathf.Lerp(a0, a1, i / (float)segments);
            float s = Mathf.Sin(a);
            float c = Mathf.Cos(a);
            verts[i * 2] = new Vector3(s * inner, 0f, c * inner);
            verts[i * 2 + 1] = new Vector3(s * range, 0f, c * range);
        }
        for (int i = 0; i < segments; i++)
        {
            int v = i * 2, t = i * 6;
            tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
            tris[t + 3] = v + 2; tris[t + 4] = v + 1; tris[t + 5] = v + 3;
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
    }

    private void BuildCapsuleMesh(Mesh mesh)
    {
        const int cap = 8;
        float r = Mathf.Max(0.05f, radius);
        int n = cap * 2 + 4;
        var verts = new Vector3[n];
        int vi = 0;
        verts[vi++] = new Vector3(-r, 0f, 0f);
        verts[vi++] = new Vector3(-r, 0f, range);
        for (int i = 1; i < cap; i++)
        {
            float a = Mathf.PI * 0.5f + Mathf.PI * i / cap;
            verts[vi++] = new Vector3(Mathf.Cos(a) * r, 0f, range + Mathf.Sin(a) * r);
        }
        verts[vi++] = new Vector3(r, 0f, range);
        verts[vi++] = new Vector3(r, 0f, 0f);
        for (int i = 1; i < cap; i++)
        {
            float a = Mathf.PI * 1.5f + Mathf.PI * i / cap;
            verts[vi++] = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        }

        var tris = new int[(n - 2) * 3];
        int ti = 0;
        for (int i = 1; i < n - 1; i++)
        {
            tris[ti++] = 0;
            tris[ti++] = i;
            tris[ti++] = i + 1;
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
    }

    private void BuildEllipseMesh(Mesh mesh)
    {
        const int segments = 24;
        var verts = new Vector3[segments + 1];
        var tris = new int[segments * 3];
        verts[0] = Vector3.zero;
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Sin(a) * radius, 0f, Mathf.Cos(a) * range);
        }
        for (int i = 0; i < segments; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = (i + 1) % segments + 1;
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
    }

    void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position + offset;
        Vector3 dir = direction.sqrMagnitude > 0.01f ? direction : transform.forward;
        float drawRange = range > 0.01f ? range : 2f;
        float drawRadius = radius > 0.01f ? radius : 1f;
        HitZoneShape sh = isActive ? _shape : HitZoneShape.BoxCone;

        Gizmos.color = isActive ? new Color(1f, 0f, 0f, 0.4f) : new Color(0f, 0.5f, 1f, 0.3f);

        if (sh == HitZoneShape.Ellipse)
        {
            DrawEllipseGizmo(origin, dir, drawRadius, drawRange);
            return;
        }

        if (sh == HitZoneShape.Capsule)
        {
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            Gizmos.DrawLine(origin + side * drawRadius, origin + dir * drawRange + side * drawRadius);
            Gizmos.DrawLine(origin - side * drawRadius, origin + dir * drawRange - side * drawRadius);
            Gizmos.DrawWireSphere(origin, drawRadius);
            Gizmos.DrawWireSphere(origin + dir * drawRange, drawRadius);
            return;
        }

        if (sh == HitZoneShape.Sector)
        {
            float cone = _activeCone > 0.1f ? _activeCone : coneHalfAngle;
            Vector3 fwd = Quaternion.Euler(0f, _yawOffset, 0f) * dir;
            Vector3 left = Quaternion.Euler(0f, -cone, 0f) * fwd;
            Vector3 right = Quaternion.Euler(0f, cone, 0f) * fwd;
            float inner = _innerRadius > 0f ? _innerRadius : drawRange * 0.24f;
            Gizmos.DrawRay(origin + left * inner, left * (drawRange - inner));
            Gizmos.DrawRay(origin + right * inner, right * (drawRange - inner));
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(origin, fwd * drawRange);
            return;
        }

        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(
            origin + dir * (drawRange * 0.5f),
            Quaternion.LookRotation(dir),
            Vector3.one
        );
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(drawRadius * 2f, height > 0f ? height : 1.5f, drawRange));
        Gizmos.matrix = oldMatrix;

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(origin, Quaternion.Euler(0f, -coneHalfAngle, 0f) * dir * drawRange);
        Gizmos.DrawRay(origin, Quaternion.Euler(0f, coneHalfAngle, 0f) * dir * drawRange);
        Gizmos.DrawRay(origin, dir * drawRange);
    }

    static void DrawEllipseGizmo(Vector3 origin, Vector3 dir, float rx, float rz)
    {
        Quaternion rot = Quaternion.LookRotation(dir);
        Vector3 prev = origin + rot * new Vector3(0f, 0f, rz);
        const int n = 24;
        for (int i = 1; i <= n; i++)
        {
            float a = i / (float)n * Mathf.PI * 2f;
            Vector3 p = origin + rot * new Vector3(Mathf.Sin(a) * rx, 0f, Mathf.Cos(a) * rz);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
}
