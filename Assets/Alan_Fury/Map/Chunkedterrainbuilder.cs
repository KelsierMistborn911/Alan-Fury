using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Строит ландшафт разбитым на чанки. Клетка (x,z) = квад на своей высоте heights[x,z].
/// Разница с соседом <= slopeThreshold — обе клетки плоские, стык закрывает вертикальная стенка.
/// Разница > slopeThreshold — переходный скат: общие углы усредняются, клетки наклоняются друг к другу.
/// Угол из двух стенок (сосед слева и снизу на одной высоте) — срез клетки по диагонали
/// с одной диагональной стенкой вместо ступеньки.
/// Все высоты углов считаются один раз в общую сетку (ComputeCorners) — трещины исключены.
/// </summary>
public class ChunkedTerrainBuilder : MonoBehaviour
{
    [Header("Источник высот")]
    public HeightMapGenerator heightSource;

    [Header("Размер тайла")]
    public float tileSize = 4f;

    [Header("Склоны / стенки")]
    [Tooltip("Разница высот с соседом ДО этого порога — вертикальная стенка. БОЛЬШЕ порога — плавный скат.")]
    public float slopeThreshold = 0.2f;

    [Header("Размер чанка (в тайлах)")]
    [Tooltip("10 = каждый чанк покрывает 10×10 тайлов. Для карты 40×40 получится 16 чанков.")]
    public int chunkSize = 10;

    [Header("Цвета")]
    public Color lowColor = new Color(0.3f, 0.3f, 0.3f);
    public Color highColor = new Color(0.9f, 0.9f, 0.9f);

    [Header("Материал (опционально)")]
    [Tooltip("Если не задан — используется Standard с вершинными цветами")]
    public Material terrainMaterial;

    private const float Eps = 0.001f;

    private List<GameObject> chunks = new List<GameObject>();
    private float[,] heights;
    private int mapWidth, mapDepth;
    private Vector3 mapOrigin;

    // Углы клетки: 0=SW(x,z) 1=SE(x+1,z) 2=NE(x+1,z+1) 3=NW(x,z+1)
    private float[,,] cornerH;      // [x, z, 4] — высота каждого угла каждой клетки, считается один раз
    private int[,] splitCorner;     // -1 = нет среза; иначе индекс угла, срезанного диагональю
    private float[,] splitLow;      // высота нижнего (срезанного) треугольника

    public float TileSize => tileSize;

