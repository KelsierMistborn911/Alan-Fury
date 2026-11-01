using UnityEngine;
using System.Collections.Generic;

public class PlayerVision : MonoBehaviour
{
    [Header("Источник")]
    public Transform origin;
    public MapGrid mapGrid;

    [Header("Форма (клетки)")]
    public int forwardCells = 22;
    public int backCells = 2;
    public int sideCells = 10;
    public float eyeHeight = 1.4f;
    public float peekHeight = 5f;

    [Header("Визуализация (Gizmo, Scene View)")]
    public bool drawGizmos = true;
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

    public bool IsPointVisible(Vector3 worldPos)
    {
        if (!InShape(worldPos)) return false;
        if (mapGrid == null || !mapGrid.IsReady) return true;

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        mapGrid.WorldToCell(worldPos, out int tcx, out int tcz);
        if (pcx == tcx && pcz == tcz) return true;
        return mapGrid.HasSightLos(EyePosition, worldPos);
    }

    public bool InFringe(Vector3 worldPos, int extraCells)
    {
        if (IsPointVisible(worldPos)) return false;
        return InShapeExpanded(worldPos, Mathf.Max(0, extraCells));
    }

    public bool IsCellVisible(int cx, int cz)
    {
        if (mapGrid == null || !mapGrid.IsReady) return false;
        Vector3 center = mapGrid.CellCenterWorld(cx, cz);
        if (!InShape(center)) return false;

        mapGrid.WorldToCell(OriginTransform.position, out int pcx, out int pcz);
        if (pcx == cx && pcz == cz) return true;
        return mapGrid.HasSightLos(EyePosition, center);
    }

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

    public bool InShape(Vector3 worldPos) => InShapeExpanded(worldPos, 0);

    public bool InShapeExpanded(Vector3 worldPos, int extraCells)
    {
        int extra = Mathf.Max(0, extraCells);
        Vector3 origin = OriginTransform.position;
        origin.y = 0f;
        Vector3 p = worldPos;
        p.y = 0f;
        Vector3 rel = p - origin;
        float ts = TileSize;
        if (ts < 0.001f) return false;
        float localX = Vector3.Dot(rel, RightFlat) / ts;
        float localZ = Vector3.Dot(rel, ForwardFlat) / ts;
        int side = Mathf.Max(1, sideCells) + extra;
        int fwd = Mathf.Max(1, forwardCells) + extra;
        int back = Mathf.Max(0, backCells) + extra;
        float offset = (fwd - back) * 0.5f;
        float radius = (fwd + back) * 0.5f;
        float lx = localX / side;
        float lz = (localZ - offset) / Mathf.Max(1f, radius);
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
    private Mesh _fillVisibleMesh;
    private Mesh _fillBlockedMesh;
    private Material _fillVisibleMat;
    private Material _fillBlockedMat;

    void Awake()
    {
        if (origin == null) origin = transform;
        EnsureMapGrid();
    }

    void Start()
    {
        EnsureMapGrid();
        if (FindObjectOfType<EnemyPresenceManager>() == null)
        {
            var go = new GameObject("EnemyPresenceManager");
            go.AddComponent<EnemyPresenceManager>();
        }
    }

    public void EnsureMapGrid()
    {
        if (mapGrid != null && mapGrid.IsReady) return;
        if (mapGrid != null && !mapGrid) mapGrid = null;

        var found = FindObjectOfType<MapGrid>();
        if (found != null) mapGrid = found;
    }

    // ТОЛЬКО GIZMO: рисуем контур и кубики в Scene View
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        DrawVisionShape(gizmo: true);
        if (showVisibleCells) DrawVisibleCells(gizmo: true);
        DrawOverlayGizmo();
    }

    void OnDisable()
    {
        DestroyMesh(ref _overlayMesh);
        DestroyMesh(ref _fillVisibleMesh);
        DestroyMesh(ref _fillBlockedMesh);
        DestroyMat(ref _overlayMat);
        DestroyMat(ref _fillVisibleMat);
        DestroyMat(ref _fillBlockedMat);
    }

    private void DestroyMesh(ref Mesh m)
    {
        if (m == null) return;
        if (Application.isPlaying) Destroy(m);
        else DestroyImmediate(m);
        m = null;
    }

    private void DestroyMat(ref Material m)
    {
        if (m == null) return;
        if (Application.isPlaying) Destroy(m);
        else DestroyImmediate(m);
        m = null;
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

    // Вспомогательный метод для Gizmo-контура (рисует только в Scene View)
    private void DrawOverlayGizmo()
    {
        if (mapGrid == null || !mapGrid.IsReady) return;
        CollectVisibleCells(_cellBuffer);
        float half = mapGrid.TileSize * 0.5f;
        float w = Mathf.Clamp(0.05f, 0.01f, half * 0.35f);
        Gizmos.color = new Color(0.80f, 0.66f, 0.30f, 0.32f);
        for (int i = 0; i < _cellBuffer.Count; i++)
        {
            var cell = _cellBuffer[i];
            Vector3 p = mapGrid.CellCenterWorld(cell.x, cell.y);
            float y = p.y + yOffset;
            float x0 = p.x - half, x1 = p.x + half;
            float z0 = p.z - half, z1 = p.z + half;
            if (!IsOverlayVisible(cell.x, cell.y - 1)) Gizmos.DrawCube(new Vector3((x0 + x1) * 0.5f, y, z0 + w * 0.5f), new Vector3(x1 - x0, 0.02f, w));
            if (!IsOverlayVisible(cell.x, cell.y + 1)) Gizmos.DrawCube(new Vector3((x0 + x1) * 0.5f, y, z1 - w * 0.5f), new Vector3(x1 - x0, 0.02f, w));
            if (!IsOverlayVisible(cell.x - 1, cell.y)) Gizmos.DrawCube(new Vector3(x0 + w * 0.5f, y, (z0 + z1) * 0.5f), new Vector3(w, 0.02f, z1 - z0));
            if (!IsOverlayVisible(cell.x + 1, cell.y)) Gizmos.DrawCube(new Vector3(x1 - w * 0.5f, y, (z0 + z1) * 0.5f), new Vector3(w, 0.02f, z1 - z0));
        }
    }

    private bool IsOverlayVisible(int cx, int cz)
    {
        return _visibleSet.Contains(new Vector2Int(cx, cz));
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