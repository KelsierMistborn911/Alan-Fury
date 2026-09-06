using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Зрение / слух / заметность NPC. Один компонент, опции на префабе.
/// LOS как у игрока: клетки MapGrid Full / MaxHeight (+ Tree = Full).
/// </summary>
public class NpcPerception : MonoBehaviour
{
    public enum Profile { Custom, Wolf, Humanoid, GhostHost, GhostScout }

    [Header("Профиль")]
    public Profile profile = Profile.Custom;

    [Header("Сенсоры")]
    public bool useSight = true;
    [Tooltip("Игнорировать конус — полный круг.")]
    public bool omnidirectional = false;
    [Tooltip("Резать луч клетками MapGrid (как PlayerVision).")]
    public bool useCover = true;
    [Tooltip("Дополнительный Physics.Raycast. По умолчанию выкл — деревья уже на сетке.")]
    public bool usePhysicsLos = false;
    public bool useHearing = true;
    public bool useNotice = true;
    [Tooltip("Туман AlphaStalker. Волку да, призраку нет.")]
    public bool useFogConceal = true;
    public bool trackPlayerCombat = true;
    public bool trackIfPlayerSeesMe = true;

    [Header("Цель")]
    public Transform player;

    [Header("Зрение")]
    [FormerlySerializedAs("wolfSightRange")]
    public float sightRange = 24f;
    [FormerlySerializedAs("wolfViewHalfAngle")]
    [Range(0f, 180f)] public float viewHalfAngle = 70f;
    [FormerlySerializedAs("selfEyeOffset")]
    public Vector3 eyeOffset = new Vector3(0f, 1.0f, 0f);
    public Vector3 playerEyeOffset = new Vector3(0f, 1.2f, 0f);
    public LayerMask sightBlockers = ~0;
    public MapGrid mapGrid;

    [Header("Игрок смотрит на меня")]
    [Range(0f, 180f)] public float viewAngleThreshold = 35f;
    public float playerSightRange = 35f;

    [Header("Слух")]
    public float hearRangeAtNoise1 = 22f;

    [Header("Заметность / след")]
    public float noticeFillPerSecond = 0.35f;
    public float noticeDecayPerSecond = 0.22f;
    public float cueLifetime = 8f;
    public float hearUncertainty = 7f;
    public float sightUncertainty = 1.2f;

    [Header("Гизмо")]
    public bool drawGizmos = true;
    [Range(16, 96)] public int gizmoSegments = 48;
    public float gizmoY = 0.12f;
    public Color fieldColor = new Color(0.45f, 0.85f, 1f, 0.22f);
    public Color fieldEdge = new Color(0.55f, 0.95f, 1f, 0.85f);
    public Color hostFieldColor = new Color(0.85f, 0.45f, 1f, 0.16f);
    public Color hostFieldEdge = new Color(0.95f, 0.55f, 1f, 0.9f);
    public Color seenColor = new Color(1f, 0.25f, 0.2f, 0.28f);

    public float Notice01 { get; private set; }
    public bool IsLocked { get; private set; }
    public bool HasCue => _cueValid;
    public Vector3 CuePos => _cuePos;
    public float CueRadius => _cueRadius;
    public float CueAge => _cueAge;

    public bool SeesPlayer { get; private set; }
    public Transform Seen { get; private set; }
    public Transform Heard { get; private set; }

    public bool HasPlayer => player != null;
    public Vector3 PlayerPos => player != null ? player.position : transform.position;
    public bool IsOmni => omnidirectional || viewHalfAngle >= 179.5f;
    public Vector3 Eye => transform.position + eyeOffset;

    public bool PlayerIsCharging => _playerCombat != null && _playerCombat.IsCharging;
    public bool PlayerIsAttacking => _playerCombat != null && _playerCombat.IsAttacking;
    public bool PlayerIsWindingUp => _playerCombat != null && _playerCombat.IsWindingUp;
    public bool PlayerThreatActive =>
        _playerCombat != null && _playerCombat.IsInAttackPipeline;