    public void BuildTerrain()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("ChunkedTerrainBuilder: карта высот не готова!");
            return;
        }

        ClearTerrain();

        heights = heightSource.heightMap;
        mapWidth = heights.GetLength(0);
        mapDepth = heights.GetLength(1);
        mapOrigin = new Vector3(-mapWidth * tileSize / 2f, 0, -mapDepth * tileSize / 2f);

        ComputeCorners();
        ComputeSplits();

        int chunksX = Mathf.CeilToInt((float)mapWidth / chunkSize);
        int chunksZ = Mathf.CeilToInt((float)mapDepth / chunkSize);

        for (int cx = 0; cx < chunksX; cx++)
            for (int cz = 0; cz < chunksZ; cz++)
                BuildChunk(cx, cz);

        Debug.Log($"ChunkedTerrainBuilder: построено {chunks.Count} чанков ({chunksX}×{chunksZ}), карта {mapWidth}×{mapDepth}");
    }

    public void ClearTerrain()
    {
        foreach (var chunk in chunks)
        {
            if (chunk == null) continue;
            if (Application.isPlaying) Destroy(chunk);
            else DestroyImmediate(chunk);
        }
        chunks.Clear();
    }

    // =================== Единая сетка углов ===================

    /// <summary>Две клетки «свариваются» в общем углу: одинаковая высота — просто общая вершина;
    /// разница больше порога — скат (угол усредняется). Разница в пределах порога — НЕ свариваются,
    /// каждая держит свою высоту, зазор закроет стенка.</summary>
    private bool Welds(float a, float b)
    {
        float d = Mathf.Abs(a - b);
        return d < Eps || d > slopeThreshold;
    }

    /// <summary>Один проход по всем узлам сетки: до 4 клеток вокруг узла группируются
    /// по правилу Welds, каждая группа получает среднюю высоту. Результат в cornerH —
    /// обе стороны любого ребра читают одни и те же числа, трещины невозможны.</summary>
    private void ComputeCorners()
    {
        cornerH = new float[mapWidth, mapDepth, 4];
        for (int x = 0; x < mapWidth; x++)
            for (int z = 0; z < mapDepth; z++)
                for (int k = 0; k < 4; k++)
                    cornerH[x, z, k] = heights[x, z];

        // Соседи узла (gx,gz): клетка (gx-1,gz-1) касается углом NE(2), (gx,gz-1) — NW(3),
        // (gx-1,gz) — SE(1), (gx,gz) — SW(0)
        int[] offX = { -1, 0, -1, 0 };
        int[] offZ = { -1, -1, 0, 0 };
        int[] cornerIdx = { 2, 3, 1, 0 };

        var cells = new List<int>(4);   // индексы 0..3 из таблиц выше
        var group = new int[4];

        for (int gx = 0; gx <= mapWidth; gx++)
            for (int gz = 0; gz <= mapDepth; gz++)
            {
                cells.Clear();
                for (int i = 0; i < 4; i++)
                {
                    int cx = gx + offX[i], cz = gz + offZ[i];
                    if (cx >= 0 && cx < mapWidth && cz >= 0 && cz < mapDepth)
                        cells.Add(i);
                }
                if (cells.Count < 2) continue;

                for (int i = 0; i < cells.Count; i++) group[i] = i;

                // объединяем группы по правилу Welds (все пары, с транзитивностью)
                bool merged = true;
                while (merged)
                {
                    merged = false;
                    for (int a = 0; a < cells.Count; a++)
                        for (int b = a + 1; b < cells.Count; b++)
                        {
                            if (group[a] == group[b]) continue;
                            float ha = heights[gx + offX[cells[a]], gz + offZ[cells[a]]];
                            float hb = heights[gx + offX[cells[b]], gz + offZ[cells[b]]];
                            if (!Welds(ha, hb)) continue;
                            int from = group[b], to = group[a];
                            for (int j = 0; j < cells.Count; j++)
                                if (group[j] == from) group[j] = to;
                            merged = true;
                        }
                }

                for (int g = 0; g < cells.Count; g++)
                {
                    float sum = 0f; int n = 0;
                    for (int j = 0; j < cells.Count; j++)
                        if (group[j] == g)
                        {
                            sum += heights[gx + offX[cells[j]], gz + offZ[cells[j]]];
                            n++;
                        }
                    if (n < 2) continue; // одиночка остаётся на своей высоте
                    float avg = sum / n;
                    for (int j = 0; j < cells.Count; j++)
                        if (group[j] == g)
                        {
                            int i = cells[j];
                            cornerH[gx + offX[i], gz + offZ[i], cornerIdx[i]] = avg;
                        }
                }
            }
    }

    // =================== Диагональные срезы ===================

    /// <summary>Ищет клетки-уголки: два перпендикулярных соседа на одной высоте, оба через стенку.
    /// Такой угол режется диагональю — вместо ступеньки одна диагональная стенка.</summary>
    private void ComputeSplits()
    {
        splitCorner = new int[mapWidth, mapDepth];
        splitLow = new float[mapWidth, mapDepth];

        // для угла k: пара перпендикулярных соседей и диагональный сосед
        int[,] nb = {
            { -1, 0, 0, -1, -1, -1 }, // SW: W, S, диаг SW
            {  1, 0, 0, -1,  1, -1 }, // SE: E, S, диаг SE
            {  1, 0, 0,  1,  1,  1 }, // NE: E, N, диаг NE
            { -1, 0, 0,  1, -1,  1 }, // NW: W, N, диаг NW
        };

        for (int x = 0; x < mapWidth; x++)
            for (int z = 0; z < mapDepth; z++)
            {
                splitCorner[x, z] = -1;
                if (!IsFlat(x, z)) continue; // клетка со скатом не режется
                float h = heights[x, z];

                int found = -1; float foundLow = 0f; int count = 0;
                for (int k = 0; k < 4; k++)
                {
                    int ax = x + nb[k, 0], az = z + nb[k, 1];
                    int bx = x + nb[k, 2], bz = z + nb[k, 3];
                    int dx = x + nb[k, 4], dz = z + nb[k, 5];
                    if (!InMap(ax, az) || !InMap(bx, bz) || !InMap(dx, dz)) continue;

                    float ha = heights[ax, az], hb = heights[bx, bz], hd = heights[dx, dz];
                    if (h <= ha) continue;                                    // режется только высокая клетка
                    if (!IsWallDiff(h, ha) || !IsWallDiff(h, hb)) continue;   // оба соседа через стенку
                    if (Mathf.Abs(ha - hb) > Eps || Mathf.Abs(hd - ha) > Eps) continue; // одна высота вокруг угла
                    if (!IsFlat(ax, az) || !IsFlat(bx, bz)) continue;         // соседи тоже плоские

                    found = k; foundLow = ha; count++;
                }

                if (count == 1) // ровно один угол — иначе (пик/перешеек) оставляем ступеньку
                {
                    splitCorner[x, z] = found;
                    splitLow[x, z] = foundLow;
                }
            }
    }

    private bool InMap(int x, int z) => x >= 0 && x < mapWidth && z >= 0 && z < mapDepth;

    private bool IsWallDiff(float a, float b)
    {
        float d = Mathf.Abs(a - b);
        return d >= Eps && d <= slopeThreshold;
    }

    private bool IsFlat(int x, int z)
    {
        float h = heights[x, z];
        for (int k = 0; k < 4; k++)
            if (Mathf.Abs(cornerH[x, z, k] - h) > Eps) return false;
        return true;
    }

    // =================== Чанки ===================

    private void BuildChunk(int chunkX, int chunkZ)
    {
        int startX = chunkX * chunkSize;
        int startZ = chunkZ * chunkSize;
        int endX = Mathf.Min(startX + chunkSize, mapWidth);
        int endZ = Mathf.Min(startZ + chunkSize, mapDepth);

        var verts = new List<Vector3>();
        var tris = new List<int>();
        var colors = new List<Color>();
        var cache = new Dictionary<long, int>();

        for (int x = startX; x < endX; x++)
            for (int z = startZ; z < endZ; z++)
                AddCell(x, z, verts, tris, colors, cache);

        if (verts.Count == 0) return;

        var chunkGO = new GameObject($"Chunk_{chunkX}_{chunkZ}");
        chunkGO.transform.SetParent(transform);
        chunkGO.isStatic = true;

        int terrainLayer = LayerMask.NameToLayer("Terrain");
        if (terrainLayer >= 0)
            chunkGO.layer = terrainLayer;
        else
            Debug.LogWarning("Слой 'Terrain' не найден в Tags & Layers — чанк остался на слое по умолчанию.");

        var mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.colors = colors.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.Optimize();

        chunkGO.AddComponent<MeshFilter>().sharedMesh = mesh;

        var mr = chunkGO.AddComponent<MeshRenderer>();
        mr.sharedMaterial = terrainMaterial != null
            ? terrainMaterial
            : new Material(Shader.Find("Standard"));

        var mc = chunkGO.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;

        chunks.Add(chunkGO);
    }

    // =================== Геометрия клетки ===================

    private void AddCell(int x, int z,
        List<Vector3> verts, List<int> tris, List<Color> colors,
        Dictionary<long, int> cache)
    {
        float x0 = CellX(x), x1 = CellX(x + 1);
        float z0 = CellZ(z), z1 = CellZ(z + 1);

        if (splitCorner[x, z] >= 0)
            AddSplitCell(x, z, x0, x1, z0, z1, verts, tris, colors, cache);
        else
        {
            int v00 = GetVert(x0, cornerH[x, z, 0], z0, verts, colors, cache);
            int v10 = GetVert(x1, cornerH[x, z, 1], z0, verts, colors, cache);
            int v11 = GetVert(x1, cornerH[x, z, 2], z1, verts, colors, cache);
            int v01 = GetVert(x0, cornerH[x, z, 3], z1, verts, colors, cache);

            tris.Add(v00); tris.Add(v01); tris.Add(v11);
            tris.Add(v00); tris.Add(v11); tris.Add(v10);
        }

        // Стенки: каждая клетка отвечает только за свои E и N рёбра — каждое ребро карты
        // обрабатывается ровно один раз, дублей и расхождений быть не может.
        if (x + 1 < mapWidth)
        {
            GetEdge(x, z, 1, 2, out float myA, out float myB);       // моё E ребро: SE→NE
            GetEdge(x + 1, z, 0, 3, out float nbA, out float nbB);   // его W ребро: SW→NW
            if (Mathf.Abs(myA - nbA) > Eps || Mathf.Abs(myB - nbB) > Eps)
            {
                Color c = HeightColor(Mathf.Min(Mathf.Min(myA, myB), Mathf.Min(nbA, nbB)));
                AddVQuad(new Vector3(x1, myA, z0), new Vector3(x1, nbA, z0),
                         new Vector3(x1, myB, z1), new Vector3(x1, nbB, z1), c, verts, tris, colors);
            }
        }
        if (z + 1 < mapDepth)
        {
            GetEdge(x, z, 3, 2, out float myA, out float myB);       // моё N ребро: NW→NE
            GetEdge(x, z + 1, 0, 1, out float nbA, out float nbB);   // его S ребро: SW→SE
            if (Mathf.Abs(myA - nbA) > Eps || Mathf.Abs(myB - nbB) > Eps)
            {
                Color c = HeightColor(Mathf.Min(Mathf.Min(myA, myB), Mathf.Min(nbA, nbB)));
                AddVQuad(new Vector3(x0, nbA, z1), new Vector3(x0, myA, z1),
                         new Vector3(x1, nbB, z1), new Vector3(x1, myB, z1), c, verts, tris, colors);
            }
        }
    }

    /// <summary>Высоты двух углов ребра клетки с учётом среза: рёбра, примыкающие
    /// к срезанному углу, лежат на splitLow, остальные — на высоте клетки.</summary>
    private void GetEdge(int x, int z, int ca, int cb, out float hA, out float hB)
    {
        int sc = splitCorner[x, z];
        if (sc < 0)
        {
            hA = cornerH[x, z, ca];
            hB = cornerH[x, z, cb];
            return;
        }
        // ребро примыкает к срезанному углу, если один из его углов = sc
        bool touches = (ca == sc || cb == sc);
        float h = touches ? splitLow[x, z] : heights[x, z];
        hA = h; hB = h;
    }

    /// <summary>Клетка-уголок: диагональ через клетку, треугольник у срезанного угла
    /// опущен на высоту соседей, второй остаётся на высоте клетки, между ними диагональная стенка.</summary>
    private void AddSplitCell(int x, int z, float x0, float x1, float z0, float z1,
        List<Vector3> verts, List<int> tris, List<Color> colors,
        Dictionary<long, int> cache)
    {
        int k = splitCorner[x, z];
        float lo = splitLow[x, z];
        float hi = heights[x, z];

        Vector3 SW = new Vector3(x0, 0, z0), SE = new Vector3(x1, 0, z0);
        Vector3 NE = new Vector3(x1, 0, z1), NW = new Vector3(x0, 0, z1);

        Vector3 dA, dB;          // концы диагонали (порядок задаёт лицевую сторону стенки — к нижнему углу)
        Vector3[] loTri, hiTri;  // треугольники в правильном порядке обхода

        switch (k)
        {
            case 0: // срез SW, диагональ NW—SE
                loTri = new[] { SW, NW, SE }; hiTri = new[] { NW, NE, SE };
                dA = SE; dB = NW; break;
            case 1: // срез SE, диагональ SW—NE
                loTri = new[] { SW, NE, SE }; hiTri = new[] { SW, NW, NE };
                dA = NE; dB = SW; break;
            case 2: // срез NE, диагональ NW—SE
                loTri = new[] { NW, NE, SE }; hiTri = new[] { SW, NW, SE };
                dA = NW; dB = SE; break;
            default: // 3, срез NW, диагональ SW—NE
                loTri = new[] { SW, NW, NE }; hiTri = new[] { SW, NE, SE };
                dA = SW; dB = NE; break;
        }

        for (int i = 0; i < 3; i++)
            tris.Add(GetVert(loTri[i].x, lo, loTri[i].z, verts, colors, cache));
        for (int i = 0; i < 3; i++)
            tris.Add(GetVert(hiTri[i].x, hi, hiTri[i].z, verts, colors, cache));

        Color c = HeightColor(Mathf.Min(lo, hi));
        AddVQuad(new Vector3(dA.x, lo, dA.z), new Vector3(dA.x, hi, dA.z),
                 new Vector3(dB.x, lo, dB.z), new Vector3(dB.x, hi, dB.z), c, verts, tris, colors);
    }

    private void AddVQuad(Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr, Color c,
        List<Vector3> verts, List<int> tris, List<Color> colors)
    {
        int s = verts.Count;
        verts.Add(bl); verts.Add(br); verts.Add(tr); verts.Add(tl);
        for (int i = 0; i < 4; i++) colors.Add(c);
        tris.Add(s); tris.Add(s + 3); tris.Add(s + 2);
        tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);
    }

    private int GetVert(float wx, float y, float wz,
        List<Vector3> verts, List<Color> colors, Dictionary<long, int> cache)
    {
        int ix = Mathf.RoundToInt(wx * 1000f);
        int iy = Mathf.RoundToInt(y * 1000f);
        int iz = Mathf.RoundToInt(wz * 1000f);
        long key = ((long)(ix & 0x1FFFFF))
                 | ((long)(iy & 0x1FFFFF) << 21)
                 | ((long)(iz & 0x1FFFFF) << 42);

        if (cache.TryGetValue(key, out int idx)) return idx;

        idx = verts.Count;
        verts.Add(new Vector3(wx, y, wz));
        colors.Add(HeightColor(y));
        cache[key] = idx;
        return idx;
    }

    private float CellX(int x) => x * tileSize - mapWidth * tileSize / 2f;
    private float CellZ(int z) => z * tileSize - mapDepth * tileSize / 2f;

    private Color HeightColor(float h)
    {
        float t = Mathf.InverseLerp(0, heightSource.maxHeight, h);
        return Color.Lerp(lowColor, highColor, t);
    }

    void OnDestroy() => ClearTerrain();
}
