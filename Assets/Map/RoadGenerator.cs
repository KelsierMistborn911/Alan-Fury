using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Процедурная дорога от центра одного короткого края карты до центра другого.
/// Маршрут — Дейкстра по рельефу. Пишет Road в MapGrid.
/// Вызывается из TerrainManager до объектов.
/// </summary>
public class RoadGenerator : MonoBehaviour
{
    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;
    public MapGrid mapGrid;

    [Header("Дорога")]
    [Tooltip("Ширина дороги в клетках.")]
    public int roadWidth = 2;
    [Tooltip("Штраф за перепад высоты между соседними клетками. Выше = дорога ровнее, но длиннее.")]
    public float heightWeight = 8f;

    [Header("Меш дороги")]
    [Tooltip("Строить плоский меш дороги (квадры по тайлам).")]
    public bool buildRoadMesh = true;
    [Tooltip("Материал дороги. Если пусто — создаётся простой URP/Lit.")]
    public Material roadMaterial;
    public Color roadColor = new Color(0.45f, 0.38f, 0.28f);
    [Tooltip("Подъём над землёй, чтобы дорога не мерцала с тайлами.")]
    public float meshHeightOffset = 0.02f;

    [Header("Выравнивание высот под дорогой")]
    [Tooltip("Сглаживать карту высот вдоль дороги (полотно + обочина). TerrainManager пересоберёт меш.")]
    public bool flattenAlongRoad = true;
    [Tooltip("Макс. перепад высоты между соседними клетками дороги вдоль маршрута (м).")]
    public float maxHeightStep = 0.03f;
    [Tooltip("Ширина обочины в клетках с каждой стороны от полотна.")]
    public int shoulderCells = 1;
    [Tooltip("Насколько обочина подтягивается к высоте дороги (0 — не трогать, 1 — вровень).")]
    [Range(0f, 1f)] public float shoulderBlend = 0.5f;

    [Header("Гизмо")]
    public bool showGizmos = true;
    public Color roadGizmoColor = new Color(0.6f, 0.5f, 0.2f, 0.75f);
    public float gizmoHeight = 0.06f;

    private bool[,] roadCells;
    private GameObject roadMeshGO;
    private List<Vector2Int> path = new List<Vector2Int>();
    private int width, depth;
    private float tileSize;
    private Vector3 mapOrigin;
    private bool isBuilt;

    // ============ Публичный доступ ============

    public bool IsRoad(int x, int z)
    {
        if (roadCells == null) return false;
        if (x < 0 || x >= width || z < 0 || z >= depth) return false;
        return roadCells[x, z];
    }

    public List<Vector2Int> Path => path;

    // ============ Генерация ============