    public float PlayerWeaponRange
    {
        get
        {
            var w = _playerLoadout != null ? _playerLoadout.GetMainWeapon() : null;
            return w != null ? w.Reach() : CombatRangeTable.Default.Outer(CombatRange.Mid);
        }
    }

    public float PlayerWeaponConeHalfAngle => _playerHitbox != null ? _playerHitbox.coneHalfAngle : 60f;

    public float AngleFromPlayerGaze
    {
        get
        {
            if (!HasPlayer) return 180f;
            Vector3 toSelf = transform.position - player.position; toSelf.y = 0f;
            Vector3 fwd = player.forward; fwd.y = 0f;
            if (toSelf.sqrMagnitude < 0.0001f || fwd.sqrMagnitude < 0.0001f) return 180f;
            return Vector3.Angle(fwd, toSelf);
        }
    }

    public float DistanceToPlayer
    {
        get
        {
            if (player == null) return Mathf.Infinity;
            return FlatDist(transform.position, player.position);
        }
    }

    public Vector3 DirFromPlayerFlat
    {
        get
        {
            if (player == null) return Vector3.forward;
            Vector3 d = transform.position - player.position;
            d.y = 0f;
            return d.sqrMagnitude < 0.0001f ? Vector3.forward : d.normalized;
        }
    }

    public Vector3 PlayerForwardFlat
    {
        get
        {
            if (player == null) return Vector3.forward;
            Vector3 f = player.forward;
            f.y = 0f;
            return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        }
    }

    public bool PlayerLookingAtMe
    {
        get
        {
            if (!trackIfPlayerSeesMe || player == null) return false;
            Vector3 fwd = player.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return false;
            return Vector3.Angle(fwd, DirFromPlayerFlat) < viewAngleThreshold;
        }
    }

    private WerewolfAlphaStalker _stalker;
    private HumanoidCombat _playerCombat;
    private PlayerLoadout _playerLoadout;
    private WeaponHitbox _playerHitbox;

    private bool _cueValid;
    private Vector3 _cuePos;
    private float _cueRadius = 1f;
    private float _cueAge;

    void Awake()
    {
        if (profile == Profile.GhostHost) ApplyHost();
        else if (profile == Profile.GhostScout) ApplyScout();
        else if (profile == Profile.Humanoid) ApplyHumanoid();
        else if (profile == Profile.Wolf) ApplyWolf();

        EnsureMapGrid();
        ResolvePlayer();
        _stalker = GetComponent<WerewolfAlphaStalker>();
    }

    void Start()
    {
        EnsureMapGrid();
    }

    public void EnsureMapGrid()
    {
        if (mapGrid != null && mapGrid.IsReady) return;
        if (mapGrid != null && !mapGrid) mapGrid = null;
        var found = FindObjectOfType<MapGrid>();
        if (found != null) mapGrid = found;
    }

    public void ApplyWolf()
    {
        profile = Profile.Wolf;
        useSight = true;
        omnidirectional = false;
        useCover = true;
        usePhysicsLos = false;
        useHearing = true;
        useNotice = true;
        useFogConceal = true;
        trackPlayerCombat = true;
        trackIfPlayerSeesMe = true;
        if (sightRange < 1f) sightRange = 24f;
        if (viewHalfAngle < 1f) viewHalfAngle = 70f;
        eyeOffset = new Vector3(0f, 1.0f, 0f);
        drawGizmos = true;
    }

    public void ApplyHumanoid()
    {
        profile = Profile.Humanoid;
        useSight = true;
        omnidirectional = false;
        useCover = true;
        usePhysicsLos = false;
        useHearing = false;
        useNotice = false;
        useFogConceal = false;
        trackPlayerCombat = false;
        trackIfPlayerSeesMe = false;
        sightRange = 16f;
        viewHalfAngle = 55f;
        eyeOffset = new Vector3(0f, 1.6f, 0f);
        drawGizmos = true;
    }

