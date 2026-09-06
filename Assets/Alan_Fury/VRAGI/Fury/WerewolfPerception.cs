using UnityEngine;

/// <summary>
/// Восприятие оборотнем игрока. Считает дистанцию, направление,
/// смотрит ли игрок на оборотня и есть ли между ними прямая видимость.
/// Только данные — решения принимают мозг/сталкер.
/// Учитывает укрытия (туман): данные об укрытиях живут в AlphaStalker.
/// </summary>
public class WerewolfPerception : MonoBehaviour
{
    [Header("Игрок (текущая цель)")]
    [Tooltip("Не единственный герой мира — выбранная цель из PlayerRegistry (обычно nearest).")]
    public Transform player;

    [Header("Угол обзора игрока")]
    [Tooltip("Половина конуса обзора (град). В пределах него игрок считается смотрящим на цель.")]
    [Range(0f, 180f)] public float viewAngleThreshold = 35f;

    [Header("Линия видимости")]
    [Tooltip("Слои, перекрывающие обзор: рельеф, объекты-укрытия.")]
    public LayerMask sightBlockers = ~0;
    [Tooltip("Точка «глаз» оборотня относительно его origin.")]
    public Vector3 selfEyeOffset = new Vector3(0f, 1.0f, 0f);
    [Tooltip("Точка «глаз» игрока относительно его origin.")]
    public Vector3 playerEyeOffset = new Vector3(0f, 1.2f, 0f);
    [Tooltip("Дальше этого игрок не замечает оборотня даже в прямой видимости (м).")]
    public float playerSightRange = 35f;

    [Header("Зрение волка")]
    [Tooltip("Полуугол конуса зрения волка (град).")]
    [Range(0f, 180f)] public float wolfViewHalfAngle = 70f;
    [Tooltip("Дальность зрения волка (м).")]
    public float wolfSightRange = 24f;
    [Tooltip("Сетка клеток для гизмо зрения. Пусто — FindObjectOfType.")]
    public MapGrid mapGrid;
    [Tooltip("Рисовать клетки конуса, когда объект выбран.")]
    public bool drawVisionCells = true;

    [Header("Слух волка")]
    [Tooltip("Радиус слуха при CurrentNoise = 1 (м). Множится на шум цели.")]
    public float hearRangeAtNoise1 = 22f;

    [Header("Шкала заметности (только зрение, знак над волком)")]
    [Tooltip("Прирост Notice01 в секунду при Noticeability = 1 в упор.")]
    public float noticeFillPerSecond = 0.35f;
    [Tooltip("Спад Notice01 в секунду вне зрения.")]
    public float noticeDecayPerSecond = 0.22f;

    [Header("След")]
    [Tooltip("След умирает, если столько секунд не было зрения/слуха/броска.")]
    public float cueLifetime = 8f;
    [Tooltip("Радиус диска следа при слухе на краю слышимости (м).")]
    public float hearUncertainty = 7f;
    [Tooltip("Радиус диска следа при зрительном контакте (м).")]
    public float sightUncertainty = 1.2f;

    /// <summary>0..1. Только зрение. Для знака. Вне конуса тает.</summary>
    public float Notice01 { get; private set; }

    /// <summary>Полный зрительный контакт (шкала дошла до 1), пока жив след.</summary>
    public bool IsLocked { get; private set; }

    public bool HasCue => _cueValid;
    public Vector3 CuePos => _cuePos;
    public float CueRadius => _cueRadius;
    public float CueAge => _cueAge;

    public bool HasPlayer => player != null;
    public Vector3 PlayerPos => player.position;

    /// <summary>Игрок заряжает удар (замах удержанием).</summary>
    public bool PlayerIsCharging => _playerCombat != null && _playerCombat.IsCharging;
    /// <summary>Игрок в активной фазе удара.</summary>
    public bool PlayerIsAttacking => _playerCombat != null && _playerCombat.IsAttacking;
    /// <summary>Игрок в windup обычного/любого удара (телеграф до active-фазы).</summary>
    public bool PlayerIsWindingUp => _playerCombat != null && _playerCombat.IsWindingUp;
    /// <summary>Игрок замахивается или уже бьёт — угроза для уворота.</summary>
    public bool PlayerThreatActive =>
        _playerCombat != null && _playerCombat.IsInAttackPipeline;

