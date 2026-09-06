using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Зрение игрока: смещённый эллипс на плоскости карты + LOS по клеткам.
/// Клетка в форме, если центр внутри эллипса. Лучи на край. Tree без маркера = Full.
/// </summary>
public class PlayerVision : MonoBehaviour
{
    [Header("Источник")]
    [Tooltip("Откуда смотрим. Пусто = этот Transform (ставь на игрока).")]
    public Transform origin;
    [Tooltip("Опционально — для клеток, высоты и укрытия.")]
    public MapGrid mapGrid;

    [Header("Форма (клетки)")]
    [Tooltip("Сколько клеток вперёд от игрока.")]
    public int forwardCells = 22;
    [Tooltip("Сколько клеток назад от игрока.")]
    public int backCells = 2;
    [Tooltip("Полуширина эллипса в клетках (в самой широкой точке).")]
    public int sideCells = 10;
    [Tooltip("Высота «глаз» относительно origin (м).")]
    public float eyeHeight = 1.4f;
    [Tooltip("Высота пробы для жёлтых клеток (м над землёй). Цель выше укрытия.")]
    public float peekHeight = 5f;

    [Header("Визуализация")]
    public bool drawGizmos = true;
    public bool drawRuntime = true;
    [Tooltip("Контур видимых клеток. Прячется за объектами.")]
    public bool drawOverlay = true;
    [Tooltip("Толщина золотого контура, м.")]
    public float overlayWidth = 0.05f;
    public Color overlayColor = new Color(0.80f, 0.66f, 0.30f, 0.32f);
    public bool showVisibleCells = true;
    public int maxCellsToDraw = 600;
    public int arcSegments = 64;
    public float yOffset = 0.12f;
    public Color shapeFillColor = new Color(0.25f, 0.85f, 1f, 0.16f);
    public Color shapeEdgeColor = new Color(0.3f, 0.95f, 1f, 0.85f);
    public Color cellClearColor = new Color(0.25f, 0.95f, 0.3f, 0.55f);
    public Color cellPartialColor = new Color(1f, 0.85f, 0.15f, 0.55f);
    public Color cellBlockedColor = new Color(0.95f, 0.2f, 0.15f, 0.5f);

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

    public float TileSize => (mapGrid != null && mapGrid.IsReady) ? mapGrid.TileSize : 4f;
    public float OffsetMeters => OffsetCells * TileSize;
    public float RadiusMeters => RadiusCells * TileSize;
    public float SideMeters => Mathf.Max(1, sideCells) * TileSize;
    public float ForwardMeters => Mathf.Max(1, forwardCells) * TileSize;

    public Vector3 RightFlat
    {
        get
        {
            Vector3 r = Vector3.Cross(Vector3.up, ForwardFlat);
            return r.sqrMagnitude < 0.0001f ? Vector3.right : r.normalized;
        }
    }

    public int OffsetCells
    {
        get
        {
            int f = Mathf.Max(1, forwardCells);
            int b = Mathf.Max(0, backCells);
            return (f - b) / 2;
        }
    }

    public int RadiusCells
    {
        get
        {
            int f = Mathf.Max(1, forwardCells);
            int b = Mathf.Max(0, backCells);
            return (f + b) / 2;
        }
    }

    public Vector3 ShapeCenterFlat
    {
        get
        {
            Vector3 o = OriginTransform.position;
            o.y = 0f;
            return o + ForwardFlat * OffsetMeters;
        }
    }

    /// <summary>Точка внутри смещённого круга и с LOS до её клетки.</summary>
    public bool IsPointVisible(Vector3 worldPos)
    {
        if (!InShape(worldPos)) return false;
        if (mapGrid == null || !mapGrid.IsReady) return true;

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        mapGrid.WorldToCell(worldPos, out int tcx, out int tcz);
        if (pcx == tcx && pcz == tcz) return true;
        return mapGrid.HasSightLos(EyePosition, worldPos);
    }

