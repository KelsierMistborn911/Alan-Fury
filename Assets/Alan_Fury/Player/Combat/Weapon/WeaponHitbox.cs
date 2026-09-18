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
    public float coneHalfAngle = 60f;

    [Header("Зона (меш)")]
    [Tooltip("Показывать меш зоны на замахе и во время прохода удара.")]
    public bool showZoneMesh = true;
    [Tooltip("Высота зоны над origin (м).")]
    public float debugZoneY = 0.05f;
    [Tooltip("Толщина кромки прохода (градусы для сектора).")]
    public float sweepBladePad = 8f;

    public Color telegraphColor = new Color(1f, 0.85f, 0.15f, 0.22f);
    public Color sweepColor = new Color(1f, 0.12f, 0.08f, 0.42f);

    [Header("Отладка")]
    public bool debugShowZone = false;

    private Transform _zoneFull;
    private Transform _zoneSweep;
    private Mesh _meshFull;
    private Mesh _meshSweep;
    private Material _matFull;
    private Material _matSweep;

    private float _activeCone;
    private HitZoneShape _shape;
    private float _innerRadius;
    private float _yawOffset;
    private float _sweepSign = 1f;
    private bool _followFacing;

    private bool _telegraphing;
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

    static readonly List<WeaponHitbox> LiveList = new List<WeaponHitbox>(16);
    public static IReadOnlyList<WeaponHitbox> Live => LiveList;

    public bool IsTelegraphing => _telegraphing;
    public bool IsSweeping => isActive;
    public bool IsLive => _telegraphing || isActive;
    public float Sweep01 => !isActive || duration <= 0.0001f ? (isActive ? 1f : 0f) : Mathf.Clamp01(timer / duration);
    public HitZoneShape CurrentShape => _shape;
    public float CurrentRange => range;
    public Vector3 CurrentDirection => direction;
    public Vector3 ZoneOrigin => HitOrigin();

    void Awake()
    {
        if (visual == null)
            visual = GetComponent<SwordAttackVisual>();
    }

    void OnEnable()
    {
        if (!LiveList.Contains(this)) LiveList.Add(this);
    }

    void OnDisable()
    {
        LiveList.Remove(this);
    }

    void OnDestroy()
    {
        LiveList.Remove(this);
        if (_zoneFull != null) Destroy(_zoneFull.gameObject);
        if (_zoneSweep != null) Destroy(_zoneSweep.gameObject);
        if (_matFull != null) Destroy(_matFull);
        if (_matSweep != null) Destroy(_matSweep);
    }

    public void SetHitInfo(HitInfo info)
    {
        _hitInfo = info;
        _hasHitInfo = true;
    }

    /// <summary>Замах: полная зона без урона. Меш следует за корпусом, пока не начался проход.</summary>
    public void ShowTelegraph(float range, float radius, float height, Vector3 offset,
                              Vector3 direction, LayerMask layers,
                              float coneHalfAngleOverride = -1f,
                              HitZoneShape shape = HitZoneShape.BoxCone,
                              float innerRadius = -1f, float yawOffsetDeg = 0f,
                              float sweepSign = 1f)
    {
        ApplyShape(range, radius, height, offset, direction, layers,
            coneHalfAngleOverride, shape, innerRadius, yawOffsetDeg, sweepSign);
        _followFacing = true;
        _telegraphing = true;
        isActive = false;
        timer = 0f;
        lastHitTime.Clear();
        RefreshZoneMeshes(0f, fullOnly: true);
    }

    public void Activate(float range, float radius, float height, Vector3 offset,
                         Vector3 direction, float damage, float stagger,
                         LayerMask layers, float duration, float tickInterval, float chargePercent = 0f,
                         int comboIndex = 0, float coneHalfAngleOverride = -1f,
                         HitZoneShape shape = HitZoneShape.BoxCone, float innerRadius = -1f, float yawOffsetDeg = 0f,
                         float sweepSign = 1f)
    {
        ApplyShape(range, radius, height, offset, direction, layers,
            coneHalfAngleOverride, shape, innerRadius, yawOffsetDeg, sweepSign);
        this.damage = damage;
        this.stagger = stagger;
        this.duration = Mathf.Max(0.02f, duration);
        this.tickInterval = tickInterval;
        this.nextTickTime = 0f;
        _followFacing = false;

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

        _telegraphing = false;
        isActive = true;
        timer = 0f;
        lastHitTime.Clear();

        if (visual != null)
            visual.ShowArc(this.direction, offset, this.duration, chargePercent, comboIndex);

        RefreshZoneMeshes(0f, fullOnly: false);
    }

    public void Deactivate()
    {
        _telegraphing = false;
        isActive = false;
        _hasHitInfo = false;
        _followFacing = false;
        if (visual != null) visual.HideArc();
        HideZoneMeshes();
    }

    void ApplyShape(float range, float radius, float height, Vector3 offset,
                    Vector3 direction, LayerMask layers,
                    float coneHalfAngleOverride, HitZoneShape shape,
                    float innerRadius, float yawOffsetDeg, float sweepSign)
    {
        _activeCone = coneHalfAngleOverride > 0f ? coneHalfAngleOverride : coneHalfAngle;
        _shape = shape;
        _yawOffset = yawOffsetDeg;
        _sweepSign = sweepSign >= 0f ? 1f : -1f;
        this.range = range;
        this.radius = radius;
        this.height = height > 0.2f ? height : 2.2f;
        this.offset = offset;
        this.direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : PlanarForward();
        this.layers = ResolveLayers(layers);
        _innerRadius = innerRadius >= 0f
            ? innerRadius
            : (shape == HitZoneShape.Sector ? this.range * 0.24f : 0f);
    }

    void Update()
    {
        if (_telegraphing)
        {
            if (_followFacing)
                this.direction = PlanarForward();
            RefreshZoneMeshes(0f, fullOnly: true);
            return;
        }

        if (!isActive) return;

        timer += Time.deltaTime;
        bool expired = duration <= 0f || timer >= duration;
        if (Time.time >= nextTickTime || expired)
        {
            DetectHits();
            nextTickTime = Time.time + Mathf.Max(0.02f, tickInterval > 0f ? tickInterval : 0.02f);
        }

        RefreshZoneMeshes(Sweep01, fullOnly: false);

        if (expired)
        {
            isActive = false;
            _hasHitInfo = false;
            if (visual != null) visual.HideArc();
            HideZoneMeshes();
        }
    }

    Vector3 PlanarForward()
    {
        Vector3 f = transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    Vector3 HitOrigin()
    {
        Vector3 dir = direction.sqrMagnitude > 0.01f ? direction : PlanarForward();
        return transform.position + Quaternion.LookRotation(dir) * offset;
    }

    public bool PointInFullZone(Vector3 worldPoint)
    {
        return InsideZone(HitOrigin(), worldPoint, 1f);
    }

    public bool PointInHotZone(Vector3 worldPoint)
    {
        return isActive && InsideZone(HitOrigin(), worldPoint, Sweep01);
    }

    /// <summary>Дистанция до выхода из полной зоны. 0 — уже снаружи. -1 — за maxDist не выходит.</summary>
    public float RayLeave(Vector3 from, Vector3 dir, float maxDist)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return PointInFullZone(from) ? -1f : 0f;
        dir.Normalize();
        if (!PointInFullZone(from)) return 0f;
        const float step = 0.18f;
        float cap = Mathf.Max(step, maxDist);
        for (float t = step; t <= cap; t += step)
        {
            if (!PointInFullZone(from + dir * t))
                return t;
        }
        return -1f;
    }

    public bool RayEnters(Vector3 from, Vector3 dir, float maxDist)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return PointInFullZone(from);
        dir.Normalize();
        if (PointInFullZone(from)) return false;
        const float step = 0.18f;
        float cap = Mathf.Max(step, maxDist);
        for (float t = step; t <= cap; t += step)
        {
            if (PointInFullZone(from + dir * t))
                return true;
        }
        return false;
    }

    void DetectHits()
    {
        if (!isActive) return;

        Vector3 origin = HitOrigin();
        Collider[] colliders = QueryColliders(origin);
        if (colliders == null || colliders.Length == 0)
            colliders = QueryColliders(origin, Physics.AllLayers);

        foreach (Collider col in colliders)
        {
            if (col.transform == transform || col.transform.IsChildOf(transform))
                continue;
            if (!InsideZone(origin, SamplePoint(col.transform), Sweep01)) continue;

            IDamageable damageable = col.GetComponentInParent<IDamageable>();
            if (damageable == null) continue;

            var host = damageable as Component;
            if (host != null && (host.transform == transform || transform.IsChildOf(host.transform)))
                continue;
            if (WasHit(host != null ? host.gameObject : col.gameObject)) continue;
            if (IsDodgeInvulnerable(host != null ? host.transform : col.transform)) continue;

            ApplyHit(damageable, col.transform);
        }

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
        if (!InsideZone(origin, SamplePoint(target), Sweep01)) return;

        var damageable = target.GetComponentInParent<IDamageable>();
        if (damageable == null) return;
        var host = damageable as Component;
        if (host != null && (host.transform == transform || transform.IsChildOf(host.transform)))
            return;
        if (WasHit(host != null ? host.gameObject : target.gameObject)) return;
        if (IsDodgeInvulnerable(host != null ? host.transform : target)) return;

        ApplyHit(damageable, target);
    }

    static bool IsDodgeInvulnerable(Transform t)
    {
        if (t == null) return false;
        var loco = t.GetComponentInParent<HumanoidLocomotion>();
        return loco != null && loco.IsDodgeInvulnerable;
    }

    static Vector3 SamplePoint(Transform t)
    {
        if (t == null) return Vector3.zero;
        var col = t.GetComponentInChildren<Collider>();
        if (col != null) return col.bounds.center;
        return t.position + Vector3.up * 0.9f;
    }

    bool WasHit(GameObject key)
    {
        if (key == null) return false;
        return lastHitTime.ContainsKey(key);
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
        float impulse = stagger;
        bool closeHold = hit.isInfight || hit.band <= CombatRange.Clinch;
        bool strongHit = hit.isHeavy && hit.chargePercent >= 0.55f;
        bool strongImpulse = hit.stagger > 5.5f;
        if (closeHold && !(strongHit && strongImpulse))
            impulse *= 0.08f;
        damageable.ApplyKnockback(knockback * impulse);

        var host = damageable as Component;
        lastHitTime[host != null ? host.gameObject : target.gameObject] = Time.time;
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

    bool InsideZone(Vector3 origin, Vector3 target, float sweep)
    {
        if (Mathf.Abs(target.y - origin.y) > Mathf.Max(height, 2.2f) + 2f)
            return false;

        Vector3 flat = target - origin;
        flat.y = 0f;
        float dist = flat.magnitude;
        float u = Mathf.Clamp01(sweep);

        switch (_shape)
        {
            case HitZoneShape.Capsule:
            {
                float len = Mathf.Max(0.12f, range * Mathf.Max(u, 0.02f));
                Vector3 end = origin + direction * len;
                return PointToSegment(target, origin, end) <= radius;
            }
            case HitZoneShape.Ellipse:
            {
                if (dist < 0.0001f) return u > 0.02f;
                Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(direction));
                Vector3 local = inv * flat;
                float nx = radius > 0.01f ? local.x / radius : local.x;
                float nz = range > 0.01f ? local.z / range : local.z;
                float reach = Mathf.Max(0.08f, u);
                return nx * nx + nz * nz <= reach * reach;
            }
            case HitZoneShape.Sector:
            {
                if (dist > range + 0.15f) return false;
                if (dist < 0.0001f) return true;
                Vector3 fwd = Quaternion.Euler(0f, _yawOffset, 0f) * direction;
                float ang = Vector3.SignedAngle(fwd, flat, Vector3.up);
                return AngleInSweep(ang, u);
            }
            default:
            {
                if (dist > range + 0.01f) return false;
                if (dist < 0.0001f) return true;
                float ang = Vector3.SignedAngle(direction, flat, Vector3.up);
                return AngleInSweep(ang, u);
            }
        }
    }

    bool AngleInSweep(float signedAngle, float u)
    {
        float pad = Mathf.Max(2f, sweepBladePad);
        float a0 = -_activeCone;
        float a1 = _activeCone;
        if (_sweepSign >= 0f)
        {
            float front = Mathf.Lerp(a0, a1, u);
            return signedAngle >= a0 - 0.5f && signedAngle <= front + pad;
        }
        else
        {
            float front = Mathf.Lerp(a1, a0, u);
            return signedAngle <= a1 + 0.5f && signedAngle >= front - pad;
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

    void EnsureZoneMeshes()
    {
        if (_zoneFull == null)
            CreateZone("HitZoneFull", telegraphColor, out _zoneFull, out _meshFull, out _matFull);
        if (_zoneSweep == null)
            CreateZone("HitZoneSweep", sweepColor, out _zoneSweep, out _meshSweep, out _matSweep);
    }

    void CreateZone(string name, Color color, out Transform root, out Mesh mesh, out Material mat)
    {
        var go = new GameObject(name);
        mesh = new Mesh { name = name };
        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mat = MakeZoneMat(color);
        mr.material = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        root = go.transform;
    }

    static Material MakeZoneMat(Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        var mat = new Material(sh);
        mat.SetFloat("_Surface", 1f);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        mat.color = color;
        return mat;
    }

    void RefreshZoneMeshes(float sweep, bool fullOnly)
    {
        if (!showZoneMesh && !debugShowZone)
        {
            HideZoneMeshes();
            return;
        }

        EnsureZoneMeshes();
        Vector3 origin = HitOrigin() + Vector3.up * debugZoneY;
        Vector3 dir = direction.sqrMagnitude > 0.01f ? direction : PlanarForward();
        Quaternion rot = Quaternion.LookRotation(dir);

        BuildZoneMesh(_meshFull, 1f);
        _zoneFull.SetPositionAndRotation(origin, rot);
        _zoneFull.localScale = Vector3.one;
        if (_matFull != null) _matFull.color = telegraphColor;
        if (!_zoneFull.gameObject.activeSelf) _zoneFull.gameObject.SetActive(true);

        if (fullOnly)
        {
            if (_zoneSweep.gameObject.activeSelf) _zoneSweep.gameObject.SetActive(false);
            return;
        }

        BuildZoneMesh(_meshSweep, Mathf.Clamp01(sweep));
        _zoneSweep.SetPositionAndRotation(origin, rot);
        _zoneSweep.localScale = Vector3.one;
        if (_matSweep != null) _matSweep.color = sweepColor;
        bool showSweep = sweep > 0.001f;
        if (_zoneSweep.gameObject.activeSelf != showSweep)
            _zoneSweep.gameObject.SetActive(showSweep);
    }

    void HideZoneMeshes()
    {
        if (_zoneFull != null) _zoneFull.gameObject.SetActive(false);
        if (_zoneSweep != null) _zoneSweep.gameObject.SetActive(false);
    }

    void BuildZoneMesh(Mesh mesh, float sweep)
    {
        switch (_shape)
        {
            case HitZoneShape.Sector:
                BuildSectorMesh(mesh, sweep);
                break;
            case HitZoneShape.Capsule:
                BuildCapsuleMesh(mesh, sweep);
                break;
            case HitZoneShape.Ellipse:
                BuildEllipseMesh(mesh, sweep);
                break;
            default:
                BuildBoxConeMesh(mesh, sweep);
                break;
        }
    }

    void BuildBoxConeMesh(Mesh mesh, float sweep)
    {
        const int segments = 8;
        float tanA = Mathf.Tan(Mathf.Clamp(_activeCone, 1f, 89f) * Mathf.Deg2Rad);
        float reach = range * Mathf.Max(0.04f, sweep);

        var verts = new Vector3[(segments + 1) * 2];
        var tris = new int[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float d = reach * i / segments;
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

    void BuildSectorMesh(Mesh mesh, float sweep)
    {
        const int segments = 16;
        float inner = Mathf.Min(_innerRadius, range * 0.95f);
        float a0 = (_yawOffset - _activeCone) * Mathf.Deg2Rad;
        float a1 = (_yawOffset + _activeCone) * Mathf.Deg2Rad;
        float from = _sweepSign >= 0f ? a0 : a1;
        float to = Mathf.Lerp(from, _sweepSign >= 0f ? a1 : a0, Mathf.Clamp01(sweep));

        var verts = new Vector3[(segments + 1) * 2];
        var tris = new int[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float a = Mathf.Lerp(from, to, i / (float)segments);
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

    void BuildCapsuleMesh(Mesh mesh, float sweep)
    {
        const int cap = 8;
        float r = Mathf.Max(0.05f, radius);
        float len = range * Mathf.Max(0.04f, sweep);
        int n = cap * 2 + 4;
        var verts = new Vector3[n];
        int vi = 0;
        verts[vi++] = new Vector3(-r, 0f, 0f);
        verts[vi++] = new Vector3(-r, 0f, len);
        for (int i = 1; i < cap; i++)
        {
            float a = Mathf.PI * 0.5f + Mathf.PI * i / cap;
            verts[vi++] = new Vector3(Mathf.Cos(a) * r, 0f, len + Mathf.Sin(a) * r);
        }
        verts[vi++] = new Vector3(r, 0f, len);
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

    void BuildEllipseMesh(Mesh mesh, float sweep)
    {
        const int segments = 24;
        float s = Mathf.Max(0.06f, sweep);
        var verts = new Vector3[segments + 1];
        var tris = new int[segments * 3];
        verts[0] = Vector3.zero;
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Sin(a) * radius * s, 0f, Mathf.Cos(a) * range * s);
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
        HitZoneShape sh = (isActive || _telegraphing) ? _shape : HitZoneShape.BoxCone;

        Gizmos.color = isActive ? new Color(1f, 0f, 0f, 0.4f) : new Color(1f, 0.8f, 0.1f, 0.3f);

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