    public void GenerateRoad()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("RoadGenerator: нужен инициализированный HeightMapGenerator!");
            return;
        }

        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();

        ClearRoad();

        width = heightSource.width;
        depth = heightSource.depth;
        tileSize = ResolveTileSize();
        mapOrigin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);

        // Старт и финиш — центры коротких краёв. Дорога идёт вдоль длинной оси карты.
        Vector2Int start, end;
        if (width >= depth)
        {
            // длинная ось — X
            start = new Vector2Int(0, depth / 2);
            end = new Vector2Int(width - 1, depth / 2);
        }
        else
        {
            // длинная ось — Z
            start = new Vector2Int(width / 2, 0);
            end = new Vector2Int(width / 2, depth - 1);
        }

        path = FindPath(start, end);
        if (path.Count == 0)
        {
            Debug.LogWarning("RoadGenerator: путь не найден.");
            return;
        }

        roadCells = new bool[width, depth];
        StampRoad();
        FlattenAlongRoad();
        BuildRoadMesh();
        isBuilt = true;

        Debug.Log($"RoadGenerator: дорога построена, узлов {path.Count}, ширина {roadWidth}.");
    }

    public void ClearRoad()
    {
        roadCells = null;
        path.Clear();
        isBuilt = false;
        DestroyRoadMesh();
    }

    // ============ Поиск пути (Дейкстра, 8 соседей) ============

    private List<Vector2Int> FindPath(Vector2Int start, Vector2Int end)
    {
        int n = width * depth;
        float[] dist = new float[n];
        int[] prev = new int[n];
        bool[] done = new bool[n];
        for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }

        int startIdx = Idx(start.x, start.y);
        int endIdx = Idx(end.x, end.y);
        dist[startIdx] = 0f;

        var heap = new MinHeap(n + 1);
        heap.Push(startIdx, 0f);

        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

        while (heap.Count > 0)
        {
            int cur = heap.Pop();
            if (done[cur]) continue;
            done[cur] = true;
            if (cur == endIdx) break;

            int cx = cur / depth;
            int cz = cur % depth;
            float curH = heightSource.GetHeight(cx, cz);

            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k];
                int nz = cz + dz[k];
                if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;

                int nIdx = Idx(nx, nz);
                if (done[nIdx]) continue;

                float baseCost = (dx[k] != 0 && dz[k] != 0) ? 1.41421356f : 1f;
                float dh = Mathf.Abs(heightSource.GetHeight(nx, nz) - curH);
                float stepCost = baseCost + dh * heightWeight;

                float nd = dist[cur] + stepCost;
                if (nd < dist[nIdx])
                {
                    dist[nIdx] = nd;
                    prev[nIdx] = cur;
                    heap.Push(nIdx, nd);
                }
            }
        }

        var result = new List<Vector2Int>();
        if (startIdx != endIdx && prev[endIdx] == -1) return result; // не дошли

        int p = endIdx;
        while (p != -1)
        {
            result.Add(new Vector2Int(p / depth, p % depth));
            if (p == startIdx) break;
            p = prev[p];
        }
        result.Reverse();
        return result;
    }

    private int Idx(int x, int z) => x * depth + z;

    // ============ Нанесение дороги ============

    private void StampRoad()
    {
        int half = roadWidth / 2;

        foreach (Vector2Int c in path)
        {
            for (int ox = 0; ox < roadWidth; ox++)
            {
                for (int oz = 0; oz < roadWidth; oz++)
                {
                    int x = c.x + ox - half;
                    int z = c.y + oz - half;
                    if (x < 0 || x >= width || z < 0 || z >= depth) continue;

                    roadCells[x, z] = true;
                    if (mapGrid != null && mapGrid.IsReady)
                        mapGrid.Occupy(x, z, 1, 1, MapGrid.OccupancyFlags.Road, anchorCenter: true);
                }
            }
        }
    }

    // ============ Выравнивание высот вдоль дороги ============

    /// <summary>Сглаживает высоты под полотном и обочиной (без воды/мостов).</summary>
    private void FlattenAlongRoad()
    {
        if (!flattenAlongRoad) return;
        if (path.Count == 0 || roadCells == null) return;

        float[,] hm = heightSource.heightMap;
        if (hm == null) return;

        int n = path.Count;
        float[] profile = new float[n];
        for (int i = 0; i < n; i++)
            profile[i] = hm[path[i].x, path[i].y];

        float step = Mathf.Max(0f, maxHeightStep);
        for (int i = 1; i < n; i++)
            profile[i] = Mathf.Clamp(profile[i], profile[i - 1] - step, profile[i - 1] + step);
        for (int i = n - 2; i >= 0; i--)
            profile[i] = Mathf.Clamp(profile[i], profile[i + 1] - step, profile[i + 1] + step);

        int half = roadWidth / 2;
        int span = half + Mathf.Max(0, shoulderCells);
        float blend = Mathf.Clamp01(shoulderBlend);

        for (int i = 0; i < n; i++)
        {
            Vector2Int c = path[i];
            float target = profile[i];
            for (int ox = -span; ox <= span; ox++)
            {
                for (int oz = -span; oz <= span; oz++)
                {
                    int x = c.x + ox;
                    int z = c.y + oz;
                    if (x < 0 || x >= width || z < 0 || z >= depth) continue;

                    if (roadCells[x, z])
                        hm[x, z] = target;
                    else
                        hm[x, z] = Mathf.Lerp(hm[x, z], target, blend);
                }
            }
        }
    }

    // ============ Меш дороги ============

    /// <summary>
    /// Строит один плоский меш: по квадру на каждый дорожный тайл, на высоте этого тайла.
    /// Порядок вершин — как у верхней грани тайла в ChunkedTerrainBuilder (смотрит вверх).
    /// </summary>
    private void BuildRoadMesh()
    {
        DestroyRoadMesh();
        if (!buildRoadMesh || roadCells == null) return;

        var verts = new List<Vector3>();
        var tris = new List<int>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();

        float half = tileSize / 2f;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                if (!roadCells[x, z]) continue;

                float h = heightSource.GetHeight(x, z) + meshHeightOffset;
                float cx = mapOrigin.x + x * tileSize;
                float cz = mapOrigin.z + z * tileSize;

                int b = verts.Count;
                verts.Add(new Vector3(cx - half, h, cz - half));
                verts.Add(new Vector3(cx + half, h, cz - half));
                verts.Add(new Vector3(cx + half, h, cz + half));
                verts.Add(new Vector3(cx - half, h, cz + half));

                // UV тайлятся непрерывно по клеткам — под текстуру дороги.
                uvs.Add(new Vector2(x, z));
                uvs.Add(new Vector2(x + 1, z));
                uvs.Add(new Vector2(x + 1, z + 1));
                uvs.Add(new Vector2(x, z + 1));

                normals.Add(Vector3.up); normals.Add(Vector3.up);
                normals.Add(Vector3.up); normals.Add(Vector3.up);

                tris.Add(b + 0); tris.Add(b + 3); tris.Add(b + 2);
                tris.Add(b + 0); tris.Add(b + 2); tris.Add(b + 1);
            }
        }

        if (verts.Count == 0) return;

        var mesh = new Mesh { name = "RoadMesh" };
        if (verts.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetUVs(0, uvs);
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();

        roadMeshGO = new GameObject("RoadMesh");
        roadMeshGO.transform.SetParent(transform, false);
        roadMeshGO.AddComponent<MeshFilter>().sharedMesh = mesh;
        roadMeshGO.AddComponent<MeshRenderer>().sharedMaterial =
            roadMaterial != null ? roadMaterial : CreateDefaultRoadMaterial();
    }

    private Material CreateDefaultRoadMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            Debug.LogWarning("RoadGenerator: не найден URP-шейдер — проверь, что проект на URP.");
            shader = Shader.Find("Sprites/Default");
        }

        var mat = new Material(shader);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", roadColor);
        else mat.color = roadColor;
        return mat;
    }

    private void DestroyRoadMesh()
    {
        if (roadMeshGO == null) return;
        if (Application.isPlaying) Destroy(roadMeshGO);
        else DestroyImmediate(roadMeshGO);
        roadMeshGO = null;
    }

    private float ResolveTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 1f;
    }

    // ============ Бинарная куча для Дейкстры ============

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
            while (i > 1 && costs[i] < costs[i / 2])
            {
                Swap(i, i / 2);
                i /= 2;
            }
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
        if (!showGizmos || !Application.isPlaying) return;
        if (!isBuilt || roadCells == null || heightSource == null) return;

        Gizmos.color = roadGizmoColor;
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                if (!roadCells[x, z]) continue;

                float h = heightSource.GetHeight(x, z);
                Vector3 center = mapOrigin + new Vector3(x * tileSize, h + gizmoHeight, z * tileSize);
                Gizmos.DrawCube(center, new Vector3(tileSize * 0.9f, 0.05f, tileSize * 0.9f));
            }
        }
    }
#endif
}
