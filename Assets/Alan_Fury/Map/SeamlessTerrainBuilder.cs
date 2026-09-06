using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Строит единый бесшовный 3D-меш ландшафта на основе карты высот.
/// Создаёт отдельные GameObject'ы с нужными компонентами.
/// </summary>
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider))]
public class SeamlessTerrainBuilder : MonoBehaviour
{
    [Header("Источник высот")]
    public HeightMapGenerator heightSource;

    [Header("Настройки тайла")]
    public float tileSize = 4f;
    public float tileThickness = 0.2f;

    [Header("Фундамент")]
    public bool addFoundation = true;
    public float foundationDepth = 10f;

    [Header("Цвета")]
    public Color lowColor = new Color(0.3f, 0.3f, 0.3f);
    public Color highColor = new Color(0.9f, 0.9f, 0.9f);
    public Color foundationColor = new Color(0.2f, 0.15f, 0.1f);

    // Компоненты
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;

    // Данные высот
    private float[,] heights;
    private int width, depth;
    private float minHeight;
    private Vector3 mapOrigin;

    // Данные меша
    private List<Vector3> vertices;
    private List<int> triangles;
    private List<Color> colors;

    // Кэш для дедупликации вершин (используется вместо CombineVertices — один проход)
    private Dictionary<long, int> vertexCache;

    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
    }

    void OnDestroy()
    {
        ClearTerrain();
    }

    public void BuildTerrain()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("SeamlessTerrainBuilder: карта высот ещё не была сгенерирована!");
            return;
        }

        FetchHeightMap();
        BuildMesh();
        ApplyMesh();
        SetupCollider();
    }

    public void ClearTerrain()
    {
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            if (Application.isPlaying)
                Destroy(meshFilter.sharedMesh);
            else
                DestroyImmediate(meshFilter.sharedMesh);

            meshFilter.sharedMesh = null;
        }

        if (meshCollider != null)
            meshCollider.sharedMesh = null;
    }

    private void FetchHeightMap()
    {
        heights = heightSource.heightMap;
        width = heights.GetLength(0);
        depth = heights.GetLength(1);

        minHeight = float.MaxValue;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                if (heights[x, z] < minHeight)
                    minHeight = heights[x, z];

        mapOrigin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
    }

    private void BuildMesh()
    {
        vertices = new List<Vector3>();
        triangles = new List<int>();
        colors = new List<Color>();
        vertexCache = new Dictionary<long, int>();

        // Верхние грани
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                AddTopFace(x, z);

        // Стены между соседними ячейками разной высоты
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                AddWallsForCell(x, z);

        // Фундамент
        if (addFoundation)
        {
            AddFoundation();
            AddFoundationBottom();
        }
    }

    private void AddTopFace(int x, int z)
    {
        float h = heights[x, z];
        float cx = CellToWorldX(x);
        float cz = CellToWorldZ(z);
        float half = tileSize / 2f;

        int v0 = GetOrCreateVertex(cx - half, h, cz - half);
        int v1 = GetOrCreateVertex(cx + half, h, cz - half);
        int v2 = GetOrCreateVertex(cx + half, h, cz + half);
        int v3 = GetOrCreateVertex(cx - half, h, cz + half);

        AddQuad(v0, v1, v2, v3);
    }

    private void AddWallsForCell(int x, int z)
    {
        // Стена по оси X (между x и x+1)
        if (x + 1 < width && ShouldAddWall(x, z, x + 1, z))
            AddWallBetweenX(x, z, x + 1, z);

        // Стена по оси Z (между z и z+1)
        if (z + 1 < depth && ShouldAddWall(x, z, x, z + 1))
            AddWallBetweenZ(x, z, x, z + 1);
    }

    private bool ShouldAddWall(int x1, int z1, int x2, int z2)
    {
        return Mathf.Abs(heights[x1, z1] - heights[x2, z2]) > 0.001f;
    }

    /// <summary>
    /// Стена между двумя ячейками по оси X.
    /// Направление нормали определяется тем, какая ячейка выше.
    /// </summary>
    private void AddWallBetweenX(int x1, int z1, int x2, int z2)
    {
        float h1 = heights[x1, z1];
        float h2 = heights[x2, z2];
        float lower = Mathf.Min(h1, h2);
        float upper = Mathf.Max(h1, h2);
        float half = tileSize / 2f;

        float borderX = (CellToWorldX(x1) + CellToWorldX(x2)) / 2f;
        float cz = CellToWorldZ(z1);

        Color wallColor = GetHeightColor(lower);

        // FIX: порядок вершин зависит от того, какая сторона выше,
        // чтобы нормаль всегда смотрела в сторону более низкой ячейки.
        if (h1 > h2)
        {
            // Нормаль смотрит в +X (от x1 к x2)
            AddVerticalQuad(
                new Vector3(borderX, lower, cz - half),
                new Vector3(borderX, lower, cz + half),
                new Vector3(borderX, upper, cz - half),
                new Vector3(borderX, upper, cz + half),
                wallColor
            );
        }
        else
        {
            // Нормаль смотрит в -X (от x2 к x1)
            AddVerticalQuad(
                new Vector3(borderX, lower, cz + half),
                new Vector3(borderX, lower, cz - half),
                new Vector3(borderX, upper, cz + half),
                new Vector3(borderX, upper, cz - half),
                wallColor
            );
        }
    }

    /// <summary>
    /// Стена между двумя ячейками по оси Z.
    /// Направление нормали определяется тем, какая ячейка выше.
    /// </summary>
    private void AddWallBetweenZ(int x1, int z1, int x2, int z2)
    {
        float h1 = heights[x1, z1];
        float h2 = heights[x2, z2];
        float lower = Mathf.Min(h1, h2);
        float upper = Mathf.Max(h1, h2);
        float half = tileSize / 2f;

        float borderZ = (CellToWorldZ(z1) + CellToWorldZ(z2)) / 2f;
        float cx = CellToWorldX(x1);

        Color wallColor = GetHeightColor(lower);

        if (h1 > h2)
        {
            // Нормаль смотрит в +Z
            AddVerticalQuad(
                new Vector3(cx + half, lower, borderZ),
                new Vector3(cx - half, lower, borderZ),
                new Vector3(cx + half, upper, borderZ),
                new Vector3(cx - half, upper, borderZ),
                wallColor
            );
        }
        else
        {
            // Нормаль смотрит в -Z
            AddVerticalQuad(
                new Vector3(cx - half, lower, borderZ),
                new Vector3(cx + half, lower, borderZ),
                new Vector3(cx - half, upper, borderZ),
                new Vector3(cx + half, upper, borderZ),
                wallColor
            );
        }
    }

    private void AddFoundation()
    {
        float bottom = minHeight - foundationDepth;
        float half = tileSize / 2f;

        // Передняя грань (Z-)
        float czFront = CellToWorldZ(0) - half;
        for (int x = 0; x < width; x++)
        {
            float cx = CellToWorldX(x);
            float h = heights[x, 0];
            AddVerticalQuad(
                new Vector3(cx + half, bottom, czFront),
                new Vector3(cx - half, bottom, czFront),
                new Vector3(cx + half, h, czFront),
                new Vector3(cx - half, h, czFront),
                foundationColor
            );
        }

        // Задняя грань (Z+)
        float czBack = CellToWorldZ(depth - 1) + half;
        for (int x = 0; x < width; x++)
        {
            float cx = CellToWorldX(x);
            float h = heights[x, depth - 1];
            AddVerticalQuad(
                new Vector3(cx - half, bottom, czBack),
                new Vector3(cx + half, bottom, czBack),
                new Vector3(cx - half, h, czBack),
                new Vector3(cx + half, h, czBack),
                foundationColor
            );
        }

        // Левая грань (X-)
        float cxLeft = CellToWorldX(0) - half;
        for (int z = 0; z < depth; z++)
        {
            float cz = CellToWorldZ(z);
            float h = heights[0, z];
            AddVerticalQuad(
                new Vector3(cxLeft, bottom, cz - half),
                new Vector3(cxLeft, bottom, cz + half),
                new Vector3(cxLeft, h, cz - half),
                new Vector3(cxLeft, h, cz + half),
                foundationColor
            );
        }

        // Правая грань (X+)
        float cxRight = CellToWorldX(width - 1) + half;
        for (int z = 0; z < depth; z++)
        {
            float cz = CellToWorldZ(z);
            float h = heights[width - 1, z];
            AddVerticalQuad(
                new Vector3(cxRight, bottom, cz + half),
                new Vector3(cxRight, bottom, cz - half),
                new Vector3(cxRight, h, cz + half),
                new Vector3(cxRight, h, cz - half),
                foundationColor
            );
        }
    }

    private void AddFoundationBottom()
    {
        float bottom = minHeight - foundationDepth;
        float halfWidth = width * tileSize / 2f;
        float halfDepth = depth * tileSize / 2f;

        int start = vertices.Count;
        vertices.Add(new Vector3(-halfWidth, bottom, -halfDepth));
        vertices.Add(new Vector3(halfWidth, bottom, -halfDepth));
        vertices.Add(new Vector3(halfWidth, bottom, halfDepth));
        vertices.Add(new Vector3(-halfWidth, bottom, halfDepth));
        for (int i = 0; i < 4; i++) colors.Add(foundationColor);

        // Снизу смотрим вниз — обратный winding
        triangles.Add(start + 0); triangles.Add(start + 2); triangles.Add(start + 1);
        triangles.Add(start + 0); triangles.Add(start + 3); triangles.Add(start + 2);
    }

    /// <summary>
    /// FIX: ключ — long-упаковка трёх int, безопасна для больших карт.
    /// Заменяет Vector3Int (переполнение при > ~2000 юнитов) и
    /// устраняет необходимость в отдельном проходе CombineVertices.
    /// </summary>
    private int GetOrCreateVertex(float worldX, float y, float worldZ)
    {
        // Округляем с точностью 0.001 юнита
        int ix = Mathf.RoundToInt(worldX * 1000f);
        int iy = Mathf.RoundToInt(y * 1000f);
        int iz = Mathf.RoundToInt(worldZ * 1000f);

        // Упаковываем в long: 21 бит на компонент (±1048576 юнитов — достаточно для любого ландшафта)
        long key = ((long)(ix & 0x1FFFFF))
                 | ((long)(iy & 0x1FFFFF) << 21)
                 | ((long)(iz & 0x1FFFFF) << 42);

        if (vertexCache.TryGetValue(key, out int index))
            return index;

        index = vertices.Count;
        vertices.Add(new Vector3(worldX, y, worldZ));
        colors.Add(GetHeightColor(y));
        vertexCache[key] = index;

        return index;
    }

    /// <summary>
    /// Добавляет вертикальный четырёхугольник.
    /// bl/br — нижние углы, tl/tr — верхние. Нормаль направлена от bl к br (по правилу правой руки).
    /// </summary>
    private void AddVerticalQuad(Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr, Color c)
    {
        int start = vertices.Count;
        vertices.AddRange(new[] { bl, br, tr, tl });
        for (int i = 0; i < 4; i++) colors.Add(c);

        triangles.Add(start + 0); triangles.Add(start + 3); triangles.Add(start + 2);
        triangles.Add(start + 0); triangles.Add(start + 2); triangles.Add(start + 1);
    }

    /// <summary>
    /// Добавляет горизонтальный четырёхугольник (верхняя грань тайла).
    /// </summary>
    private void AddQuad(int a, int b, int c, int d)
    {
        triangles.Add(a); triangles.Add(d); triangles.Add(c);
        triangles.Add(a); triangles.Add(c); triangles.Add(b);
    }

    private float CellToWorldX(int x) => x * tileSize - width * tileSize / 2f;
    private float CellToWorldZ(int z) => z * tileSize - depth * tileSize / 2f;

    private Color GetHeightColor(float h)
    {
        float t = Mathf.InverseLerp(0, heightSource.maxHeight, h);
        return Color.Lerp(lowColor, highColor, t);
    }

    private void ApplyMesh()
    {
        ClearTerrain();

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.colors = colors.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.Optimize();

        meshFilter.sharedMesh = mesh;

        if (meshRenderer.sharedMaterial == null)
            meshRenderer.sharedMaterial = new Material(Shader.Find("Standard"));

        if (meshCollider != null)
        {
            meshCollider.enabled = false;
            meshCollider.enabled = true;
        }
    }

    private void SetupCollider()
    {
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = meshFilter.sharedMesh;
        }
    }
}
