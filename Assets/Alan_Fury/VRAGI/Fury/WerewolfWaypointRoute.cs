using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Замкнутый маршрут из точек. Пока точки берутся вдоль RoadGenerator.Path.
/// Вешается на волка или логово. Пустой список → патруль стоит.
/// </summary>
public class WerewolfWaypointRoute : MonoBehaviour, IWerewolfRoute
{
    [Tooltip("Точки обхода. Пусто — соберутся вдоль дороги при старте / ПКМ Build From Road.")]
    public Transform[] points;

    [Header("Дорога")]
    [Tooltip("Пусто — FindObjectOfType. Точки ставятся по центру клеток Path.")]
    public RoadGenerator road;
    [Tooltip("Шаг между точками вдоль дороги (м).")]
    public float roadSpacing = 18f;
    [Tooltip("На сколько метров увести точку с полотна в лес.")]
    public float roadsideMeters = 8f;
    [Tooltip("Сторона обочины. true = левая по ходу дороги.")]
    public bool roadsideLeft = true;
    [Tooltip("Потолок точек.")]
    public int maxRoadPoints = 14;
    [Tooltip("Если points пуст — собрать с дороги в Start.")]
    public bool buildFromRoadOnStart = true;

    [Tooltip("Радиус квадрата для Create Square Points (м). Запасной вариант.")]
    public float generateRadius = 14f;

    [Tooltip("Считать прибытием, если ближе этого (м).")]
    public float arriveDistance = 2f;

    private int _index;
    private bool _roadTried;
    private float _roadRetry;
    private int _roadAttempts;

    public bool HasPoints
    {
        get
        {
            if (points == null || points.Length == 0) return false;
            for (int i = 0; i < points.Length; i++)
                if (points[i] != null) return true;
            return false;
        }
    }

    public float ArriveDistance => arriveDistance;

    void Start()
    {
        if (buildFromRoadOnStart && !HasPoints)
            BuildFromRoad();
    }

    void Update()
    {
        if (!buildFromRoadOnStart || HasPoints || _roadTried) return;
        _roadRetry += Time.deltaTime;
        if (_roadRetry < 0.5f) return;
        _roadRetry = 0f;
        BuildFromRoad();
        _roadAttempts++;
        if (_roadAttempts >= 8) _roadTried = true;
    }

    public Vector3 CurrentPoint
    {
        get
        {
            Transform t = CurrentTransform();
            return t != null ? t.position : transform.position;
        }
    }

    public Vector3 ResumePoint(Vector3 from)
    {
        ResetToNearest(from);
        return CurrentPoint;
    }

    public Vector3 LookHint(Vector3 from)
    {
        Vector3 p = CurrentPoint;
        Vector3 d = p - from; d.y = 0f;
        if (d.sqrMagnitude < 0.01f) return from + transform.forward;
        return p;
    }

    public void Advance()
    {
        if (points == null || points.Length == 0) return;
        int n = points.Length;
        for (int k = 0; k < n; k++)
        {
            _index = (_index + 1) % n;
            if (points[_index] != null) return;
        }
    }

    public void ResetToNearest(Vector3 from)
    {
        if (points == null || points.Length == 0) return;
        int best = _index;
        float bestSq = float.MaxValue;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            float sq = FlatSqr(from, points[i].position);
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        _index = best;
    }

    private Transform CurrentTransform()
    {
        if (points == null || points.Length == 0) return null;
        if (_index < 0 || _index >= points.Length) _index = 0;
        if (points[_index] != null) return points[_index];
        for (int i = 0; i < points.Length; i++)
            if (points[i] != null) { _index = i; return points[i]; }
        return null;
    }