    /// <summary>Клетка видна: центр в круге + LOS. Требует MapGrid.</summary>
    public bool IsCellVisible(int cx, int cz)
    {
        if (mapGrid == null || !mapGrid.IsReady) return false;
        Vector3 center = mapGrid.CellCenterWorld(cx, cz);
        if (!InShape(center)) return false;

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        if (pcx == cx && pcz == cz) return true;
        return mapGrid.HasSightLos(EyePosition, center);
    }

    /// <summary>
    /// Внешние рёбра золотого контура (те же, что overlay).
    /// Точки — концы рёбер клеток без видимого соседа. dst очищается.
    /// </summary>
    public void CollectOuterContourPoints(List<Vector3> dst)
    {
        dst.Clear();
        if (mapGrid == null || !mapGrid.IsReady) return;

        CollectVisibleCells(_cellBuffer);
        if (_cellBuffer.Count == 0) return;

        float half = mapGrid.TileSize * 0.5f;
        int n = _cellBuffer.Count;

        for (int i = 0; i < n; i++)
        {
            var cell = _cellBuffer[i];
            Vector3 p = mapGrid.CellCenterWorld(cell.x, cell.y);
            float y = p.y + yOffset;
            float x0 = p.x - half, x1 = p.x + half;
            float z0 = p.z - half, z1 = p.z + half;

            if (!IsOverlayVisible(cell.x, cell.y - 1))
            {
                dst.Add(new Vector3(x0, y, z0));
                dst.Add(new Vector3(x1, y, z0));
            }
            if (!IsOverlayVisible(cell.x, cell.y + 1))
            {
                dst.Add(new Vector3(x0, y, z1));
                dst.Add(new Vector3(x1, y, z1));
            }
            if (!IsOverlayVisible(cell.x - 1, cell.y))
            {
                dst.Add(new Vector3(x0, y, z0));
                dst.Add(new Vector3(x0, y, z1));
            }
            if (!IsOverlayVisible(cell.x + 1, cell.y))
            {
                dst.Add(new Vector3(x1, y, z0));
                dst.Add(new Vector3(x1, y, z1));
            }
        }
    }

    /// <summary>Видимые клетки: лучи на край формы, тень от укрытия. outList очищается.</summary>
    public void CollectVisibleCells(List<Vector2Int> outList)
    {
        outList.Clear();
        if (mapGrid == null || !mapGrid.IsReady) return;

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        CollectShapeCells(_shapeBuffer);
        if (_shapeBuffer.Count == 0) return;

        CollectEdgeCells(_shapeBuffer, _edgeBuffer);
        _visibleSet.Clear();
        _visitedSet.Clear();

        if (_edgeBuffer.Count == 0)
        {
            _visibleSet.Add(new Vector2Int(pcx, pcz));
        }
        else
        {
            for (int i = 0; i < _edgeBuffer.Count; i++)
            {
                var e = _edgeBuffer[i];
                Vector3 end = mapGrid.CellCenterWorld(e.x, e.y);
                WalkRay(pcx, pcz, e.x, e.y, end.y, mark: true);
            }
        }

        // редкие дыры после толщины
        for (int i = 0; i < _shapeBuffer.Count; i++)
        {
            var c = _shapeBuffer[i];
            if (_visitedSet.Contains(c)) continue;
            Vector3 end = mapGrid.CellCenterWorld(c.x, c.y);
            if (mapGrid.HasSightLos(EyePosition, end))
                _visibleSet.Add(c);
        }

        foreach (var c in _visibleSet)
            outList.Add(c);
    }

    public bool InShape(Vector3 worldPos)
    {
        Vector3 origin = OriginTransform.position;
        origin.y = 0f;
        Vector3 p = worldPos;
        p.y = 0f;
        Vector3 rel = p - origin;
        float ts = TileSize;
        if (ts < 0.001f) return false;
        float localX = Vector3.Dot(rel, RightFlat) / ts;
        float localZ = Vector3.Dot(rel, ForwardFlat) / ts;
        float ax = Mathf.Max(1, sideCells);
        float az = Mathf.Max(1, RadiusCells);
        float lx = localX / ax;
        float lz = (localZ - OffsetCells) / az;
        return lx * lx + lz * lz <= 1.0001f;
    }

