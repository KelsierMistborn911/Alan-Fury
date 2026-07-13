using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Строит ландшафт разбитым на чанки вместо одного монолитного меша.
/// Каждый чанк — отдельный GameObject с MeshFilter/MeshRenderer/MeshCollider.
/// Это позволяет Unity culling'у отбрасывать невидимые чанки автоматически.
/// 
/// Использование: замените SeamlessTerrainBuilder на этот компонент,
/// или используйте оба — этот для рендера, старый для коллайдера.
/// </summary>
public class ChunkedTerrainBuilder : MonoBehaviour
{
    [Header("Источник высот")]
    public HeightMapGenerator heightSource;

    [Header("Размер тайла")]
    public float tileSize = 4f;

    [Header("Размер чанка (в тайлах)")]
    [Tooltip("10 = каждый чанк покрывает 10×10 тайлов. Для карты 40×40 получится 16 чанков.")]
    public int chunkSize = 10;

    [Header("Фундамент")]
    public bool addFoundation = true;
    public float foundationDepth = 10f;

    [Header("Цвета")]
    public Color lowColor = new Color(0.3f, 0.3f, 0.3f);
    public Color highColor = new Color(0.9f, 0.9f, 0.9f);
    public Color foundationColor = new Color(0.2f, 0.15f, 0.1f);

    [Header("Материал (опционально)")]
    [Tooltip("Если не задан — используется Standard с вершинными цветами")]
    public Material terrainMaterial;

    // Ссылки на созданные чанки
    private List<GameObject> chunks = new List<GameObject>();
    private float[,] heights;
    private int mapWidth, mapDepth;
    private float minHeight;
    private Vector3 mapOrigin;

    public float TileSize => tileSize; // для совместимости с ObjectPlacer и ZoneSystem

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

        minHeight = float.MaxValue;
        for (int x = 0; x < mapWidth; x++)
            for (int z = 0; z < mapDepth; z++)
                if (heights[x, z] < minHeight) minHeight = heights[x, z];

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

