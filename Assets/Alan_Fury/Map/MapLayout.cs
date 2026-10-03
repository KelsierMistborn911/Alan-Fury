using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Процедурная дорога от центра одного короткого края карты до центра другого.
/// Маршрут — Дейкстра по рельефу плюс боковая волна. Пишет Road в MapGrid.
/// Вызывается из TerrainManager до объектов.
/// </summary>
public class MapLayout : MonoBehaviour
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
    [Tooltip("Боковой размах волны, клетки. Дорога всё равно край в край.")]
    public float waveAmplitude = 70f;
    [Tooltip("Длина волны вдоль длинной оси, клетки.")]
    public float waveWavelength = 240f;
    [Tooltip("Вторая волна, доля от амплитуды. Чтобы не была синусом из линейки.")]
    [Range(0f, 1f)] public float waveHarmonic = 0.4f;

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

    [Header("Зоны")]
    [Tooltip("Роща в обе стороны от оси дороги, клетки.")]
    public int groveBandCells = 14;
    [Tooltip("Чаща по краю карты, клетки.")]
    public int edgeThicketCells = 22;
    [Tooltip("Меньше = крупнее островки чащи.")]
    public float zoneNoiseScale = 0.012f;
    [Range(0f, 1f)] public float thicketIslandThreshold = 0.78f;
    public Vector2 zoneNoiseOffset = new Vector2(17.3f, 91.7f);
    public int largeClearingCount = 5;
    public int smallClearingCount = 10;
    public int largeClearingMinRadius = 5;
    public int largeClearingMaxRadius = 10;
    public int smallClearingMinRadius = 3;
    public int smallClearingMaxRadius = 5;
    public int clearingSeed = 4912;

    [Header("Разметка в сцене")]
    public bool showZoneMarkup = true;
    [Tooltip("Грубый шаг заливки, клетки. Не повторяет рельеф.")]
    public int markupBlock = 16;
    [Tooltip("Плоская высота заливки над нулём, метры. Выше рельефа, одна плоскость.")]
    public float markupHeight = 18f;
    public Color groveGizmoColor = new Color(0.55f, 0.95f, 0.32f, 0.55f);
    public Color forestGizmoColor = new Color(0.14f, 0.55f, 0.22f, 0.35f);
    public Color thicketGizmoColor = new Color(0.02f, 0.18f, 0.05f, 0.55f);
    public Color clearingGizmoColor = new Color(0.95f, 0.82f, 0.18f, 0.55f);
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
    private int pathVersion;
    private byte[,] zones;
    private int zoneW, zoneD, zonePathVersion = -1;
    private int markupKey = int.MinValue;
    private readonly List<RectInt> groveRects = new List<RectInt>();
    private readonly List<RectInt> forestRects = new List<RectInt>();
    private readonly List<RectInt> thicketRects = new List<RectInt>();
    private readonly List<RectInt> clearingRects = new List<RectInt>();

    public int PathVersion => pathVersion;

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
        BuildSpine();
        FinishRoad();
    }

    /// <summary>Ось дороги до карты высот. Не ищет путь по клеткам.</summary>
    public void BuildSpine()
    {
        if (heightSource == null) heightSource = GetComponent<HeightMapGenerator>();
        if (heightSource == null)
        {
            Debug.LogError("MapLayout: нет HeightMapGenerator.");
            return;
        }
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        ClearRoad();
        width = heightSource.width;
        depth = heightSource.depth;
        tileSize = ResolveTileSize();
        mapOrigin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        path = BuildWavePath();
        pathVersion++;
        Debug.Log($"MapLayout: ось дороги {path.Count} узлов.");
    }

    /// <summary>Полотно, выравнивание высот и зоны. Карта высот уже есть.</summary>
    public void FinishRoad()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("MapLayout: карта высот ещё не готова.");
            return;
        }
        if (path.Count == 0) BuildSpine();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();
        roadCells = new bool[width, depth];
        StampRoad();
        FlattenAlongRoad();
        BuildRoadMesh();
        isBuilt = true;
        pathVersion++;
        BuildZones();
        Debug.Log($"MapLayout: полотно готово, узлов {path.Count}, ширина {roadWidth}.");
    }

    public void ClearRoad()
    {
        roadCells = null;
        path.Clear();
        isBuilt = false;
        pathVersion++;
        DestroyRoadMesh();
    }

    /// <summary>Желаемая боковая клетка волны. along — вдоль длинной оси.</summary>
    public float DesiredLateral(float along)
    {
        int w = width;
        int d = depth;
        if (w <= 0 || d <= 0)
        {
            if (heightSource == null) return 0f;
            w = heightSource.width;
            d = heightSource.depth;
        }
        float center = (w >= d ? d : w) * 0.5f;
        float wl = Mathf.Max(8f, waveWavelength);
        float phase = along / wl * Mathf.PI * 2f;
        return center
            + Mathf.Sin(phase) * waveAmplitude
            + Mathf.Sin(phase * 2.15f + 1.4f) * waveAmplitude * waveHarmonic;
    }

    // Ось дороги — волна вдоль длинной стороны. Без поиска по карте.
    private List<Vector2Int> BuildWavePath()
    {
        var result = new List<Vector2Int>(Mathf.Max(width, depth));
        bool alongX = width >= depth;
        int alongN = alongX ? width : depth;
        int lateralN = alongX ? depth : width;
        int prevX = -1, prevZ = -1;
        for (int along = 0; along < alongN; along++)
        {
            int lat = Mathf.Clamp(Mathf.RoundToInt(DesiredLateral(along)), 0, lateralN - 1);
            int x = alongX ? along : lat;
            int z = alongX ? lat : along;
            if (prevX >= 0) AppendLine(result, prevX, prevZ, x, z);
            else result.Add(new Vector2Int(x, z));
            prevX = x;
            prevZ = z;
        }
        return result;
    }

    private static void AppendLine(List<Vector2Int> dst, int x0, int z0, int x1, int z1)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dz = Mathf.Abs(z1 - z0);
        int sx = x0 < x1 ? 1 : -1;
        int sz = z0 < z1 ? 1 : -1;
        int err = dx - dz;
        while (x0 != x1 || z0 != z1)
        {
            int e2 = err * 2;
            if (e2 > -dz) { err -= dz; x0 += sx; }
            if (e2 < dx) { err += dx; z0 += sz; }
            dst.Add(new Vector2Int(x0, z0));
        }
    }

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

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                if (!roadCells[x, z]) continue;

                float h = heightSource.GetHeight(x, z) + meshHeightOffset;
                float x0 = mapOrigin.x + x * tileSize;
                float z0 = mapOrigin.z + z * tileSize;
                float x1 = x0 + tileSize;
                float z1 = z0 + tileSize;

                int b = verts.Count;
                verts.Add(ToMesh(x0, h, z0));
                verts.Add(ToMesh(x1, h, z0));
                verts.Add(ToMesh(x1, h, z1));
                verts.Add(ToMesh(x0, h, z1));

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
        roadMeshGO.transform.localPosition = Vector3.zero;
        roadMeshGO.transform.localRotation = Quaternion.identity;
        roadMeshGO.transform.localScale = Vector3.one;
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
            Debug.LogWarning("MapLayout: не найден URP-шейдер — проверь, что проект на URP.");
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

    private Vector3 ToWorld(float x, float y, float z)
    {
        Vector3 local = new Vector3(x, y, z);
        return chunkedBuilder != null ? chunkedBuilder.MapLocalToWorld(local) : local;
    }

    private Vector3 ToMesh(float x, float y, float z)
    {
        Vector3 world = ToWorld(x, y, z);
        return transform.InverseTransformPoint(world);
    }

    public NaturePlacement.Zone GetZone(int x, int z)
    {
        EnsureZones();
        if (zones == null || x < 0 || z < 0 || x >= zoneW || z >= zoneD) return NaturePlacement.Zone.None;
        return (NaturePlacement.Zone)zones[x, z];
    }

    public void EnsureZones()
    {
        if (heightSource == null) heightSource = GetComponent<HeightMapGenerator>();
        if (heightSource == null) return;
        int w = heightSource.width;
        int d = heightSource.depth;
        if (w <= 0 || d <= 0) return;
        if (zones != null && zoneW == w && zoneD == d && zonePathVersion == pathVersion) return;
        width = w;
        depth = d;
        BuildZones();
    }

    private void BuildZones()
    {
        if (heightSource == null) heightSource = GetComponent<HeightMapGenerator>();
        if (heightSource == null) return;
        int w = heightSource.width;
        int d = heightSource.depth;
        if (w <= 0 || d <= 0) return;
        width = w;
        depth = d;
        zones = new byte[w, d];
        zoneW = w;
        zoneD = d;
        zonePathVersion = pathVersion;
        markupKey = int.MinValue;

        int edge = Mathf.Max(0, edgeThicketCells);
        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < d; z++)
            {
                bool rim = x < edge || z < edge || x >= w - edge || z >= d - edge;
                if (rim)
                {
                    zones[x, z] = (byte)NaturePlacement.Zone.Thicket;
                    continue;
                }
                float n = Mathf.PerlinNoise(
                    (x + zoneNoiseOffset.x) * zoneNoiseScale,
                    (z + zoneNoiseOffset.y) * zoneNoiseScale);
                zones[x, z] = n >= thicketIslandThreshold
                    ? (byte)NaturePlacement.Zone.Thicket
                    : (byte)NaturePlacement.Zone.Forest;
            }
        }
        StampGrove(w, d);
        int large = StampClearings(w, d, largeClearingCount, largeClearingMinRadius, largeClearingMaxRadius, 3);
        int small = StampClearings(w, d, smallClearingCount, smallClearingMinRadius, smallClearingMaxRadius, 17);
        Debug.Log($"MapLayout: зоны {w}×{d}, поляны крупные {large}, мелкие {small}.");
    }

    private void StampGrove(int w, int d)
    {
        int band = Mathf.Max(1, groveBandCells);
        if (path != null && path.Count > 0)
        {
            for (int i = 0; i < path.Count; i++)
                StampGroveCell(path[i].x, path[i].y, band, w, d);
            return;
        }
        bool alongX = w >= d;
        int alongN = alongX ? w : d;
        int lateralN = alongX ? d : w;
        for (int along = 0; along < alongN; along++)
        {
            int lat = Mathf.Clamp(Mathf.RoundToInt(DesiredLateral(along)), 0, lateralN - 1);
            int x = alongX ? along : lat;
            int z = alongX ? lat : along;
            StampGroveCell(x, z, band, w, d);
        }
    }

    private void StampGroveCell(int cx, int cz, int band, int w, int d)
    {
        int x0 = Mathf.Max(0, cx - band);
        int x1 = Mathf.Min(w - 1, cx + band);
        int z0 = Mathf.Max(0, cz - band);
        int z1 = Mathf.Min(d - 1, cz + band);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                zones[x, z] = (byte)NaturePlacement.Zone.Grove;
    }

    private int StampClearings(int w, int d, int count, int minR, int maxR, int salt)
    {
        if (count <= 0) return 0;
        int lo = Mathf.Max(1, Mathf.Min(minR, maxR));
        int hi = Mathf.Max(lo, Mathf.Max(minR, maxR));
        int placed = 0;
        int attempt = 0;
        int guard = count * 40;
        while (placed < count && attempt < guard)
        {
            attempt++;
            int cx = 1 + HashRange(attempt, salt, w - 2);
            int cz = 1 + HashRange(attempt, salt + 9, d - 2);
            if (zones[cx, cz] != (byte)NaturePlacement.Zone.Forest) continue;
            int radius = lo + HashRange(attempt, salt + 21, hi - lo + 1);
            if (!ClearingFits(cx, cz, radius, w, d)) continue;
            int r2 = radius * radius;
            for (int x = Mathf.Max(0, cx - radius); x <= Mathf.Min(w - 1, cx + radius); x++)
                for (int z = Mathf.Max(0, cz - radius); z <= Mathf.Min(d - 1, cz + radius); z++)
                {
                    int dx = x - cx, dz = z - cz;
                    if (dx * dx + dz * dz > r2) continue;
                    if (zones[x, z] == (byte)NaturePlacement.Zone.Forest)
                        zones[x, z] = (byte)NaturePlacement.Zone.Clearing;
                }
            placed++;
        }
        return placed;
    }

    private bool ClearingFits(int cx, int cz, int radius, int w, int d)
    {
        if (cx - radius < 1 || cz - radius < 1 || cx + radius >= w - 1 || cz + radius >= d - 1) return false;
        int r2 = radius * radius;
        for (int x = cx - radius; x <= cx + radius; x++)
            for (int z = cz - radius; z <= cz + radius; z++)
            {
                int dx = x - cx, dz = z - cz;
                if (dx * dx + dz * dz > r2) continue;
                if (zones[x, z] != (byte)NaturePlacement.Zone.Forest) return false;
            }
        return true;
    }

    private static int HashRange(int n, int salt, int span)
    {
        if (span <= 1) return 0;
        unchecked
        {
            uint h = (uint)(n * 374761393 + salt * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= (h >> 16);
            return (int)(h % (uint)span);
        }
    }

    private void CacheZoneRects()
    {
        int key = zoneW * 100003 + zoneD + zonePathVersion * 17 + markupBlock * 131;
        if (markupKey == key && groveRects.Count + forestRects.Count + thicketRects.Count + clearingRects.Count > 0) return;
        markupKey = key;
        groveRects.Clear();
        forestRects.Clear();
        thicketRects.Clear();
        clearingRects.Clear();
        FillZoneRects((byte)NaturePlacement.Zone.Grove, groveRects);
        FillZoneRects((byte)NaturePlacement.Zone.Forest, forestRects);
        FillZoneRects((byte)NaturePlacement.Zone.Thicket, thicketRects);
        FillZoneRects((byte)NaturePlacement.Zone.Clearing, clearingRects);
    }

    private void FillZoneRects(byte want, List<RectInt> rects)
    {
        int step = Mathf.Max(4, markupBlock);
        int rows = (zoneD + step - 1) / step;
        int cols = (zoneW + step - 1) / step;
        for (int row = 0; row < rows; row++)
        {
            int z = row * step;
            int x = 0;
            while (x < cols)
            {
                if (BlockZone(x * step, z) != want) { x++; continue; }
                int x1 = x + 1;
                while (x1 < cols && BlockZone(x1 * step, z) == want) x1++;
                if (rects.Count > 0)
                {
                    RectInt prev = rects[rects.Count - 1];
                    if (prev.y + prev.height == z && prev.x == x * step && prev.width == (x1 - x) * step)
                    {
                        prev.height += step;
                        rects[rects.Count - 1] = prev;
                        x = x1;
                        continue;
                    }
                }
                rects.Add(new RectInt(x * step, z, (x1 - x) * step, step));
                x = x1;
            }
        }
    }

    private byte BlockZone(int x, int z)
    {
        x = Mathf.Clamp(x, 0, zoneW - 1);
        z = Mathf.Clamp(z, 0, zoneD - 1);
        return zones[x, z];
    }

    void OnEnable()
    {
        KillZoneMarkupObject();
    }

    private void KillZoneMarkupObject()
    {
        var old = transform.Find("ZoneMarkup");
        if (old == null) return;
        if (Application.isPlaying) Destroy(old.gameObject);
        else DestroyImmediate(old.gameObject);
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!showZoneMarkup) return;
        KillZoneMarkupObject();
        EnsureZones();
        if (zones == null) return;
        CacheZoneRects();
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        float ts = chunkedBuilder != null ? chunkedBuilder.tileSize : 4f;
        if (mapOrigin == Vector3.zero)
            mapOrigin = new Vector3(-zoneW * ts * 0.5f, 0f, -zoneD * ts * 0.5f);
        DrawZoneSheet(groveRects, groveGizmoColor, ts);
        DrawZoneSheet(forestRects, forestGizmoColor, ts);
        DrawZoneSheet(thicketRects, thicketGizmoColor, ts);
        DrawZoneSheet(clearingRects, clearingGizmoColor, ts);
    }

    private void DrawZoneSheet(List<RectInt> rects, Color color, float ts)
    {
        if (rects == null || rects.Count == 0) return;
        UnityEditor.Handles.color = color;
        float y = markupHeight;
        for (int i = 0; i < rects.Count; i++)
        {
            RectInt r = rects[i];
            Vector3 a = ToWorld(mapOrigin.x + r.x * ts, y, mapOrigin.z + r.y * ts);
            Vector3 b = ToWorld(mapOrigin.x + (r.x + r.width) * ts, y, mapOrigin.z + r.y * ts);
            Vector3 c = ToWorld(mapOrigin.x + (r.x + r.width) * ts, y, mapOrigin.z + (r.y + r.height) * ts);
            Vector3 d = ToWorld(mapOrigin.x + r.x * ts, y, mapOrigin.z + (r.y + r.height) * ts);
            UnityEditor.Handles.DrawSolidRectangleWithOutline(new[] { a, b, c, d }, color, color);
        }
    }
#endif
}