    public void ApplyHost()
    {
        profile = Profile.GhostHost;
        useSight = true;
        omnidirectional = true;
        viewHalfAngle = 180f;
        if (sightRange < 16f) sightRange = 18f;
        eyeOffset = new Vector3(0f, 1.2f, 0f);
        useCover = true;
        usePhysicsLos = false;
        useHearing = false;
        useNotice = false;
        useFogConceal = false;
        trackPlayerCombat = false;
        trackIfPlayerSeesMe = false;
        drawGizmos = true;
    }

    public void ApplyScout()
    {
        profile = Profile.GhostScout;
        useSight = true;
        omnidirectional = false;
        if (viewHalfAngle >= 179.5f) viewHalfAngle = 55f;
        if (sightRange > 14f) sightRange = 8f;
        eyeOffset = new Vector3(0f, 0.6f, 0f);
        useCover = true;
        usePhysicsLos = false;
        useHearing = false;
        useNotice = false;
        useFogConceal = false;
        trackPlayerCombat = false;
        trackIfPlayerSeesMe = false;
        drawGizmos = true;
    }

    void Update()
    {
        if (player == null)
            ResolvePlayer();
        else if (PlayerRegistry.Instance != null && PlayerRegistry.Instance.Count > 0)
        {
            var nearest = PlayerRegistry.Instance.GetNearest(transform.position);
            if (nearest != null && nearest != player)
                BindPlayer(nearest);
        }

        TickSenses(Time.deltaTime);
        if (drawGizmos && useSight)
            DrawSight((a, b, c) => Debug.DrawLine(a, b, c));
    }

    void ResolvePlayer()
    {
        Transform next = null;
        if (PlayerRegistry.Instance != null && PlayerRegistry.Instance.Count > 0)
            next = PlayerRegistry.Instance.GetNearest(transform.position);
        if (next == null)
            next = PlayerRegistry.ResolvePrimary();
        if (next != null)
            BindPlayer(next);
    }

    void BindPlayer(Transform t)
    {
        player = t;
        if (!trackPlayerCombat)
        {
            _playerCombat = null;
            _playerLoadout = null;
            _playerHitbox = null;
            return;
        }
        _playerCombat = t != null ? t.GetComponent<HumanoidCombat>() : null;
        _playerLoadout = t != null ? t.GetComponent<PlayerLoadout>() : null;
        _playerHitbox = t != null ? t.GetComponentInChildren<WeaponHitbox>() : null;
    }

    public bool HasLineOfSightToPlayer()
    {
        if (player == null) return false;
        return HasLineOfSight(player);
    }

    public bool HasLineOfSight(Transform target)
    {
        if (target == null) return false;
        Vector3 a = Eye;
        Vector3 b = target.position + playerEyeOffset;

        if (useFogConceal && _stalker != null &&
            _stalker.IsConcealedAt(target.position, transform.position))
            return false;

        if (useCover)
        {
            EnsureMapGrid();
            if (mapGrid != null && mapGrid.IsReady && !mapGrid.HasSightLos(a, b))
                return false;
        }

        if (usePhysicsLos)
        {
            Vector3 to = b - a;
            float d = to.magnitude;
            if (d >= 0.05f &&
                Physics.Raycast(a, to / d, out RaycastHit hit, d, sightBlockers, QueryTriggerInteraction.Ignore))
            {
                Transform h = hit.transform;
                if (h != target && !h.IsChildOf(target) && h != transform && !h.IsChildOf(transform))
                    return false;
            }
        }

        return true;
    }

    public bool IsSeenByPlayer()
    {
        if (!trackIfPlayerSeesMe || player == null) return false;
        if (DistanceToPlayer > playerSightRange) return false;
        if (!PlayerLookingAtMe) return false;
        if (useFogConceal && _stalker != null &&
            _stalker.IsConcealedAt(transform.position, player.position))
            return false;
        return HasLineOfSightToPlayer();
    }

    public bool CanSee(Transform target) => CanSeeTarget(target);