    [ContextMenu("Build From Road")]
    public void BuildFromRoad()
    {
        if (road == null) road = FindObjectOfType<RoadGenerator>();
        if (road == null || road.Path == null || road.Path.Count < 2)
            return;

        MapGrid grid = road.mapGrid;
        if (grid == null || !grid.IsReady) grid = FindObjectOfType<MapGrid>();
        if (grid == null || !grid.IsReady) return;

        var poly = new List<Vector3>(road.Path.Count);
        for (int i = 0; i < road.Path.Count; i++)
        {
            Vector2Int c = road.Path[i];
            poly.Add(grid.CellCenterWorld(c.x, c.y));
        }

        float spacing = Mathf.Max(4f, roadSpacing);
        var sampleIdx = new List<int>();
        sampleIdx.Add(0);
        float acc = 0f;
        for (int i = 1; i < poly.Count; i++)
        {
            acc += Vector3.Distance(poly[i - 1], poly[i]);
            if (acc >= spacing)
            {
                sampleIdx.Add(i);
                acc = 0f;
                if (sampleIdx.Count >= maxRoadPoints) break;
            }
        }
        int lastI = poly.Count - 1;
        if (sampleIdx[sampleIdx.Count - 1] != lastI)
        {
            if (sampleIdx.Count >= maxRoadPoints) sampleIdx[sampleIdx.Count - 1] = lastI;
            else sampleIdx.Add(lastI);
        }

        var samples = new List<Vector3>(sampleIdx.Count);
        for (int s = 0; s < sampleIdx.Count; s++)
            samples.Add(OffsetOffRoad(grid, poly, sampleIdx[s]));

        ClearGenerated("RoadWP_");
        Transform holder = GetRoadHolder();
        var list = new Transform[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var go = new GameObject("RoadWP_" + i);
            go.transform.SetParent(holder, true);
            go.transform.position = samples[i];
            list[i] = go.transform;
        }
        points = list;
        _roadTried = true;
        ResetToNearest(transform.position);
    }

    Vector3 OffsetOffRoad(MapGrid grid, List<Vector3> poly, int index)
    {
        Vector3 onRoad = poly[index];
        int i0 = Mathf.Max(0, index - 1);
        int i1 = Mathf.Min(poly.Count - 1, index + 1);
        Vector3 along = poly[i1] - poly[i0];
        along.y = 0f;
        if (along.sqrMagnitude < 0.01f) along = Vector3.forward;
        along.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, along);
        if (!roadsideLeft) side = -side;

        float want = Mathf.Max(grid.TileSize * 0.5f * Mathf.Max(1, road != null ? road.roadWidth : 2) + 2f, roadsideMeters);
        Vector3 prefer = OffsetAlongSide(grid, onRoad, side, want);
        if (prefer.sqrMagnitude > 0.01f) return prefer;
        Vector3 other = OffsetAlongSide(grid, onRoad, -side, want);
        if (other.sqrMagnitude > 0.01f) return other;
        return onRoad;
    }

    static Vector3 OffsetAlongSide(MapGrid grid, Vector3 from, Vector3 side, float meters)
    {
        float ts = grid.TileSize;
        int steps = Mathf.Max(2, Mathf.CeilToInt(meters / ts) + 3);
        Vector3 lastGood = Vector3.zero;
        bool found = false;
        for (int s = 1; s <= steps; s++)
        {
            Vector3 p = from + side * (s * ts);
            grid.WorldToCell(p, out int cx, out int cz);
            if (grid.IsBlocked(cx, cz)) continue;
            if (grid.HasFlag(cx, cz, MapGrid.OccupancyFlags.Road)) continue;
            lastGood = grid.CellCenterWorld(cx, cz);
            found = true;
            if (s * ts >= meters) break;
        }
        return found ? lastGood : Vector3.zero;
    }

    Transform GetRoadHolder()
    {
        Transform parent = road != null ? road.transform : transform;
        Transform holder = parent.Find("RoadWaypoints");
        if (holder != null) return holder;
        var go = new GameObject("RoadWaypoints");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        return go.transform;
    }

    void ClearGenerated(string prefix)
    {
        if (points == null) return;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            if (!points[i].name.StartsWith(prefix)) continue;
            if (Application.isPlaying) Destroy(points[i].gameObject);
            else DestroyImmediate(points[i].gameObject);
        }
    }

    [ContextMenu("Create Square Points")]
    public void CreateSquarePoints()
    {
        float r = Mathf.Max(2f, generateRadius);
        Vector3 o = transform.position;
        var list = new Transform[4];
        Vector3[] offs =
        {
            new Vector3(0f, 0f, r),
            new Vector3(r, 0f, 0f),
            new Vector3(0f, 0f, -r),
            new Vector3(-r, 0f, 0f)
        };
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject("WP_" + i);
            go.transform.SetParent(transform, true);
            go.transform.position = o + offs[i];
            list[i] = go.transform;
        }
        points = list;
    }

    static float FlatSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (points == null || points.Length == 0) return;
        Gizmos.color = new Color(0.4f, 0.75f, 0.35f, 0.9f);
        Vector3 prev = Vector3.zero;
        bool hasPrev = false;
        Vector3 first = Vector3.zero;
        bool hasFirst = false;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            Vector3 p = points[i].position;
            Gizmos.DrawWireSphere(p, 0.4f);
            if (hasPrev) Gizmos.DrawLine(prev, p);
            if (!hasFirst) { first = p; hasFirst = true; }
            prev = p; hasPrev = true;
        }
        if (hasFirst && hasPrev) Gizmos.DrawLine(prev, first);
    }
#endif
}