    /// <summary>Дальность удара текущего оружия игрока (м); 2, если оружие не найдено.</summary>
    public float PlayerWeaponRange
    {
        get
        {
            var w = _playerLoadout != null ? _playerLoadout.GetMainWeapon() : null;
            return w != null ? w.Reach() : CombatRangeTable.Default.Outer(CombatRange.Mid);
        }
    }

    /// <summary>Полуконус удара оружия игрока (град, из его WeaponHitbox); 60, если хитбокс не найден.</summary>
    public float PlayerWeaponConeHalfAngle => _playerHitbox != null ? _playerHitbox.coneHalfAngle : 60f;

    /// <summary>Угол (град) между взглядом игрока и направлением на этого волка. 0 = игрок смотрит прямо на волка.</summary>
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

    private WerewolfAlphaStalker _stalker; // источник данных об укрытиях (может отсутствовать)
    private CombatController3D _playerCombat;
    private PlayerLoadout _playerLoadout;
    private WeaponHitbox _playerHitbox;

    private bool _cueValid;
    private Vector3 _cuePos;
    private float _cueRadius = 1f;
    private float _cueAge;

    void Awake()
    {
        ResolvePlayer();
        _stalker = GetComponent<WerewolfAlphaStalker>();
    }

    void Update()
    {
        // Игрок появляется после Host-спавна — подхватываем из реестра, если ещё null / сменился.
        if (player == null)
            ResolvePlayer();
        else if (PlayerRegistry.Instance != null && PlayerRegistry.Instance.Count > 0)
        {
            var nearest = PlayerRegistry.Instance.GetNearest(transform.position);
            if (nearest != null && nearest != player)
                BindPlayer(nearest);
        }

        TickSenses(Time.deltaTime);
        if (drawVisionCells)
            DrawVisionDebug();
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
        _playerCombat = t != null ? t.GetComponent<CombatController3D>() : null;
        _playerLoadout = t != null ? t.GetComponent<PlayerLoadout>() : null;
        _playerHitbox = t != null ? t.GetComponentInChildren<WeaponHitbox>() : null;
    }

    /// <summary>Горизонтальная дистанция до игрока (Y игнорируется).</summary>
    public float DistanceToPlayer
    {
        get
        {
            if (player == null) return Mathf.Infinity;
            Vector3 d = player.position - transform.position;
            d.y = 0f;
            return d.magnitude;
        }
    }

    /// <summary>Горизонтальное направление ОТ игрока К оборотню (нормализованное).</summary>
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

    /// <summary>Плоское направление взгляда игрока (нормализованное).</summary>
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

    /// <summary>Повёрнут ли игрок в сторону оборотня (только угол, без укрытий).</summary>
    public bool PlayerLookingAtMe
    {
        get
        {
            if (player == null) return false;
            Vector3 fwd = player.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return false;
            fwd.Normalize();
            float angle = Vector3.Angle(fwd, DirFromPlayerFlat);
            return angle < viewAngleThreshold;
        }
    }

