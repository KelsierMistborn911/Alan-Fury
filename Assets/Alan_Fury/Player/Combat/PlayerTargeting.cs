using UnityEngine;

/// <summary>
/// Захват и удержание цели игрока: lock, hold, close-switch, cone search, маркер.
/// Вынесено из CombatController3D. Логика не менялась.
/// </summary>
public class PlayerTargeting : MonoBehaviour
{
    [Header("Захват цели")]
    public float targetLockRange = 15f;
    [Tooltip("Дальше этой дистанции лок сбрасывается (перехват на ближайшего в targetLockRange). До неё держится метка.")]
    public float targetHoldRange = 30f;
    [Tooltip("Боевая зона: ближе — автолок, доворот корпуса и удары в цель. Дальше — только метка, всё по мыши.")]
    public float combatFaceRange = 10f;
    [Tooltip("Внутри этого радиуса лок перехватывает тот враг, что ближе к курсору.")]
    public float closeSwitchRange = 6f;
    [Tooltip("На сколько градусов кандидат должен выигрывать у текущей цели, чтобы отобрать лок. Больше — реже мигает.")]
    public float closeSwitchAngleMargin = 20f;
    [Tooltip("Сколько юнитов дистанции стоит 1° отклонения от курсора при автолоке. 0 — чисто ближайший.")]
    public float aimAnglePenalty = 0.1f;
    public LayerMask enemyLayers;
    [Tooltip("Дуга перед игроком (град.), в которой ищутся цели для Tab и автонаведения.")]
    [Range(0f, 360f)] public float aimConeAngle = 200f;

    [Header("Метка цели")]
    [Tooltip("Высота красного маркера над таргетом (м).")]
    public float targetMarkerHeight = 2.2f;
    [Tooltip("Размер маркера (м).")]
    public float targetMarkerSize = 0.35f;
    [ColorUsage(true, true)]
    [Tooltip("HDR-цвет квадрата. Intensity выше 1 даёт блум.")]
    public Color targetMarkerColor = new Color(2.4f, 0.14f, 0.08f, 1f);
    [Tooltip("Доп. множитель яркости в шейдер. 1 = как цвет, 2–4 = сильнее свечение.")]
    public float targetMarkerBloom = 2.5f;

    // --- Публичное состояние ---
    public Transform CurrentTarget { get; private set; }
    /// <summary>Временная цель при заряде, когда NearTarget нет (мягкий авто-aim).</summary>
    public Transform AutoTarget { get; private set; }

    public bool HasTarget => CurrentTarget != null;

    public Transform NearTarget
    {
        get
        {
            if (!IsValidEnemy(CurrentTarget)) return null;
            Vector3 to = CurrentTarget.position - transform.position;
            to.y = 0f;
            return to.sqrMagnitude <= combatFaceRange * combatFaceRange ? CurrentTarget : null;
        }
    }

    // --- Внутреннее ---
    private readonly Collider[] _enemyBuffer = new Collider[32];
    private Transform _shiftSavedTarget;
    private Transform _targetMarker;
    private Material _markerMat;

    void OnDestroy()
    {
        if (_targetMarker != null)
            Destroy(_targetMarker.gameObject);
        if (_markerMat != null)
            Destroy(_markerMat);
    }

    // ---------- API ----------

    public void ClearTarget()
    {
        CurrentTarget = null;
    }

    public void SetTarget(Transform t)
    {
        CurrentTarget = IsValidEnemy(t) ? t : null;
    }

    public void SetAutoTarget(Transform t) => AutoTarget = t;
    public void ClearAutoTarget() => AutoTarget = null;

    public void SaveAndClearForShift()
    {
        _shiftSavedTarget = CurrentTarget;
        ClearTarget();
    }

    public void RestoreAfterShift()
    {
        CurrentTarget = IsValidRestoreTarget(_shiftSavedTarget) ? _shiftSavedTarget : FindNearestInCone();
        _shiftSavedTarget = null;
    }

    public void ToggleOrAcquireByTab()
    {
        if (HasTarget) ClearTarget();
        else if (!Input.GetKey(KeyCode.LeftShift))
            CurrentTarget = FindNearestInCone();
    }

    public void TickLock()
    {
        if (HasTarget) MaintainLockTarget();
    }

    public void TryAcquireIfNeeded()
    {
        TryAcquireCombatTarget();
    }

    public void UpdateMarker()
    {
        UpdateTargetMarker();
    }

    // ---------- Логика (без изменений) ----------

