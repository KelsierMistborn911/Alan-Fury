using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Зрение игрока: конус + круг на плоскости карты.
/// Определяет, какие клетки / точки видит персонаж (геометрия, без LOS пока).
/// Визуализация — для отладки и настройки. Позже: прозрачность препятствий / culling.
/// Аркадный уклон: радиус побольше, конус широкий.
/// </summary>
public class PlayerVision : MonoBehaviour
{
    [Header("Источник")]
    [Tooltip("Откуда смотрим. Пусто = этот Transform (ставь на игрока).")]
    public Transform origin;
    [Tooltip("Опционально — для клеток и высоты.")]
    public MapGrid mapGrid;

    [Header("Параметры зрения")]
    [Tooltip("Макс. дальность (м). Аркаднее — 40–55.")]
    public float visionRange = 48f;
    [Tooltip("Половина угла конуса (град). 70 ≈ 140° FOV.")]
    [Range(5f, 180f)] public float coneHalfAngle = 70f;
    [Tooltip("Если true — только внутри конуса. Если false — полный круг (360°).")]
    public bool useCone = true;
    [Tooltip("Высота «глаз» относительно origin (м).")]
    public float eyeHeight = 1.4f;

    [Header("Визуализация")]
    public bool drawGizmos = true;
    public bool drawRuntime = true;
    [Tooltip("Рисовать заполненные клетки (дорого при большом range).")]
    public bool showVisibleCells = true;
    public int maxCellsToDraw = 280;
    public int arcSegments = 56;
    public float yOffset = 0.12f;
    public Color coneFillColor = new Color(0.25f, 0.85f, 1f, 0.18f);
    public Color coneEdgeColor = new Color(0.3f, 0.95f, 1f, 0.85f);
    public Color circleEdgeColor = new Color(0.2f, 0.7f, 1f, 0.45f);
    public Color cellColor = new Color(0.35f, 1f, 0.45f, 0.4f);

    // --- public API ---

    public Vector3 EyePosition
    {
        get
        {
            var t = OriginTransform;
            return t.position + Vector3.up * eyeHeight;
        }
    }

    public Vector3 ForwardFlat
    {
        get
        {
            var t = OriginTransform;
            Vector3 f = t.forward;
            f.y = 0f;
            return f.sqrMagnitude < 0.0001f ? Vector3.forward : f.normalized;
        }
    }

    /// <summary>Точка в пределах visionRange и (если useCone) внутри конуса.</summary>
    public bool IsPointVisible(Vector3 worldPos)
    {
        Vector3 eye = EyePosition;
        Vector3 to = worldPos - eye;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist > visionRange || dist < 0.001f) return dist <= 0.001f;

        if (!useCone) return true;

