// HeightMapGenerator v3.0 — абстрактная база
// Общее для всех генераторов рельефа: размеры, сид, массив высот, события, доступ к высоте,
// хелперы пост-обработки (сглаживание, террасы, чистка рванины и мелких пятен).
// Конкретная генерация — в наследниках: SwampHeightMapGenerator, ForestHeightMapGenerator.
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Базовый генератор карты высот. Единственный источник высот для остальных скриптов —
/// все они ссылаются на этот тип и работают с любым наследником.
/// </summary>
public abstract class HeightMapGenerator : MonoBehaviour
{
    public const string Version = "3.0";

    [Header("Размеры карты")]
    public int width = 60;
    public int depth = 60;

    [Header("Масштаб мира (источник tileSize)")]
    public ChunkedTerrainBuilder chunkedBuilder;

    [Header("Сид")]
    public bool randomSeed = true;
    public int seed;

    [Header("Верхняя граница высот")]
    [Tooltip("Верх диапазона рельефа. Читается TerrainZoneSystem (пороги зон) и раскраской меша.")]
    public float maxHeight = 10f;

    [Header("Террасы и очистка")]
    public float terraceStep = 0.5f;          // шаг квантования высот = высота одной ступени
    public int maxSmoothStep = 1;             // перепад с соседями в СТУПЕНЯХ <= этого — прилипает; больше = обрыв
    public int minClusterCells = 5;           // пятно одной высоты меньше этого числа клеток — прилипает к соседу

    // Публичные данные — доступ для всех скриптов
    public float[,] heightMap { get; protected set; }
    public bool isGenerated { get; protected set; }

    // ============ СОБЫТИЯ ============
    public System.Action onHeightMapReady;
    public System.Action<float[,]> onHeightMapGenerated;

    /// <summary>Запускает генерацию карты высот.</summary>
    public void Generate()
    {
        if (randomSeed)
            seed = UnityEngine.Random.Range(0, 100000);

        heightMap = new float[width, depth];

        BuildHeights(ResolveTileSize());

        isGenerated = true;
        onHeightMapReady?.Invoke();
        onHeightMapGenerated?.Invoke(heightMap);
    }

    /// <summary>
    /// Заполняет heightMap. Реализация наследника: рельеф + нужная ему пост-обработка.
    /// Массив уже выделен, сид уже определён.
    /// </summary>
    protected abstract void BuildHeights(float tileSize);

    /// <summary>Берёт tileSize из билдера, чтобы всё считалось в мировых единицах.</summary>
    protected float ResolveTileSize()
    {
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 1f;
    }