    public bool CanSeeTarget(Transform target)
    {
        if (!useSight || target == null) return false;
        Vector3 to = target.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist > sightRange) return false;
        if (dist > 0.05f && !IsOmni)
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return false;
            if (Vector3.Angle(fwd, to) > viewHalfAngle) return false;
        }

        return HasLineOfSight(target);
    }

    public void ReportCue(Vector3 worldPos, float radius)
    {
        if (!useNotice) return;
        _cuePos = worldPos;
        _cueRadius = Mathf.Max(0.5f, radius);
        _cueAge = 0f;
        _cueValid = true;
    }

    public void ClearCue()
    {
        _cueValid = false;
        _cueAge = 0f;
        IsLocked = false;
    }

    void TickSenses(float dt)
    {
        SeesPlayer = false;
        Seen = null;
        Heard = null;

        var reg = PlayerRegistry.Instance;
        Transform seen = null;
        Transform heard = null;
        float heardDist = float.MaxValue;
        float heardNoise = 0f;

        if (reg != null)
        {
            for (int i = 0; i < reg.Count; i++)
            {
                Transform t = reg.Players[i];
                if (t == null) continue;
                if (seen == null && CanSeeTarget(t))
                    seen = t;

                if (!useHearing) continue;
                float dist = FlatDist(transform.position, t.position);
                ReadStealth(t, out _, out float noise);
                float range = hearRangeAtNoise1 * Mathf.Max(0f, noise);
                if (range > 0.01f && dist <= range && dist < heardDist)
                {
                    heard = t;
                    heardDist = dist;
                    heardNoise = noise;
                }
            }
        }

        if (seen == null && (reg == null || reg.Count == 0))
        {
            var fallback = FindObjectOfType<PlayerMovement3D>();
            if (fallback != null && CanSeeTarget(fallback.transform))
                seen = fallback.transform;
        }

        Seen = seen;
        Heard = heard;
        SeesPlayer = seen != null;

        if (!useNotice)
        {
            Notice01 = 0f;
            IsLocked = false;
            return;
        }

        if (seen != null)
        {
            if (player != seen) BindPlayer(seen);
            ReadStealth(seen, out float notice, out _);
            float dist = FlatDist(transform.position, seen.position);
            float near = 1f - Mathf.Clamp01(dist / Mathf.Max(0.01f, sightRange));
            Notice01 = Mathf.Min(1f, Notice01 + noticeFillPerSecond * notice * Mathf.Lerp(0.35f, 1f, near) * dt);
            ReportCue(seen.position, sightUncertainty);
            if (Notice01 >= 1f) IsLocked = true;
        }
        else
        {
            Notice01 = Mathf.Max(0f, Notice01 - noticeDecayPerSecond * dt);
            if (heard != null)
            {
                float range = hearRangeAtNoise1 * Mathf.Max(0.01f, heardNoise);
                float u = Mathf.Max(2f, hearUncertainty * Mathf.Clamp01(heardDist / range));
                if (!_cueValid || FlatDist(_cuePos, heard.position) > u + 3f)
                {
                    Vector3 j = heard.position - transform.position; j.y = 0f;
                    if (j.sqrMagnitude < 0.01f) j = transform.forward;
                    j.Normalize();
                    Vector3 side = Vector3.Cross(Vector3.up, j);
                    Vector3 guess = heard.position + side * Random.Range(-u * 0.5f, u * 0.5f);
                    ReportCue(guess, u);
                }
                else
                    _cueAge = 0f;
            }
        }

        if (_cueValid)
        {
            _cueAge += dt;
            if (_cueAge > cueLifetime)
                ClearCue();
        }
        else
        {
            IsLocked = false;
        }
    }

    public static void ReadStealth(Transform t, out float notice, out float noise)
    {
        notice = 1f;
        noise = 1f;
        if (t == null) return;
        var mb = t.GetComponent("PlayerStealth") as MonoBehaviour;
        if (mb == null) return;
        var tp = mb.GetType();
        var pn = tp.GetProperty("Noticeability");
        var cn = tp.GetProperty("CurrentNoise");
        if (pn != null && pn.GetValue(mb, null) is float nf) notice = nf;
        if (cn != null && cn.GetValue(mb, null) is float cf) noise = cf;
    }

    static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    delegate void LineDraw(Vector3 a, Vector3 b, Color color);

    void DrawSight(LineDraw line)
    {
        Vector3 origin = transform.position;
        origin.y += gizmoY;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        else fwd.Normalize();

        bool host = profile == Profile.GhostHost;
        Color fill = SeesPlayer ? seenColor : (host ? hostFieldColor : fieldColor);
        Color edge = SeesPlayer
            ? new Color(1f, 0.35f, 0.25f, 0.95f)
            : (host ? hostFieldEdge : fieldEdge);
        fill.a = 1f;
        edge.a = 1f;

        int n = Mathf.Max(12, gizmoSegments);
        float half = IsOmni ? 180f : viewHalfAngle;
        Vector3 fanFwd = IsOmni ? Vector3.forward : fwd;
        float span = half * 2f;
        Vector3 prev = origin + Quaternion.Euler(0f, -half, 0f) * fanFwd * sightRange;
        for (int i = 0; i <= n; i++)
        {
            float ang = -half + span * (i / (float)n);
            Vector3 tip = origin + Quaternion.Euler(0f, ang, 0f) * fanFwd * sightRange;
            line(origin, tip, fill);
            if (i > 0) line(prev, tip, edge);
            prev = tip;
        }
        if (!IsOmni)
        {
            line(origin, origin + Quaternion.Euler(0f, -half, 0f) * fwd * sightRange, edge);
            line(origin, origin + Quaternion.Euler(0f, half, 0f) * fwd * sightRange, edge);
        }
        line(origin, origin + Vector3.up * eyeOffset.y, edge);
        if (Seen != null)
            line(Eye, Seen.position + playerEyeOffset, edge);

        MapGrid grid = mapGrid;
        if (grid == null || !grid) grid = FindObjectOfType<MapGrid>();
        if (grid == null || !grid.IsReady) return;
        mapGrid = grid;
        float ts = grid.TileSize;
        if (ts < 0.01f) return;

        int rad = Mathf.CeilToInt(sightRange / ts) + 1;
        grid.WorldToCell(transform.position, out int ocx, out int ocz);
        Vector3 eye = transform.position + eyeOffset;
        float cellHalf = ts * 0.48f;
        float y = transform.position.y + 0.35f;

        for (int dx = -rad; dx <= rad; dx++)
        {
            for (int dz = -rad; dz <= rad; dz++)
            {
                int cx = ocx + dx;
                int cz = ocz + dz;
                if (!grid.InBounds(cx, cz)) continue;
                Vector3 c = grid.CellCenterWorld(cx, cz);
                Vector3 flat = c - transform.position; flat.y = 0f;
                if (flat.magnitude > sightRange) continue;
                if (!IsOmni && flat.sqrMagnitude > 0.0025f && Vector3.Angle(fwd, flat) > viewHalfAngle)
                    continue;

                bool los = grid.HasSightLos(eye, c + Vector3.up * playerEyeOffset.y);
                Color col = los
                    ? new Color(0.1f, 0.95f, 1f, 1f)
                    : new Color(1f, 0.2f, 0.85f, 1f);
                float x0 = c.x - cellHalf, x1 = c.x + cellHalf;
                float z0 = c.z - cellHalf, z1 = c.z + cellHalf;
                line(new Vector3(x0, y, z0), new Vector3(x1, y, z0), col);
                line(new Vector3(x1, y, z0), new Vector3(x1, y, z1), col);
                line(new Vector3(x1, y, z1), new Vector3(x0, y, z1), col);
                line(new Vector3(x0, y, z1), new Vector3(x0, y, z0), col);
            }
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawGizmos || !useSight) return;
        DrawSight((a, b, c) => { Gizmos.color = c; Gizmos.DrawLine(a, b); });
    }
#endif
}