        float angle = Vector3.Angle(ForwardFlat, to);
        return angle <= coneHalfAngle;
    }

    /// <summary>Клетка видна (центр клетки). Требует MapGrid.</summary>
    public bool IsCellVisible(int cx, int cz)
    {
        if (mapGrid == null || !mapGrid.IsReady) return false;
        return IsPointVisible(mapGrid.CellCenterWorld(cx, cz));
    }

    /// <summary>Собирает видимые клетки в радиусе (AABB + проверка). outList очищается.</summary>
    public void CollectVisibleCells(List<Vector2Int> outList)
    {
        outList.Clear();
        if (mapGrid == null || !mapGrid.IsReady) return;

        Vector3 eye = EyePosition;
        mapGrid.WorldToCell(eye, out int pcx, out int pcz);
        float ts = mapGrid.TileSize;
        int cellR = Mathf.CeilToInt(visionRange / ts) + 1;

        int w = mapGrid.Width;
        int d = mapGrid.Depth;
        int x0 = Mathf.Max(0, pcx - cellR);
        int x1 = Mathf.Min(w - 1, pcx + cellR);
        int z0 = Mathf.Max(0, pcz - cellR);
        int z1 = Mathf.Min(d - 1, pcz + cellR);

        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                if (IsCellVisible(x, z))
                    outList.Add(new Vector2Int(x, z));
            }
        }
    }

    // --- internals ---

    private Transform OriginTransform => origin != null ? origin : transform;
    private readonly List<Vector2Int> _cellBuffer = new List<Vector2Int>(256);

    void Awake()
    {
        if (origin == null) origin = transform;
        EnsureMapGrid();
    }

    void Start()
    {
        EnsureMapGrid();
    }

    /// <summary>Сбросить битую ссылку и найти MapGrid на сцене.</summary>
    public void EnsureMapGrid()
    {
        // type mismatch / Missing — mapGrid может быть «не null», но невалиден
        if (mapGrid != null && mapGrid.IsReady) return;
        if (mapGrid != null && !mapGrid) mapGrid = null; // Unity fake-null

        var found = FindObjectOfType<MapGrid>();
        if (found != null) mapGrid = found;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        DrawVisionShape(gizmo: true);
        if (showVisibleCells) DrawVisibleCells(gizmo: true);
    }

    void Update()
    {
        if (!drawRuntime || !Application.isPlaying) return;
        DrawVisionShape(gizmo: false);
        if (showVisibleCells) DrawVisibleCells(gizmo: false);
    }

    private void DrawVisionShape(bool gizmo)
    {
        Vector3 eye = EyePosition;
        Vector3 fwd = ForwardFlat;
        float y = eye.y - eyeHeight + yOffset; // плоскость чуть над ногами / картой
        Vector3 center = new Vector3(eye.x, y, eye.z);

        // Круг (всегда, как граница дальности)
        DrawArc(center, Vector3.forward, 360f, visionRange, circleEdgeColor, gizmo, closed: true);

        if (useCone)
        {
            // Лучи конуса + дуга
            Quaternion leftRot = Quaternion.AngleAxis(-coneHalfAngle, Vector3.up);
            Quaternion rightRot = Quaternion.AngleAxis(coneHalfAngle, Vector3.up);
            Vector3 leftDir = leftRot * fwd;
            Vector3 rightDir = rightRot * fwd;

            Vector3 leftEnd = center + leftDir * visionRange;
            Vector3 rightEnd = center + rightDir * visionRange;

            if (gizmo)
            {
                Gizmos.color = coneEdgeColor;
                Gizmos.DrawLine(center, leftEnd);
                Gizmos.DrawLine(center, rightEnd);
            }
            else
            {
                Debug.DrawLine(center, leftEnd, coneEdgeColor);
                Debug.DrawLine(center, rightEnd, coneEdgeColor);
            }

            // Дуга конуса
            DrawArc(center, leftDir, coneHalfAngle * 2f, visionRange, coneEdgeColor, gizmo, closed: false);

            // Заливка (радиальные линии — дешёвый fill)
            int fillRays = Mathf.Max(8, arcSegments / 4);
            for (int i = 0; i <= fillRays; i++)
            {
                float t = (float)i / fillRays;
                float ang = Mathf.Lerp(-coneHalfAngle, coneHalfAngle, t);
                Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * fwd;
                Vector3 end = center + dir * visionRange;
                Color c = coneFillColor;
                if (gizmo)
                {
                    Gizmos.color = c;
                    Gizmos.DrawLine(center, end);
                }
                else
                    Debug.DrawLine(center, end, c);
            }
        }
    }

    private void DrawArc(Vector3 center, Vector3 startDir, float totalAngleDeg, float radius,
        Color color, bool gizmo, bool closed)
    {
        int segs = Mathf.Max(8, arcSegments);
        if (totalAngleDeg >= 359f) segs = Mathf.Max(segs, 64);

        Vector3 prev = center + startDir.normalized * radius;
        float step = totalAngleDeg / segs;

        for (int i = 1; i <= segs; i++)
        {
            float ang = step * i;
            Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * startDir;
            Vector3 next = center + dir.normalized * radius;

            if (gizmo)
            {
                Gizmos.color = color;
                Gizmos.DrawLine(prev, next);
            }
            else
                Debug.DrawLine(prev, next, color);

            prev = next;
        }

        if (closed && totalAngleDeg >= 359f)
        {
            // уже замкнут
        }
    }

    private void DrawVisibleCells(bool gizmo)
    {
        if (mapGrid == null || !mapGrid.IsReady) return;

        CollectVisibleCells(_cellBuffer);
        int count = Mathf.Min(_cellBuffer.Count, maxCellsToDraw);
        float half = mapGrid.TileSize * 0.42f;

        for (int i = 0; i < count; i++)
        {
            var c = _cellBuffer[i];
            Vector3 p = mapGrid.CellCenterWorld(c.x, c.y);
            p.y += yOffset;

            if (gizmo)
            {
                Gizmos.color = cellColor;
                Gizmos.DrawCube(p, new Vector3(half * 2f, 0.05f, half * 2f));
            }
            else
            {
                // Debug не рисует кубы — тонкие кресты / рамки
                float h = half;
                Color col = cellColor;
                Debug.DrawLine(p + new Vector3(-h, 0, -h), p + new Vector3(h, 0, -h), col);
                Debug.DrawLine(p + new Vector3(h, 0, -h), p + new Vector3(h, 0, h), col);
                Debug.DrawLine(p + new Vector3(h, 0, h), p + new Vector3(-h, 0, h), col);
                Debug.DrawLine(p + new Vector3(-h, 0, h), p + new Vector3(-h, 0, -h), col);
            }
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        visionRange = Mathf.Max(1f, visionRange);
        coneHalfAngle = Mathf.Clamp(coneHalfAngle, 5f, 180f);
        arcSegments = Mathf.Clamp(arcSegments, 8, 128);
        maxCellsToDraw = Mathf.Max(0, maxCellsToDraw);
    }
#endif
}