    private void BuildChunk(int chunkX, int chunkZ)
    {
        // Границы тайлов этого чанка
        int startX = chunkX * chunkSize;
        int startZ = chunkZ * chunkSize;
        int endX = Mathf.Min(startX + chunkSize, mapWidth);
        int endZ = Mathf.Min(startZ + chunkSize, mapDepth);

        var verts = new List<Vector3>();
        var tris = new List<int>();
        var colors = new List<Color>();
        var cache = new Dictionary<long, int>();

        // Верхние грани
        for (int x = startX; x < endX; x++)
            for (int z = startZ; z < endZ; z++)
                AddTopFace(x, z, verts, tris, colors, cache);

        // Стены внутри чанка и на границах
        for (int x = startX; x < endX; x++)
        {
            for (int z = startZ; z < endZ; z++)
            {
                if (x + 1 < mapWidth && Mathf.Abs(heights[x, z] - heights[x + 1, z]) > 0.001f)
                    AddWallX(x, z, verts, tris, colors);

                if (z + 1 < mapDepth && Mathf.Abs(heights[x, z] - heights[x, z + 1]) > 0.001f)
                    AddWallZ(x, z, verts, tris, colors);
            }
        }

        // Фундамент только на внешних границах карты
        if (addFoundation)
            AddChunkFoundation(startX, startZ, endX, endZ, verts, tris, colors);

        if (verts.Count == 0) return;

        // Создаём GameObject чанка
        var chunkGO = new GameObject($"Chunk_{chunkX}_{chunkZ}");
        chunkGO.transform.SetParent(transform);
        chunkGO.isStatic = true; // важно для batching

        // Кладём чанк на слой "Terrain" (для рейкастов/паркура/граунд-чеков)
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

    // =================== Геометрия ===================

    private void AddTopFace(int x, int z,
        List<Vector3> verts, List<int> tris, List<Color> colors,
        Dictionary<long, int> cache)
    {
        float h = heights[x, z];
        float cx = CellX(x);
        float cz = CellZ(z);
        float half = tileSize / 2f;

        int v0 = GetVert(cx - half, h, cz - half, verts, colors, cache);
        int v1 = GetVert(cx + half, h, cz - half, verts, colors, cache);
        int v2 = GetVert(cx + half, h, cz + half, verts, colors, cache);
        int v3 = GetVert(cx - half, h, cz + half, verts, colors, cache);

        tris.Add(v0); tris.Add(v3); tris.Add(v2);
        tris.Add(v0); tris.Add(v2); tris.Add(v1);
    }

    private void AddWallX(int x, int z, List<Vector3> verts, List<int> tris, List<Color> colors)
    {
        float h1 = heights[x, z];
        float h2 = heights[x + 1, z];
        float lower = Mathf.Min(h1, h2);
        float upper = Mathf.Max(h1, h2);
        float half = tileSize / 2f;
        float borderX = (CellX(x) + CellX(x + 1)) / 2f;
        float cz = CellZ(z);
        Color c = HeightColor(lower);

        if (h1 > h2)
            AddVQuad(new Vector3(borderX, lower, cz - half), new Vector3(borderX, lower, cz + half),
                     new Vector3(borderX, upper, cz - half), new Vector3(borderX, upper, cz + half), c, verts, tris, colors);
        else
            AddVQuad(new Vector3(borderX, lower, cz + half), new Vector3(borderX, lower, cz - half),
                     new Vector3(borderX, upper, cz + half), new Vector3(borderX, upper, cz - half), c, verts, tris, colors);
    }

    private void AddWallZ(int x, int z, List<Vector3> verts, List<int> tris, List<Color> colors)
    {
        float h1 = heights[x, z];
        float h2 = heights[x, z + 1];
        float lower = Mathf.Min(h1, h2);
        float upper = Mathf.Max(h1, h2);
        float half = tileSize / 2f;
        float borderZ = (CellZ(z) + CellZ(z + 1)) / 2f;
        float cx = CellX(x);
        Color c = HeightColor(lower);

        if (h1 > h2)
            AddVQuad(new Vector3(cx + half, lower, borderZ), new Vector3(cx - half, lower, borderZ),
                     new Vector3(cx + half, upper, borderZ), new Vector3(cx - half, upper, borderZ), c, verts, tris, colors);
        else
            AddVQuad(new Vector3(cx - half, lower, borderZ), new Vector3(cx + half, lower, borderZ),
                     new Vector3(cx - half, upper, borderZ), new Vector3(cx + half, upper, borderZ), c, verts, tris, colors);
    }

    private void AddChunkFoundation(int startX, int startZ, int endX, int endZ,
        List<Vector3> verts, List<int> tris, List<Color> colors)
    {
        float bottom = minHeight - foundationDepth;
        float half = tileSize / 2f;

        // Передняя грань карты (z == 0)
        if (startZ == 0)
        {
            float czFront = CellZ(0) - half;
            for (int x = startX; x < endX; x++)
            {
                float cx = CellX(x); float h = heights[x, 0];
                AddVQuad(new Vector3(cx + half, bottom, czFront), new Vector3(cx - half, bottom, czFront),
                         new Vector3(cx + half, h, czFront), new Vector3(cx - half, h, czFront), foundationColor, verts, tris, colors);
            }
        }

        // Задняя грань карты (z == mapDepth-1)
        if (endZ == mapDepth)
        {
            float czBack = CellZ(mapDepth - 1) + half;
            for (int x = startX; x < endX; x++)
            {
                float cx = CellX(x); float h = heights[x, mapDepth - 1];
                AddVQuad(new Vector3(cx - half, bottom, czBack), new Vector3(cx + half, bottom, czBack),
                         new Vector3(cx - half, h, czBack), new Vector3(cx + half, h, czBack), foundationColor, verts, tris, colors);
            }
        }

        // Левая грань карты (x == 0)
        if (startX == 0)
        {
            float cxLeft = CellX(0) - half;
            for (int z = startZ; z < endZ; z++)
            {
                float cz = CellZ(z); float h = heights[0, z];
                AddVQuad(new Vector3(cxLeft, bottom, cz - half), new Vector3(cxLeft, bottom, cz + half),
                         new Vector3(cxLeft, h, cz - half), new Vector3(cxLeft, h, cz + half), foundationColor, verts, tris, colors);
            }
        }

        // Правая грань карты (x == mapWidth-1)
        if (endX == mapWidth)
        {
            float cxRight = CellX(mapWidth - 1) + half;
            for (int z = startZ; z < endZ; z++)
            {
                float cz = CellZ(z); float h = heights[mapWidth - 1, z];
                AddVQuad(new Vector3(cxRight, bottom, cz + half), new Vector3(cxRight, bottom, cz - half),
                         new Vector3(cxRight, h, cz + half), new Vector3(cxRight, h, cz - half), foundationColor, verts, tris, colors);
            }
        }

        // Дно фундамента (только для крайних чанков всех 4 сторон)
        if (startX == 0 && startZ == 0 && endX == mapWidth && endZ == mapDepth)
        {
            float hw = mapWidth * tileSize / 2f;
            float hd = mapDepth * tileSize / 2f;
            int s = verts.Count;
            verts.Add(new Vector3(-hw, bottom, -hd)); colors.Add(foundationColor);
            verts.Add(new Vector3(hw, bottom, -hd)); colors.Add(foundationColor);
            verts.Add(new Vector3(hw, bottom, hd)); colors.Add(foundationColor);
            verts.Add(new Vector3(-hw, bottom, hd)); colors.Add(foundationColor);
            tris.Add(s); tris.Add(s + 2); tris.Add(s + 1);
            tris.Add(s); tris.Add(s + 3); tris.Add(s + 2);
        }
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