using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Сетка проходимости + A* (8 соседей). Читает MapGrid (Tree = стена) + опционально Physics.
/// Строится после placement (деревья уже в MapGrid).
///
/// API для AI:
///   Build() / TryFindPath / NearestWalkableWorld / IsReady
/// </summary>
public class Pathfinder : MonoBehaviour
{
    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;
    public MapGrid mapGrid;

    [Header("Препятствия")]
    [Tooltip("Слои редких GO-препятствий. CheckBox по клетке (доп. к MapGrid).")]
    public LayerMask obstacleMask;
    [Tooltip("Размер бокса проверки препятствия в клетке (м).")]
    public Vector3 checkBoxSize = new Vector3(2f, 4f, 2f);

    [Header("Поиск проходимой клетки")]
    [Tooltip("Макс. радиус (в клетках) поиска ближайшей проходимой клетки при подтяжке цели.")]
    public int nearestSearchRadius = 32;

    [Header("Отладка")]
    public bool drawGizmos = false;

    public bool IsReady => _ready;

    private bool[] _walkable;
    private float[] _cost;
    private int _w, _d;
    private float _ts;
    private Vector3 _origin;
    private bool _ready;

    private const float SQRT2 = 1.41421356f;

    // ============ Построение ============

    public void Build()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("Pathfinder: карта высот не готова!");
            _ready = false;
            return;
        }

        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();

        _w = heightSource.width;
        _d = heightSource.depth;
        _ts = ResolveTileSize();
        _origin = new Vector3(-_w * _ts / 2f, 0f, -_d * _ts / 2f);

        int n = _w * _d;
        _walkable = new bool[n];
        _cost = new float[n];

        Physics.SyncTransforms();
        Vector3 half = checkBoxSize * 0.5f;
        bool usePhysicsObstacles = obstacleMask != 0;
        bool useGrid = mapGrid != null && mapGrid.IsReady;

        for (int x = 0; x < _w; x++)
        {
            for (int z = 0; z < _d; z++)
            {
                int idx = Idx(x, z);
                float h = heightSource.GetHeight(x, z);
                Vector3 center = new Vector3(_origin.x + x * _ts, h + half.y, _origin.z + z * _ts);

                bool blocked = useGrid && mapGrid.IsBlocked(x, z);
                if (!blocked && usePhysicsObstacles &&
                    Physics.CheckBox(center, half, Quaternion.identity,
                                     obstacleMask, QueryTriggerInteraction.Ignore))
                    blocked = true;

                _walkable[idx] = !blocked;
                _cost[idx] = useGrid ? mapGrid.GetCost(x, z) : 1f;
            }
        }

        _ready = true;
        Debug.Log($"Pathfinder: сетка {_w}×{_d} (MapGrid={(useGrid ? "on" : "off")}, tileSize={_ts}).");
    }

    // ============ Поиск пути ============

    /// <summary>Заполняет result мировыми точками пути (стартовая клетка не включается). false, если пути нет.</summary>
    public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> result)
    {
        return TryFindPath(from, to, result, avoidRoads: false);
    }

    /// <summary>avoidRoads — клетка с флагом Road дороже, путь идёт рядом, не по полотну.</summary>
    public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> result, bool avoidRoads)
    {
        result.Clear();
        if (!_ready) return false;

        int sx, sz, tx, tz;
        WorldToCell(from, out sx, out sz);
        WorldToCell(to, out tx, out tz);

        if ((!_walkable[Idx(sx, sz)] || (avoidRoads && IsRoadCell(sx, sz)))
            && !FindNearestWalkableCell(sx, sz, out sx, out sz, avoidRoads)) return false;
        if ((!_walkable[Idx(tx, tz)] || (avoidRoads && IsRoadCell(tx, tz)))
            && !FindNearestWalkableCell(tx, tz, out tx, out tz, avoidRoads)) return false;

        int start = Idx(sx, sz);
        int goal = Idx(tx, tz);
        if (start == goal) return false; // уже на месте

        int n = _w * _d;
        float[] g = new float[n];
        int[] prev = new int[n];
        bool[] closed = new bool[n];
        for (int i = 0; i < n; i++) { g[i] = float.MaxValue; prev[i] = -1; }

        var heap = new MinHeap(n + 1);
        g[start] = 0f;
        heap.Push(start, Heuristic(sx, sz, tx, tz));

        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

        bool found = false;
        while (heap.Count > 0)
        {
            int cur = heap.Pop();
            if (closed[cur]) continue;
            closed[cur] = true;
            if (cur == goal) { found = true; break; }

            int cx = cur / _d;
            int cz = cur % _d;

            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k];
                int nz = cz + dz[k];
                if (nx < 0 || nx >= _w || nz < 0 || nz >= _d) continue;

                int nIdx = Idx(nx, nz);
                if (closed[nIdx] || !_walkable[nIdx]) continue;

                // Диагональ — только если обе ортогональные клетки проходимы (без среза угла сквозь дерево).
                bool diag = dx[k] != 0 && dz[k] != 0;
                if (diag && (!_walkable[Idx(cx + dx[k], cz)] || !_walkable[Idx(cx, cz + dz[k])]))
                    continue;

                float stepBase = diag ? SQRT2 : 1f;
                float cellCost = _cost[nIdx];
                if (avoidRoads && IsRoadCell(nx, nz)) cellCost *= 12f;
                float nd = g[cur] + stepBase * cellCost;
                if (nd < g[nIdx])
                {
                    g[nIdx] = nd;
                    prev[nIdx] = cur;
                    heap.Push(nIdx, nd + Heuristic(nx, nz, tx, tz));
                }
            }
        }

        if (!found) return false;

        // Восстановление цепочки клеток (goal → start), затем разворот и пропуск стартовой клетки.
        var cells = new List<int>();
        int p = goal;
        while (p != -1) { cells.Add(p); if (p == start) break; p = prev[p]; }
        cells.Reverse();

        for (int i = 1; i < cells.Count; i++)
        {
            int ci = cells[i];
            // Сглаживание: пропускаем промежуточные клетки на одной прямой.
            if (i > 1 && i < cells.Count - 1 && Collinear(cells[i - 1], ci, cells[i + 1]))
                continue;
            result.Add(CellToWorld(ci / _d, ci % _d));
        }
        return result.Count > 0;
    }

    /// <summary>Ближайшая к pos проходимая мировая точка. found=false, если не нашли в радиусе.</summary>
    public Vector3 NearestWalkableWorld(Vector3 pos, out bool found)
    {
        found = false;
        if (!_ready) return pos;

        int cx, cz;
        WorldToCell(pos, out cx, out cz);
        if (_walkable[Idx(cx, cz)]) { found = true; return CellToWorld(cx, cz); }

        if (FindNearestWalkableCell(cx, cz, out int wx, out int wz))
        {
            found = true;
            return CellToWorld(wx, wz);
        }
        return pos;
    }

    public bool IsWalkableWorld(Vector3 pos)
    {
        if (!_ready) return true;
        int cx, cz;
        WorldToCell(pos, out cx, out cz);
        return _walkable[Idx(cx, cz)];
    }

    // ============ Helpers ============

    private bool FindNearestWalkableCell(int cx, int cz, out int rx, out int rz)
    {
        return FindNearestWalkableCell(cx, cz, out rx, out rz, avoidRoads: false);
    }

    private bool FindNearestWalkableCell(int cx, int cz, out int rx, out int rz, bool avoidRoads)
    {
        rx = cx; rz = cz;
        for (int r = 1; r <= nearestSearchRadius; r++)
        {
            for (int x = cx - r; x <= cx + r; x++)
            {
                for (int z = cz - r; z <= cz + r; z++)
                {
                    if (Mathf.Abs(x - cx) != r && Mathf.Abs(z - cz) != r) continue;
                    if (x < 0 || x >= _w || z < 0 || z >= _d) continue;
                    if (!_walkable[Idx(x, z)]) continue;
                    if (avoidRoads && IsRoadCell(x, z)) continue;
                    rx = x; rz = z; return true;
                }
            }
        }
        return false;
    }

    private bool IsRoadCell(int cx, int cz)
    {
        return mapGrid != null && mapGrid.IsReady
            && mapGrid.HasFlag(cx, cz, MapGrid.OccupancyFlags.Road);
    }

    private bool Collinear(int a, int b, int c)
    {
        int ax = a / _d, az = a % _d;
        int bx = b / _d, bz = b % _d;
        int cx = c / _d, cz = c % _d;
        return (bx - ax) == (cx - bx) && (bz - az) == (cz - bz);
    }

    private float Heuristic(int x1, int z1, int x2, int z2)
    {
        int dx = Mathf.Abs(x1 - x2);
        int dz = Mathf.Abs(z1 - z2);
        // Октиль-дистанция при минимальной стоимости клетки = 1 (допустимая эвристика).
        return (dx + dz) + (SQRT2 - 2f) * Mathf.Min(dx, dz);
    }

    private int Idx(int x, int z) => x * _d + z;

    private void WorldToCell(Vector3 world, out int cx, out int cz)
    {
        cx = Mathf.Clamp(Mathf.FloorToInt((world.x - _origin.x) / _ts + 0.5f), 0, _w - 1);
        cz = Mathf.Clamp(Mathf.FloorToInt((world.z - _origin.z) / _ts + 0.5f), 0, _d - 1);
    }

    private Vector3 CellToWorld(int x, int z)
    {
        float h = heightSource.GetHeight(x, z);
        return new Vector3(_origin.x + x * _ts, h, _origin.z + z * _ts);
    }

    private float ResolveTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 4f;
    }

    // ============ Бинарная куча (как в RoadGenerator) ============

    private class MinHeap
    {
        private int[] nodes;
        private float[] costs;
        private int count;

        public MinHeap(int capacity)
        {
            int cap = Mathf.Max(capacity, 4);
            nodes = new int[cap + 1];
            costs = new float[cap + 1];
            count = 0;
        }

        public int Count => count;

        public void Push(int node, float cost)
        {
            if (count + 2 >= nodes.Length)
            {
                System.Array.Resize(ref nodes, nodes.Length * 2);
                System.Array.Resize(ref costs, costs.Length * 2);
            }
            count++;
            nodes[count] = node;
            costs[count] = cost;
            int i = count;
            while (i > 1 && costs[i] < costs[i / 2]) { Swap(i, i / 2); i /= 2; }
        }

        public int Pop()
        {
            int root = nodes[1];
            nodes[1] = nodes[count];
            costs[1] = costs[count];
            count--;
            int i = 1;
            while (true)
            {
                int l = i * 2, r = i * 2 + 1, smallest = i;
                if (l <= count && costs[l] < costs[smallest]) smallest = l;
                if (r <= count && costs[r] < costs[smallest]) smallest = r;
                if (smallest == i) break;
                Swap(i, smallest);
                i = smallest;
            }
            return root;
        }

        private void Swap(int a, int b)
        {
            int tn = nodes[a]; nodes[a] = nodes[b]; nodes[b] = tn;
            float tc = costs[a]; costs[a] = costs[b]; costs[b] = tc;
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawGizmos || !_ready || !Application.isPlaying) return;
        for (int x = 0; x < _w; x++)
            for (int z = 0; z < _d; z++)
            {
                int idx = Idx(x, z);
                Vector3 c = CellToWorld(x, z) + Vector3.up * 0.1f;
                if (!_walkable[idx]) { Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.5f); }
                else if (_cost[idx] > 1f) { Gizmos.color = new Color(0.2f, 0.4f, 1f, 0.35f); }
                else continue;
                Gizmos.DrawCube(c, new Vector3(_ts * 0.85f, 0.05f, _ts * 0.85f));
            }
    }
#endif
}