    void MaintainLockTarget()
    {
        Transform latched = FindClingingWolf();
        if (latched != null)
        {
            CurrentTarget = latched;
            return;
        }
        if (!IsValidEnemy(CurrentTarget))
        {
            CurrentTarget = FindNearestInRadius(targetLockRange);
            return;
        }
        TryCloseSwitch();
        Vector3 to = CurrentTarget.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude <= targetHoldRange * targetHoldRange) return;
        CurrentTarget = FindNearestInRadius(targetLockRange);
    }

    void TryAcquireCombatTarget()
    {
        Transform latched = FindClingingWolf();
        if (latched != null)
        {
            CurrentTarget = latched;
            return;
        }
        if (NearTarget != null) return;
        Transform near = FindPreferredTarget(combatFaceRange);
        if (near != null) CurrentTarget = near;
    }

    void TryCloseSwitch()
    {
        if (!IsValidEnemy(CurrentTarget)) return;
        int count = CollectEnemies(closeSwitchRange);
        if (count == 0) return;

        Vector3 mouse = MouseDirection();
        Transform best = null;
        float bestAngle = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (!IsValidEnemy(col.transform)) continue;
            if (col.transform == CurrentTarget) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float angle = Vector3.Angle(mouse, to);
            if (angle < bestAngle) { bestAngle = angle; best = col.transform; }
        }
        if (best == null) return;

        Vector3 cur = CurrentTarget.position - transform.position;
        cur.y = 0f;
        if (bestAngle + closeSwitchAngleMargin < Vector3.Angle(mouse, cur))
            CurrentTarget = best;
    }

    public Transform FindNearestInRadius(float radius)
    {
        int count = CollectEnemies(radius);
        Transform closest = null;
        float minDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float dist = to.sqrMagnitude;
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        return closest;
    }

    public Vector3 GetPreferredAimDirection()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        if ((Mathf.Abs(h) > 0.1f || Mathf.Abs(v) > 0.1f) && Camera.main != null)
        {
            Vector3 forward = Camera.main.transform.forward; forward.y = 0f; forward.Normalize();
            Vector3 right = Camera.main.transform.right; right.y = 0f; right.Normalize();
            Vector3 dir = forward * v + right * h;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }
        return MouseDirection();
    }

    public Vector3 MouseDirection()
    {
        if (Camera.main == null) return transform.forward;
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane ground = new Plane(Vector3.up, transform.position);
        if (ground.Raycast(ray, out float dist))
        {
            Vector3 dir = ray.GetPoint(dist) - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }
        return transform.forward;
    }

    public Transform FindClosestToDirection(Vector3 preferred, float radius)
    {
        int count = CollectEnemies(radius);
        Transform best = null;
        float bestAngle = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;
            float angle = Vector3.Angle(preferred, to);
            if (angle < bestAngle) { bestAngle = angle; best = col.transform; }
        }
        return best;
    }

    public Transform FindPreferredTarget(float radius)
    {
        Vector3 preferred = GetPreferredAimDirection();
        int count = CollectEnemies(radius);
        Transform best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float score = to.magnitude + Vector3.Angle(preferred, to) * aimAnglePenalty;
            if (score < bestScore) { bestScore = score; best = col.transform; }
        }
        return best;
    }

    public Transform FindNearestInCone()
    {
        int count = CollectEnemies(targetLockRange);
        Transform closest = null;
        float minDist = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;
            float dist = to.sqrMagnitude;
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        return closest;
    }

    Transform FindClingingWolf()
    {
        var cling = WerewolfCombat.FindClingingTo(transform);
        if (cling == null) return null;
        return IsValidEnemy(cling.transform) ? cling.transform : null;
    }

    /// <summary>Живой враг: волк, призрак, скелет. Игроков реестра не берём.</summary>
    public bool IsValidEnemy(Transform t)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return false;
        if (t.root == transform.root) return false;
        if (IsRegisteredPlayer(t)) return false;

        var wolf = t.GetComponentInParent<WerewolfStats>();
        if (wolf != null) return wolf.IsAlive;
        var ghost = t.GetComponentInParent<GhostStats>();
        if (ghost != null) return ghost.IsAlive;

        var archer = t.GetComponentInParent<SkeletonArcherBrain>();
        if (archer != null)
            return archer.resources == null || archer.resources.IsAlive;
        var knight = t.GetComponentInParent<SkeletonBrain>();
        if (knight != null)
            return knight.resources == null || knight.resources.IsAlive;

        var dmg = t.GetComponentInParent<IDamageable>();
        if (dmg != null) return dmg.IsAlive;
        return true;
    }

    static bool IsRegisteredPlayer(Transform t)
    {
        if (PlayerRegistry.Instance == null || t == null) return false;
        var players = PlayerRegistry.Instance.Players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p == null) continue;
            if (t == p || t.IsChildOf(p) || p.IsChildOf(t)) return true;
        }
        return false;
    }

    int CollectEnemies(float radius)
    {
        int mask = enemyLayers.value != 0 ? enemyLayers.value : Physics.AllLayers;
        int count = Physics.OverlapSphereNonAlloc(transform.position, radius, _enemyBuffer, mask);
        float r2 = radius * radius;
        AppendSkeleton(ref count, radius, r2);
        AppendDummy(ref count, radius, r2);
        return count;
    }

    void AppendDummy(ref int count, float radius, float r2)
    {
        for (int i = 0; i < TrainingDummyStats.Alive.Count && count < _enemyBuffer.Length; i++)
        {
            var s = TrainingDummyStats.Alive[i];
            if (s == null || !s.isActiveAndEnabled || !s.IsAlive) continue;
            if ((s.transform.position - transform.position).sqrMagnitude > r2) continue;
            var col = ColliderOf(s.transform);
            if (col != null) PushUnique(col, ref count);
        }
    }

    void AppendSkeleton(ref int count, float radius, float r2)
    {
        for (int i = 0; i < SkeletonBrain.Alive.Count && count < _enemyBuffer.Length; i++)
        {
            var b = SkeletonBrain.Alive[i];
            if (b == null || !b.isActiveAndEnabled) continue;
            if ((b.transform.position - transform.position).sqrMagnitude > r2) continue;
            var col = ColliderOf(b.transform);
            if (col != null) PushUnique(col, ref count);
        }
        for (int i = 0; i < SkeletonArcherBrain.Alive.Count && count < _enemyBuffer.Length; i++)
        {
            var b = SkeletonArcherBrain.Alive[i];
            if (b == null || !b.isActiveAndEnabled) continue;
            if ((b.transform.position - transform.position).sqrMagnitude > r2) continue;
            var col = ColliderOf(b.transform);
            if (col != null) PushUnique(col, ref count);
        }
    }

    static Collider ColliderOf(Transform t)
    {
        var col = t.GetComponent<Collider>();
        if (col != null) return col;
        return t.GetComponentInChildren<Collider>();
    }

    void PushUnique(Collider col, ref int count)
    {
        if (col == null) return;
        for (int i = 0; i < count; i++)
            if (_enemyBuffer[i] == col) return;
        _enemyBuffer[count++] = col;
    }

    bool IsValidRestoreTarget(Transform t)
    {
        if (!IsValidEnemy(t)) return false;
        Vector3 to = t.position - transform.position;
        to.y = 0f;
        return to.sqrMagnitude <= targetLockRange * targetLockRange;
    }

    void UpdateTargetMarker()
    {
        Transform shown = CurrentTarget != null ? CurrentTarget : _shiftSavedTarget;
        if (shown == null)
        {
            if (_targetMarker != null) _targetMarker.gameObject.SetActive(false);
            return;
        }

        EnsureTargetMarker();
        ApplyMarkerColor();
        _targetMarker.gameObject.SetActive(true);
        _targetMarker.position = shown.position + Vector3.up * targetMarkerHeight;

        if (Camera.main != null)
            _targetMarker.rotation = Quaternion.LookRotation(Camera.main.transform.forward) * Quaternion.Euler(0f, 0f, 45f);
    }

    void EnsureTargetMarker()
    {
        if (_targetMarker != null) return;

        var go = new GameObject("TargetLockMarker");
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        float half = targetMarkerSize * 0.5f;
        var mesh = new Mesh();
        mesh.vertices = new Vector3[] {
            new Vector3(-half, -half, 0f), new Vector3(half, -half, 0f),
            new Vector3(half, half, 0f), new Vector3(-half, half, 0f)
        };
        mesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateNormals();
        mf.mesh = mesh;

        Shader shader = Shader.Find("Combat/TargetMarker");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        _markerMat = new Material(shader);
        mr.material = _markerMat;
        ApplyMarkerColor();

        _targetMarker = go.transform;
    }

    void ApplyMarkerColor()
    {
        if (_markerMat == null) return;
        Color c = targetMarkerColor;
        float glow = Mathf.Max(0.1f, targetMarkerBloom);
        c.r *= glow;
        c.g *= glow;
        c.b *= glow;
        if (_markerMat.HasProperty("_Color")) _markerMat.SetColor("_Color", c);
        if (_markerMat.HasProperty("_BaseColor")) _markerMat.SetColor("_BaseColor", c);
        _markerMat.color = c;
    }
}