    /// <summary>Левый нижний угол карты в мировых координатах (карта центрирована на нуле).</summary>
    protected Vector3 MapOrigin(float tileSize)
        => new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);

    // ==================== ПОСТ-ОБРАБОТКА ====================

    /// <summary>
    /// Box-blur 3×3 за passes проходов по готовой карте высот (до квантования).
    /// Давит мелкую рябь и смягчает склоны → площадки одной высоты крупнее.
    /// </summary>
    protected void SmoothHeightMap(int passes)
    {
        if (passes <= 0 || heightMap == null) return;

        float[,] tmp = new float[width, depth];
        for (int p = 0; p < passes; p++)
        {
            for (int x = 0; x < width; x++)
                for (int z = 0; z < depth; z++)
                {
                    float sum = 0f; int cnt = 0;
                    for (int ox = -1; ox <= 1; ox++)
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            int nx = x + ox, nz = z + oz;
                            if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;
                            sum += heightMap[nx, nz]; cnt++;
                        }
                    tmp[x, z] = sum / cnt;
                }

            for (int x = 0; x < width; x++)
                for (int z = 0; z < depth; z++)
                    heightMap[x, z] = tmp[x, z];
        }
    }

    /// <summary>Квантует высоты шагом terraceStep → плоские уступы, обрывы рисует меш.</summary>
    protected void ApplyTerraces()
    {
        if (terraceStep <= 0f) return;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = (Mathf.Floor(heightMap[x, z] / terraceStep) + 0.1f) * terraceStep;
    }

    /// <summary>
    /// Прибирает мелкую рванину после квантования: клетка, отличающаяся от самого частого
    /// соседнего уровня не больше чем на maxSmoothStep ступеней, прилипает к нему.
    /// Крупные перепады (обрыв/берег) сохраняются. 4-связность, один проход в копию.
    /// </summary>
    protected void FlattenMinorSteps()
    {
        if (terraceStep <= 0f || maxSmoothStep <= 0 || heightMap == null) return;

        float tol = maxSmoothStep * terraceStep + 1e-4f;
        var src = (float[,])heightMap.Clone();
        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float cur = src[x, z];

                var freq = new Dictionary<float, int>();
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], nz = z + dz[k];
                    if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;
                    float nv = src[nx, nz];
                    if (Mathf.Approximately(nv, cur)) continue;
                    if (Mathf.Abs(nv - cur) > tol) continue; // крупный перепад = обрыв, пропускаем
                    freq.TryGetValue(nv, out int f);
                    freq[nv] = f + 1;
                }
                if (freq.Count == 0) continue;

                float best = cur; int bestF = 0;
                foreach (var kv in freq)
                    if (kv.Value > bestF) { bestF = kv.Value; best = kv.Key; }

                if (bestF >= 2) heightMap[x, z] = best; // прилипаем, только если сосед преобладает
            }
    }

    /// <summary>
    /// Прилепляет мелкие пятна одной высоты (меньше minClusterCells клеток) к соседнему уровню.
    /// 4-связность, применяется после квантования.
    /// </summary>
    protected void CleanupSmallClusters()
    {
        if (minClusterCells <= 1 || terraceStep <= 0f) return;

        var visited = new bool[width, depth];
        var region = new List<Vector2Int>();
        var stack = new Stack<Vector2Int>();
        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int sx = 0; sx < width; sx++)
            for (int sz = 0; sz < depth; sz++)
            {
                if (visited[sx, sz]) continue;

                float val = heightMap[sx, sz];
                region.Clear();
                stack.Clear();
                stack.Push(new Vector2Int(sx, sz));
                visited[sx, sz] = true;

                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    region.Add(c);
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = c.x + dx[k], nz = c.y + dz[k];
                        if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;
                        if (visited[nx, nz]) continue;
                        if (!Mathf.Approximately(heightMap[nx, nz], val)) continue;
                        visited[nx, nz] = true;
                        stack.Push(new Vector2Int(nx, nz));
                    }
                }

                if (region.Count >= minClusterCells) continue;

                // маленькое пятно → самый частый уровень среди соседей другого уровня
                var freq = new Dictionary<float, int>();
                foreach (var c in region)
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = c.x + dx[k], nz = c.y + dz[k];
                        if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;
                        float nv = heightMap[nx, nz];
                        if (Mathf.Approximately(nv, val)) continue;
                        freq.TryGetValue(nv, out int f);
                        freq[nv] = f + 1;
                    }
                if (freq.Count == 0) continue;

                float best = val; int bestF = -1;
                foreach (var kv in freq)
                    if (kv.Value > bestF) { bestF = kv.Value; best = kv.Key; }

                foreach (var c in region) heightMap[c.x, c.y] = best;
            }
    }

    // ==================== ДОСТУП К ВЫСОТАМ ====================

    /// <summary>Очищает карту высот.</summary>
    public void Clear()
    {
        heightMap = null;
        isGenerated = false;
    }

    /// <summary>Безопасно возвращает высоту по индексам ячейки.</summary>
    public float GetHeight(int x, int z)
    {
        if (heightMap == null) return 0f;
        if (x < 0 || x >= width || z < 0 || z >= depth) return 0f;
        return heightMap[x, z];
    }

    /// <summary>Возвращает высоту с билинейной интерполяцией.</summary>
    public float GetHeightBilinear(float worldX, float worldZ, float tileSize, Vector3 mapOrigin)
    {
        if (heightMap == null) return 0f;

        float localX = worldX - mapOrigin.x;
        float localZ = worldZ - mapOrigin.z;

        float cellXFloat = localX / tileSize;
        float cellZFloat = localZ / tileSize;

        int cellX = Mathf.FloorToInt(cellXFloat);
        int cellZ = Mathf.FloorToInt(cellZFloat);

        float fx = cellXFloat - cellX;
        float fz = cellZFloat - cellZ;

        float h00 = GetHeight(cellX, cellZ);
        float h10 = GetHeight(Mathf.Min(cellX + 1, width - 1), cellZ);
        float h01 = GetHeight(cellX, Mathf.Min(cellZ + 1, depth - 1));
        float h11 = GetHeight(Mathf.Min(cellX + 1, width - 1), Mathf.Min(cellZ + 1, depth - 1));

        float h0 = Mathf.Lerp(h00, h10, fx);
        float h1 = Mathf.Lerp(h01, h11, fx);

        return Mathf.Lerp(h0, h1, fz);
    }

    /// <summary>Возвращает высоту поверхности в произвольной мировой точке.</summary>
    public float GetHeightAtWorldPos(Vector3 worldPos, float tileSize, Vector3 mapOrigin)
    {
        return GetHeightBilinear(worldPos.x, worldPos.z, tileSize, mapOrigin);
    }

#if UNITY_EDITOR
    /// <summary>Нижняя точка градиента гизмо. Наследник переопределяет, если рельеф уходит ниже нуля.</summary>
    protected virtual float GizmoMinHeight => 0f;

    private void OnDrawGizmos()
    {
        if (!isGenerated || heightMap == null) return;

        float tileSize = chunkedBuilder != null ? chunkedBuilder.tileSize : 1f;
        Vector3 mapOrigin = MapOrigin(tileSize);

        for (int x = 0; x < width; x += Mathf.Max(1, width / 20))
        {
            for (int z = 0; z < depth; z += Mathf.Max(1, depth / 20))
            {
                float h = heightMap[x, z];
                Vector3 pos = mapOrigin + new Vector3(x * tileSize, h, z * tileSize);
                float t = Mathf.InverseLerp(GizmoMinHeight, maxHeight, h);
                Gizmos.color = Color.Lerp(Color.blue, Color.red, t);
                Gizmos.DrawWireCube(pos, Vector3.one * 0.2f);
            }
        }
    }
#endif
}