    // --- internals ---

    private Transform OriginTransform => origin != null ? origin : transform;
    private readonly List<Vector2Int> _cellBuffer = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _shapeBuffer = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _edgeBuffer = new List<Vector2Int>(128);
    private readonly List<Vector2Int> _rayBuffer = new List<Vector2Int>(64);
    private readonly HashSet<Vector2Int> _shapeSet = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> _visibleSet = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> _visitedSet = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> _partialBuffer = new List<Vector2Int>(256);
    private readonly List<Vector2Int> _blockedBuffer = new List<Vector2Int>(256);
    private readonly List<Vector3> _overlayVerts = new List<Vector3>(1024);
    private readonly List<int> _overlayTris = new List<int>(1536);
    private Mesh _overlayMesh;
    private Material _overlayMat;

    void Awake()
    {
        if (origin == null) origin = transform;
        EnsureMapGrid();
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

    void LateUpdate()
    {
        if (!drawOverlay || !Application.isPlaying) return;
        if (mapGrid == null || !mapGrid.IsReady) return;
        CollectVisibleCells(_cellBuffer);
        DrawOverlayOuterContour(_cellBuffer);
    }

    void OnDisable()
    {
        if (_overlayMesh != null)
        {
            if (Application.isPlaying) Destroy(_overlayMesh);
            else DestroyImmediate(_overlayMesh);
            _overlayMesh = null;
        }
        if (_overlayMat != null)
        {
            if (Application.isPlaying) Destroy(_overlayMat);
            else DestroyImmediate(_overlayMat);
            _overlayMat = null;
        }
    }

    private void CollectShapeCells(List<Vector2Int> dst)
    {
        dst.Clear();
        _shapeSet.Clear();
        if (mapGrid == null || !mapGrid.IsReady) return;

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        int span = Mathf.Max(Mathf.Max(1, sideCells), RadiusCells + OffsetCells) + 2;
        int w = mapGrid.Width;
        int d = mapGrid.Depth;
        int x0 = Mathf.Max(0, pcx - span);
        int x1 = Mathf.Min(w - 1, pcx + span);
        int z0 = Mathf.Max(0, pcz - span);
        int z1 = Mathf.Min(d - 1, pcz + span);

        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                if (!InShape(mapGrid.CellCenterWorld(x, z))) continue;
                var c = new Vector2Int(x, z);
                dst.Add(c);
                _shapeSet.Add(c);
            }
        }
    }

    private void CollectEdgeCells(List<Vector2Int> shape, List<Vector2Int> dst)
    {
        dst.Clear();
        int w = mapGrid.Width;
        int d = mapGrid.Depth;
        for (int i = 0; i < shape.Count; i++)
        {
            var c = shape[i];
            bool edge = false;
            for (int dx = -1; dx <= 1 && !edge; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = c.x + dx;
                    int nz = c.y + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= d)
                    {
                        edge = true;
                        break;
                    }
                    if (!_shapeSet.Contains(new Vector2Int(nx, nz)))
                    {
                        edge = true;
                        break;
                    }
                }
            }
            if (edge) dst.Add(c);
        }
    }

    private bool HasLosToCell(int pcx, int pcz, int tcx, int tcz, float targetWorldY)
    {
        Vector3 end = mapGrid.CellCenterWorld(tcx, tcz);
        end.y = targetWorldY;
        return mapGrid.HasSightLos(EyePosition, end);
    }

    /// <summary>
    /// Суперкавер-луч + толщина (крест). Клетка-укрытие видна, дальше режется высотой.
    /// Сосед с укрытием глушит весь луч. mark пишет visited/visible.
    /// </summary>
    private bool WalkRay(int x0, int z0, int x1, int z1, float targetWorldY, bool mark)
    {
        Supercover(x0, z0, x1, z1, _rayBuffer);
        if (_rayBuffer.Count == 0) return false;

        Vector3 eye = EyePosition;
        Vector3 startFlat = mapGrid.CellCenterWorld(x0, z0);
        startFlat.y = 0f;
        float total = Vector3.Distance(startFlat, new Vector3(mapGrid.CellCenterWorld(x1, z1).x, 0f, mapGrid.CellCenterWorld(x1, z1).z));
        if (total < 0.0001f) total = 0.0001f;

        float occH = 0f;
        bool occFull = false;
        bool targetVisible = false;

        for (int i = 0; i < _rayBuffer.Count; i++)
        {
            var c = _rayBuffer[i];
            bool visible = CheckCellVisibility(c, i == 0, targetWorldY, eye, startFlat, total, ref occH, ref occFull, mark);
            if (c.x == x1 && c.y == z1)
                targetVisible = visible;
            if (!visible) continue;

            CheckNeighbor(c.x - 1, c.y, targetWorldY, eye, startFlat, total, ref occH, ref occFull, mark);
            CheckNeighbor(c.x + 1, c.y, targetWorldY, eye, startFlat, total, ref occH, ref occFull, mark);
            CheckNeighbor(c.x, c.y - 1, targetWorldY, eye, startFlat, total, ref occH, ref occFull, mark);
            CheckNeighbor(c.x, c.y + 1, targetWorldY, eye, startFlat, total, ref occH, ref occFull, mark);
        }

        return targetVisible;
    }

    private bool CheckCellVisibility(Vector2Int c, bool alwaysVisible, float targetWorldY,
        Vector3 eye, Vector3 startFlat, float total,
        ref float occH, ref bool occFull, bool mark)
    {
        if (mark) _visitedSet.Add(c);

        bool visible;
        if (alwaysVisible)
            visible = true;
        else if (occFull)
            visible = false;
        else if (occH > 0f)
        {
            Vector3 cc = mapGrid.CellCenterWorld(c.x, c.y);
            Vector3 flat = cc;
            flat.y = 0f;
            float t = Vector3.Distance(startFlat, flat) / total;
            float rayY = Mathf.Lerp(eye.y, targetWorldY, t);
            visible = (rayY - cc.y) > occH;
        }
        else
            visible = true;

        if (visible && mark)
            _visibleSet.Add(c);
        if (!visible) return false;

        mapGrid.GetEffectiveSightCover(c.x, c.y, out var mode, out float coverH);
        if (mode == MapGrid.SightCoverMode.Full)
            occFull = true;
        else if (mode == MapGrid.SightCoverMode.MaxHeight)
            occH = Mathf.Max(occH, coverH);
        return true;
    }

    private void CheckNeighbor(int nx, int nz, float targetWorldY,
        Vector3 eye, Vector3 startFlat, float total,
        ref float occH, ref bool occFull, bool mark)
    {
        if (occFull) return;
        if (nx < 0 || nz < 0 || nx >= mapGrid.Width || nz >= mapGrid.Depth) return;

        var nc = new Vector2Int(nx, nz);
        if (!_shapeSet.Contains(nc) && !InShape(mapGrid.CellCenterWorld(nx, nz))) return;
        if (mark && _visitedSet.Contains(nc)) return;

        CheckCellVisibility(nc, false, targetWorldY, eye, startFlat, total, ref occH, ref occFull, mark);
    }

    private static void Supercover(int x0, int z0, int x1, int z1, List<Vector2Int> dst)
    {
        dst.Clear();
        int dx = x1 - x0;
        int dz = z1 - z0;
        int nx = Mathf.Abs(dx);
        int nz = Mathf.Abs(dz);
        int sx = dx > 0 ? 1 : -1;
        int sz = dz > 0 ? 1 : -1;
        int x = x0;
        int z = z0;
        dst.Add(new Vector2Int(x, z));

        for (int ix = 0, iz = 0; ix < nx || iz < nz;)
        {
            int decision = (1 + 2 * ix) * nz - (1 + 2 * iz) * nx;
            if (decision == 0)
            {
                x += sx;
                z += sz;
                ix++;
                iz++;
            }
            else if (decision < 0)
            {
                x += sx;
                ix++;
            }
            else
            {
                z += sz;
                iz++;
            }
            dst.Add(new Vector2Int(x, z));
        }
    }

    private void DrawVisionShape(bool gizmo)
    {
        Vector3 eye = EyePosition;
        float y = eye.y - eyeHeight + yOffset;
        Vector3 center = ShapeCenterFlat;
        center.y = y;
        Vector3 fwd = ForwardFlat;
        Vector3 right = RightFlat;
        float ax = SideMeters;
        float az = RadiusMeters;

        int segs = Mathf.Max(16, arcSegments);
        Vector3 prev = center + right * ax;
        for (int i = 1; i <= segs; i++)
        {
            float ang = (i * Mathf.PI * 2f) / segs;
            Vector3 next = center + right * (ax * Mathf.Cos(ang)) + fwd * (az * Mathf.Sin(ang));
            if (gizmo)
            {
                Gizmos.color = shapeEdgeColor;
                Gizmos.DrawLine(prev, next);
            }
            else
                Debug.DrawLine(prev, next, shapeEdgeColor);
            prev = next;
        }

        Vector3 originFlat = OriginTransform.position;
        originFlat.y = y;
        Vector3 fwdEnd = originFlat + fwd * ForwardMeters;
        if (gizmo)
        {
            Gizmos.color = shapeEdgeColor;
            Gizmos.DrawLine(originFlat, fwdEnd);
            Gizmos.DrawSphere(originFlat, 0.18f);
        }
        else
            Debug.DrawLine(originFlat, fwdEnd, shapeEdgeColor);

        int fillRays = Mathf.Max(10, arcSegments / 4);
        for (int i = 0; i < fillRays; i++)
        {
            float ang = (i * Mathf.PI * 2f) / fillRays;
            Vector3 end = center + right * (ax * Mathf.Cos(ang)) + fwd * (az * Mathf.Sin(ang));
            if (gizmo)
            {
                Gizmos.color = shapeFillColor;
                Gizmos.DrawLine(center, end);
            }
            else
                Debug.DrawLine(center, end, shapeFillColor);
        }
    }

    private void DrawVisibleCells(bool gizmo)
    {
        if (mapGrid == null || !mapGrid.IsReady) return;

        CollectVisibleCells(_cellBuffer);
        CollectShapeCells(_shapeBuffer);
        _partialBuffer.Clear();
        _blockedBuffer.Clear();

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        float peek = Mathf.Max(eyeHeight, peekHeight);

        for (int i = 0; i < _shapeBuffer.Count; i++)
        {
            var c = _shapeBuffer[i];
            if (_visibleSet.Contains(c)) continue;
            Vector3 center = mapGrid.CellCenterWorld(c.x, c.y);
            bool high = (c.x == pcx && c.y == pcz) || HasLosToCell(pcx, pcz, c.x, c.y, center.y + peek);
            if (high) _partialBuffer.Add(c);
            else _blockedBuffer.Add(c);
        }

        int budget = maxCellsToDraw;
        budget -= DrawCellList(_cellBuffer, cellClearColor, gizmo, budget);
        budget -= DrawCellList(_partialBuffer, cellPartialColor, gizmo, budget);
        DrawCellList(_blockedBuffer, cellBlockedColor, gizmo, budget);
    }

    private void DrawOverlayOuterContour(List<Vector2Int> cells)
    {
        EnsureOverlay();
        if (_overlayMesh == null || _overlayMat == null) return;
        if (cells == null || cells.Count == 0) return;

        _overlayMat.SetColor("_Color", overlayColor);
        _overlayVerts.Clear();
        _overlayTris.Clear();

        float half = mapGrid.TileSize * 0.5f;
        float w = Mathf.Clamp(overlayWidth, 0.01f, half * 0.35f);
        int n = Mathf.Min(cells.Count, Mathf.Max(0, maxCellsToDraw));
        for (int i = 0; i < n; i++)
        {
            var cell = cells[i];
            Vector3 p = mapGrid.CellCenterWorld(cell.x, cell.y);
            float y = p.y + yOffset;
            float x0 = p.x - half, x1 = p.x + half;
            float z0 = p.z - half, z1 = p.z + half;
            if (!IsOverlayVisible(cell.x, cell.y - 1)) AddOverlayQuad(x0, z0, x1, z0 + w, y);
            if (!IsOverlayVisible(cell.x, cell.y + 1)) AddOverlayQuad(x0, z1 - w, x1, z1, y);
            if (!IsOverlayVisible(cell.x - 1, cell.y)) AddOverlayQuad(x0, z0 + w, x0 + w, z1 - w, y);
            if (!IsOverlayVisible(cell.x + 1, cell.y)) AddOverlayQuad(x1 - w, z0 + w, x1, z1 - w, y);
        }

        _overlayMesh.Clear();
        if (_overlayVerts.Count < 3) return;
        _overlayMesh.SetVertices(_overlayVerts);
        _overlayMesh.SetTriangles(_overlayTris, 0, false);
        _overlayMesh.RecalculateBounds();
        Graphics.DrawMesh(_overlayMesh, Matrix4x4.identity, _overlayMat, 0);
    }

    private bool IsOverlayVisible(int cx, int cz)
    {
        return _visibleSet.Contains(new Vector2Int(cx, cz));
    }

    private void AddOverlayQuad(float x0, float z0, float x1, float z1, float y)
    {
        int b = _overlayVerts.Count;
        _overlayVerts.Add(new Vector3(x0, y, z0));
        _overlayVerts.Add(new Vector3(x1, y, z0));
        _overlayVerts.Add(new Vector3(x1, y, z1));
        _overlayVerts.Add(new Vector3(x0, y, z1));
        _overlayTris.Add(b + 0); _overlayTris.Add(b + 2); _overlayTris.Add(b + 1);
        _overlayTris.Add(b + 0); _overlayTris.Add(b + 3); _overlayTris.Add(b + 2);
    }

    private void EnsureOverlay()
    {
        if (_overlayMesh == null)
        {
            _overlayMesh = new Mesh { name = "VisionCellOverlay" };
            _overlayMesh.MarkDynamic();
        }
        if (_overlayMat == null)
        {
            var sh = Shader.Find("Hidden/VisionCellOverlay");
            if (sh == null) return;
            _overlayMat = new Material(sh);
            _overlayMat.renderQueue = 3000;
        }
    }

    private int DrawCellList(List<Vector2Int> cells, Color col, bool gizmo, int budget)
    {
        if (budget <= 0 || cells == null) return 0;
        int n = Mathf.Min(cells.Count, budget);
        float half = mapGrid.TileSize * 0.42f;
        for (int i = 0; i < n; i++)
        {
            var c = cells[i];
            Vector3 p = mapGrid.CellCenterWorld(c.x, c.y);
            p.y += yOffset;
            if (gizmo)
            {
                Gizmos.color = col;
                Gizmos.DrawCube(p, new Vector3(half * 2f, 0.05f, half * 2f));
            }
            else
            {
                float h = half;
                Debug.DrawLine(p + new Vector3(-h, 0, -h), p + new Vector3(h, 0, -h), col);
                Debug.DrawLine(p + new Vector3(h, 0, -h), p + new Vector3(h, 0, h), col);
                Debug.DrawLine(p + new Vector3(h, 0, h), p + new Vector3(-h, 0, h), col);
                Debug.DrawLine(p + new Vector3(-h, 0, h), p + new Vector3(-h, 0, -h), col);
            }
        }
        return n;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        forwardCells = Mathf.Max(1, forwardCells);
        backCells = Mathf.Max(0, backCells);
        if (backCells >= forwardCells) backCells = forwardCells - 1;
        sideCells = Mathf.Max(1, sideCells);
        arcSegments = Mathf.Clamp(arcSegments, 8, 128);
        maxCellsToDraw = Mathf.Max(0, maxCellsToDraw);
    }
#endif
}