    /// <summary>Чистая ли прямая видимость между оборотнем и игроком (нет укрытий).</summary>
    public bool HasLineOfSightToPlayer()
    {
        if (player == null) return false;
        Vector3 a = transform.position + selfEyeOffset;
        Vector3 b = player.position + playerEyeOffset;
        Vector3 to = b - a;
        float d = to.magnitude;
        if (d < 0.001f) return true;
        // Луч до игрока ничего не задел → видимость чистая.
        return !Physics.Raycast(a, to / d, d - 0.05f, sightBlockers, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// Видит ли игрок оборотня прямо сейчас:
    /// в пределах дальности + в конусе обзора + не в укрытии + не перекрыт рельефом.
    /// </summary>
    public bool IsSeenByPlayer()
    {
        if (player == null) return false;
        if (DistanceToPlayer > playerSightRange) return false;
        if (!PlayerLookingAtMe) return false;

        // Оборотень в тумане, а игрок снаружи → не виден.
        if (_stalker != null && _stalker.IsConcealedAt(transform.position, player.position))
            return false;

        return HasLineOfSightToPlayer();
    }

    /// <summary>Видит ли волк цель: дальность, конус по своему forward, LOS, не скрыта туманом.</summary>
    public bool CanSeeTarget(Transform target)
    {
        if (target == null) return false;
        Vector3 to = target.position - transform.position; to.y = 0f;
        float dist = to.magnitude;
        if (dist > wolfSightRange) return false;
        if (dist > 0.05f)
        {
            Vector3 fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return false;
            if (Vector3.Angle(fwd, to) > wolfViewHalfAngle) return false;
        }

        if (_stalker != null && _stalker.IsConcealedAt(target.position, transform.position))
            return false;

        Vector3 a = transform.position + selfEyeOffset;
        Vector3 b = target.position + playerEyeOffset;
        Vector3 ray = b - a;
        float d = ray.magnitude;
        if (d < 0.001f) return true;
        return !Physics.Raycast(a, ray / d, d - 0.05f, sightBlockers, QueryTriggerInteraction.Ignore);
    }

    /// <summary>Внешний след (бросок патруля и т.п.). Не lock.</summary>
    public void ReportCue(Vector3 worldPos, float radius)
    {
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

    private void TickSenses(float dt)
    {
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

        if (seen != null)
        {
            if (player != seen) BindPlayer(seen);
            ReadStealth(seen, out float notice, out _);
            float dist = FlatDist(transform.position, seen.position);
            float near = 1f - Mathf.Clamp01(dist / Mathf.Max(0.01f, wolfSightRange));
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

    void DrawVisionDebug()
    {
        Vector3 p = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        else fwd.Normalize();

        Color cone = new Color(0.15f, 0.9f, 1f, 1f);
        Vector3 left = Quaternion.AngleAxis(wolfViewHalfAngle, Vector3.up) * fwd;
        Vector3 right = Quaternion.AngleAxis(-wolfViewHalfAngle, Vector3.up) * fwd;
        Debug.DrawLine(p, p + left * wolfSightRange, cone);
        Debug.DrawLine(p, p + right * wolfSightRange, cone);
        Vector3 prev = p + left * wolfSightRange;
        for (int i = 1; i <= 12; i++)
        {
            float a = Mathf.Lerp(wolfViewHalfAngle, -wolfViewHalfAngle, i / 12f);
            Vector3 tip = p + Quaternion.AngleAxis(a, Vector3.up) * fwd * wolfSightRange;
            Debug.DrawLine(prev, tip, cone);
            prev = tip;
        }

        MapGrid grid = mapGrid;
        if (grid == null || !grid.IsReady) return;
        float ts = grid.TileSize;
        if (ts < 0.01f) return;

        int rad = Mathf.CeilToInt(wolfSightRange / ts) + 1;
        grid.WorldToCell(p, out int ocx, out int ocz);
        Vector3 eye = p + selfEyeOffset;
        float half = ts * 0.48f;
        float y = p.y + 0.4f;

        for (int dx = -rad; dx <= rad; dx++)
        {
            for (int dz = -rad; dz <= rad; dz++)
            {
                int cx = ocx + dx;
                int cz = ocz + dz;
                if (cx < 0 || cz < 0 || cx >= grid.Width || cz >= grid.Depth) continue;
                Vector3 c = grid.CellCenterWorld(cx, cz);
                Vector3 flat = c - p; flat.y = 0f;
                if (flat.magnitude > wolfSightRange) continue;
                if (flat.sqrMagnitude > 0.0025f && Vector3.Angle(fwd, flat) > wolfViewHalfAngle) continue;

                bool los = CellLos(grid, ocx, ocz, cx, cz, eye);
                Color col = los ? cone : new Color(1f, 0.25f, 0.85f, 1f);
                float x0 = c.x - half, x1 = c.x + half;
                float z0 = c.z - half, z1 = c.z + half;
                Debug.DrawLine(new Vector3(x0, y, z0), new Vector3(x1, y, z0), col);
                Debug.DrawLine(new Vector3(x1, y, z0), new Vector3(x1, y, z1), col);
                Debug.DrawLine(new Vector3(x1, y, z1), new Vector3(x0, y, z1), col);
                Debug.DrawLine(new Vector3(x0, y, z1), new Vector3(x0, y, z0), col);
            }
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

    static bool CellLos(MapGrid grid, int x0, int z0, int x1, int z1, Vector3 eye)
    {
        if (x0 == x1 && z0 == z1) return true;
        int x = x0, z = z0;
        int dx = Mathf.Abs(x1 - x0), dz = Mathf.Abs(z1 - z0);
        int sx = x0 < x1 ? 1 : -1;
        int sz = z0 < z1 ? 1 : -1;
        int err = dx - dz;
        int steps = dx + dz;
        if (steps < 1) steps = 1;
        int i = 0;
        while (true)
        {
            if (!(x == x0 && z == z0) && !(x == x1 && z == z1))
            {
                Vector3 cell = grid.CellCenterWorld(x, z);
                float t = i / (float)steps;
                float beamY = Mathf.Lerp(eye.y, grid.CellCenterWorld(x1, z1).y + 1f, t);
                if (grid.BlocksSightAtHeight(x, z, beamY - cell.y))
                    return false;
            }
            if (x == x1 && z == z1) return true;
            int e2 = 2 * err;
            if (e2 > -dz) { err -= dz; x += sx; }
            if (e2 < dx) { err += dx; z += sz; }
            i++;
            if (i > 512) return false;
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (drawVisionCells) DrawWolfVisionGizmos();
    }

    void OnDrawGizmosSelected()
    {
        if (!drawVisionCells) DrawWolfVisionGizmos();
    }

    void DrawWolfVisionGizmos()
    {
        Vector3 p = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude > 0.0001f) fwd.Normalize();
        else fwd = Vector3.forward;

        Vector3 left = Quaternion.AngleAxis(wolfViewHalfAngle, Vector3.up) * fwd;
        Vector3 right = Quaternion.AngleAxis(-wolfViewHalfAngle, Vector3.up) * fwd;
        Gizmos.color = new Color(0.15f, 0.85f, 1f, 1f);
        Gizmos.DrawLine(p, p + left * wolfSightRange);
        Gizmos.DrawLine(p, p + right * wolfSightRange);
        const int fan = 12;
        Vector3 prev = p + left * wolfSightRange;
        for (int i = 1; i <= fan; i++)
        {
            float a = Mathf.Lerp(wolfViewHalfAngle, -wolfViewHalfAngle, i / (float)fan);
            Vector3 tip = p + Quaternion.AngleAxis(a, Vector3.up) * fwd * wolfSightRange;
            Gizmos.DrawLine(prev, tip);
            prev = tip;
        }

        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.35f);
        Gizmos.DrawWireSphere(p, hearRangeAtNoise1);

        if (Application.isPlaying && _cueValid)
        {
            Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.9f);
            Gizmos.DrawWireSphere(_cuePos, _cueRadius);
        }

        DrawVisionCells(p, fwd);
    }

    void DrawVisionCells(Vector3 origin, Vector3 fwd)
    {
        MapGrid grid = mapGrid;
        if (grid == null || !grid) grid = Object.FindObjectOfType<MapGrid>();
        if (grid == null || !grid.IsReady) return;
        mapGrid = grid;

        float ts = grid.TileSize;
        if (ts < 0.01f) return;
        int rad = Mathf.CeilToInt(wolfSightRange / ts) + 1;
        grid.WorldToCell(origin, out int ocx, out int ocz);

        Vector3 eye = origin + selfEyeOffset;
        float half = ts * 0.48f;
        float y = origin.y + 0.35f;

        for (int dx = -rad; dx <= rad; dx++)
        {
            for (int dz = -rad; dz <= rad; dz++)
            {
                int cx = ocx + dx;
                int cz = ocz + dz;
                if (cx < 0 || cz < 0 || cx >= grid.Width || cz >= grid.Depth) continue;

                Vector3 c = grid.CellCenterWorld(cx, cz);
                Vector3 flat = c - origin; flat.y = 0f;
                float dist = flat.magnitude;
                if (dist > wolfSightRange) continue;
                if (dist > 0.05f && Vector3.Angle(fwd, flat) > wolfViewHalfAngle) continue;

                bool los = CellLos(grid, ocx, ocz, cx, cz, eye);
                Gizmos.color = los
                    ? new Color(0.1f, 0.95f, 1f, 0.95f)
                    : new Color(1f, 0.2f, 0.85f, 0.9f);

                float x0 = c.x - half, x1 = c.x + half;
                float z0 = c.z - half, z1 = c.z + half;
                Gizmos.DrawLine(new Vector3(x0, y, z0), new Vector3(x1, y, z0));
                Gizmos.DrawLine(new Vector3(x1, y, z0), new Vector3(x1, y, z1));
                Gizmos.DrawLine(new Vector3(x1, y, z1), new Vector3(x0, y, z1));
                Gizmos.DrawLine(new Vector3(x0, y, z1), new Vector3(x0, y, z0));
            }
        }
    }

#endif
}
