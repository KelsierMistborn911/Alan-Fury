using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using Unity.Mathematics;

/// <summary>
/// Генерирует карту высот через шум Перлина.
/// Единственный источник данных о высотах для всех остальных скриптов.
/// </summary>
public class HeightMapGenerator : MonoBehaviour
{
    [Header("Размеры карты")]
    public int width = 20;
    public int depth = 20;

    [Header("Настройки шума")]
    [Tooltip("Частота шума в мировых единицах (на метр). Размер форм не зависит от размера карты. Ниже = крупнее холмы.")]
    public float noiseScale = 0.02f;
    public float maxHeight = 0.5f;
    [Tooltip("Доля «хребтистости»: 0 — округлые холмы (fBm), 1 — острые гребни (ridged). ~0.4 даёт хребты без резкости.")]
    [Range(0f, 1f)] public float ridgeStrength = 0.4f;

    [Header("Масштаб мира (источник tileSize)")]
    public ChunkedTerrainBuilder chunkedBuilder;
    public SeamlessTerrainBuilder seamlessBuilder;

    [Header("Сглаживание")]
    [Tooltip("Проходов box-сглаживания 3×3 по готовой карте. Убирает дырки, одиночные лужи, резкие переходы; делает берега пологими.")]
    public int smoothPasses = 2;

    [Header("Карст / болото")]
    [Range(0f, 1f)]
    [Tooltip("Прижимает низины к плоскому дну и поднимает контраст островов. 0 = выкл (плавный fBm), 1 = острова из плоской мелкой воды.")]
    public float shallowFlatten = 0.5f;

    [Tooltip("Сколько уровней-террас. 0 = плавные острова, больше = резкие плоские уступы (карст). На блочном меше даёт чёткие обрывы.")]
    public int terraceLevels = 6;

    [Tooltip("Сколько глубоких омутов пробить в низинах. Вода прячет их сверху — дна не видно.")]
    public int omutCount = 4;

    [Range(0f, 1f)]
    [Tooltip("Глубина омута как доля maxHeight. Радиус воронки фиксированный (~3 клетки).")]
    public float omutDepth = 0.6f;

    [Range(0f, 1f)]
    [Tooltip("Доля карты под водой. Линия воды садится в зазор между террасами и якорится на y=0: земля выше 0, дно ниже. Заменяет waterPercent.")]
    public float seaLevel = 0.4f;

    [Header("Сид")]
    public bool randomSeed = true;
    public int seed;

    [Header("Производительность")]
    public bool useJobs = true;
    // Минимальный размер карты для использования Job System
    public int jobsMinCells = 10000;

    // Публичные данные — доступ для всех скриптов
    public float[,] heightMap { get; private set; }
    public bool isGenerated { get; private set; }

    // Радиус воронки омута в клетках (не выношу в инспектор — минимум параметров).
    private const int OmutRadius = 3;

    // ============ СОБЫТИЯ ============
    public System.Action onHeightMapReady;
    public System.Action<float[,]> onHeightMapGenerated;

    /// <summary>
    /// Запускает генерацию карты высот.
    /// </summary>
    public void Generate()
    {
        if (randomSeed)
            seed = UnityEngine.Random.Range(0, 100000);

        heightMap = new float[width, depth];

        float ts = ResolveTileSize();

        if (useJobs && width * depth > jobsMinCells)
            GenerateWithJobs(ts);
        else
            GenerateSimple(ts);

        // --- Пост-обработка (порядок важен) ---
        NormalizeInPlace01();   // нормируем в [0,1], чтобы шейпинг считался предсказуемо
        ApplyShallowFlatten();  // прижимаем низины к плоскому дну (мелкая вода + острова)
        SmoothHeightMap();      // сглаживает базу/берега; террасы и омуты идут ПОСЛЕ, поэтому остаются резкими
        ApplyTerracing();       // режем на уровни-террасы → резкие края/обрывы
        AnchorToSeaLevel();     // линию воды — в зазор между террасами и на y=0 (земля >0, дно <0), масштаб на maxHeight
        CarveOmuts();           // пробиваем глубокие омуты последними — их не трогают ни сглаживание, ни террасы

        isGenerated = true;

        onHeightMapReady?.Invoke();
        onHeightMapGenerated?.Invoke(heightMap);
    }

    private void GenerateSimple(float tileSize)
    {
        float seedOffset = seed * 0.1f;

        const int octaves = 4;
        const float persistence = 0.4f;
        const float lacunarity = 2f;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                float bx = x * tileSize * noiseScale + seedOffset;
                float bz = z * tileSize * noiseScale + seedOffset;

                // Фрактальный шум (fBm) + опциональная «хребтистость» (ridged).
                // Крупные октавы дают связные бассейны (низины) и водоразделы.
                float amp = 1f, freq = 1f, sum = 0f, ampMax = 0f;
                for (int o = 0; o < octaves; o++)
                {
                    float n = Mathf.PerlinNoise(bx * freq, bz * freq);   // [0,1]
                    float r = 1f - Mathf.Abs(2f * n - 1f);               // гребень: пик у n=0.5
                    float v = Mathf.Lerp(n, r, ridgeStrength);
                    sum += v * amp;
                    ampMax += amp;
                    amp *= persistence;
                    freq *= lacunarity;
                }

                heightMap[x, z] = (sum / ampMax) * maxHeight;
            }
        }
    }

    private void GenerateWithJobs(float tileSize)
    {
        int totalCells = width * depth;
        NativeArray<float> heightsArray = new NativeArray<float>(totalCells, Allocator.TempJob);

        var job = new HeightMapJob
        {
            width = width,
            noiseScale = noiseScale,
            tileSize = tileSize,
            maxHeight = maxHeight,
            ridgeStrength = ridgeStrength,
            seed = seed,
            heights = heightsArray
        };

        JobHandle handle = job.Schedule(totalCells, 64);
        handle.Complete();

        for (int i = 0; i < totalCells; i++)
        {
            int x = i % width;
            int z = i / width;
            heightMap[x, z] = heightsArray[i];
        }

        heightsArray.Dispose();
    }

    /// <summary>
    /// Burst-совместимая реализация шума Перлина через Unity.Mathematics.
    /// noise.cnoise возвращает значения в [-1, 1], нормализуем в [0, 1].
    /// </summary>
    [BurstCompile]
    private struct HeightMapJob : IJobParallelFor
    {
        public int width;
        public float noiseScale;
        public float tileSize;
        public float maxHeight;
        public float ridgeStrength;
        public int seed;
        public NativeArray<float> heights;

        public void Execute(int index)
        {
            int x = index % width;
            int z = index / width;

            float seedOffset = seed * 0.1f;
            float bx = x * tileSize * noiseScale + seedOffset;
            float bz = z * tileSize * noiseScale + seedOffset;

            // Фрактальный шум (fBm) + опциональная «хребтистость» (ridged).
            const int octaves = 4;
            const float persistence = 0.4f;
            const float lacunarity = 2f;

            float amp = 1f, freq = 1f, sum = 0f, ampMax = 0f;
            for (int o = 0; o < octaves; o++)
            {
                float2 coord = new float2(bx * freq, bz * freq);
                // cnoise: [-1, 1] → нормализуем в [0, 1]
                float n = Unity.Mathematics.noise.cnoise(coord) * 0.5f + 0.5f;
                float r = 1f - math.abs(2f * n - 1f);               // гребень: пик у n=0.5
                float v = math.lerp(n, r, ridgeStrength);
                sum += v * amp;
                ampMax += amp;
                amp *= persistence;
                freq *= lacunarity;
            }

            heights[index] = (sum / ampMax) * maxHeight;
        }
    }

    /// <summary>
    /// Сглаживает готовую карту высот box-фильтром 3×3 за smoothPasses проходов.
    /// Один общий пост-проход для обоих путей генерации (Simple и Job):
    /// убирает одиночные ямы/лужи, рваные берега и резкие переходы, делает берега пологими.
    /// </summary>
    private void SmoothHeightMap()
    {
        if (smoothPasses <= 0 || heightMap == null) return;

        float[,] tmp = new float[width, depth];

        for (int p = 0; p < smoothPasses; p++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    float sum = 0f;
                    int cnt = 0;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            int nx = x + ox;
                            int nz = z + oz;
                            if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;
                            sum += heightMap[nx, nz];
                            cnt++;
                        }
                    }
                    tmp[x, z] = sum / cnt;
                }
            }

            // переносим результат прохода обратно
            for (int x = 0; x < width; x++)
                for (int z = 0; z < depth; z++)
                    heightMap[x, z] = tmp[x, z];
        }
    }

    /// <summary>
    /// Нормирует карту в [0,1] перед шейпингом, чтобы степенные/террасные преобразования
    /// работали предсказуемо независимо от maxHeight. Линейная операция: при выключенном
    /// шейпинге итог совпадает со старым поведением (финальный RenormalizeHeights всё растянет).
    /// </summary>
    private void NormalizeInPlace01()
    {
        if (heightMap == null) return;

        float min = float.MaxValue, max = float.MinValue;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float h = heightMap[x, z];
                if (h < min) min = h;
                if (h > max) max = h;
            }

        float range = max - min;
        if (range < 1e-6f) return;

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = (heightMap[x, z] - min) / range;
    }

    /// <summary>
    /// Прижимает низины к плоскому дну степенной кривой h^p (p растёт с shallowFlatten).
    /// Низкие значения сбиваются к 0 (мелкое плоское дно — будущая мелкая вода),
    /// высокие расходятся (острова получают рельеф). Работает в нормированном [0,1].
    /// </summary>
    private void ApplyShallowFlatten()
    {
        if (shallowFlatten <= 0f || heightMap == null) return;

        float p = Mathf.Lerp(1f, 3f, Mathf.Clamp01(shallowFlatten));
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = Mathf.Pow(Mathf.Clamp01(heightMap[x, z]), p);
    }

    /// <summary>
    /// Квантует высоты в terraceLevels уровней — плоские уступы вместо плавных склонов.
    /// На блочном меше границы уровней превращаются в резкие вертикальные обрывы (карст).
    /// Идёт ПОСЛЕ сглаживания, поэтому уступы остаются резкими. Работает в [0,1].
    /// </summary>
    private void ApplyTerracing()
    {
        if (terraceLevels <= 0 || heightMap == null) return;

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float h = Mathf.Clamp01(heightMap[x, z]);
                heightMap[x, z] = Mathf.Round(h * terraceLevels) / terraceLevels;
            }
    }

    /// <summary>
    /// Заякоривает рельеф относительно линии воды на y=0.
    /// Берёт террасированную карту [0,1] и сдвигает её так, чтобы линия воды попала
    /// РОВНО в зазор между уровнями террас — на 0 не стоит ни одна верхушка клетки,
    /// поэтому плоскость воды на 0 не даёт z-fighting ни на одном сиде.
    /// seaLevel задаёт, сколько уровней уходит под воду. После сдвига масштабирует на maxHeight:
    /// земля > 0, мелкое дно ≈ −полступени террасы (реальная глубина «по колено»), глубже — ниже.
    /// Заменяет RenormalizeHeights в пайплайне (тот остаётся в файле, но больше не вызывается).
    /// </summary>
    private void AnchorToSeaLevel()
    {
        if (heightMap == null) return;

        float cut01;
        if (terraceLevels > 0)
        {
            // Режем по половинному зазору между уровнями: линия воды посередине между
            // соседними террасами, поэтому 0 никогда не совпадёт с верхушкой клетки.
            int seaCut = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(seaLevel) * terraceLevels), 0, terraceLevels);
            cut01 = (seaCut - 0.5f) / terraceLevels;
        }
        else
        {
            // Без террас зазоров нет — просто сдвигаем на долю seaLevel
            // (на непрерывных высотах совпадение с плоскостью маловероятно, z-fighting не возникает).
            cut01 = Mathf.Clamp01(seaLevel);
        }

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = (heightMap[x, z] - cut01) * maxHeight;
    }

    /// <summary>
    /// Пробивает omutCount глубоких воронок в низинах (центры — только в нижних клетках,
    /// с отступом от краёв и разнесённые между собой). Глубина = omutDepth * maxHeight.
    /// Вызывается последним: ни сглаживание, ни террасы, ни RenormalizeHeights их не трогают,
    /// поэтому стенки остаются резкими, а дно — глубоко под уровнем воды.
    /// </summary>
    private void CarveOmuts()
    {
        if (omutCount <= 0 || heightMap == null) return;

        int margin = OmutRadius + 1;
        if (width <= 2 * margin || depth <= 2 * margin) return;

        // Порог низин: центры омутов только там, где низко (≈ нижние 40% диапазона).
        float min = float.MaxValue, max = float.MinValue;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float h = heightMap[x, z];
                if (h < min) min = h;
                if (h > max) max = h;
            }
        float thr = min + 0.4f * (max - min);

        var rng = new System.Random(seed);
        float depthUnits = omutDepth * maxHeight;

        var placed = new System.Collections.Generic.List<Vector2Int>();
        int attempts = 0, maxAttempts = omutCount * 40;
        int minSep = OmutRadius * 3;

        while (placed.Count < omutCount && attempts < maxAttempts)
        {
            attempts++;
            int cx = margin + rng.Next(width - 2 * margin);
            int cz = margin + rng.Next(depth - 2 * margin);

            if (heightMap[cx, cz] > thr) continue;   // только низины

            bool tooClose = false;
            foreach (var pcenter in placed)
                if ((pcenter.x - cx) * (pcenter.x - cx) + (pcenter.y - cz) * (pcenter.y - cz) < minSep * minSep)
                { tooClose = true; break; }
            if (tooClose) continue;

            placed.Add(new Vector2Int(cx, cz));

            // Воронка: полная глубина во внутренней половине, плавный спад на внешнем кольце → резкие стенки.
            for (int ox = -OmutRadius; ox <= OmutRadius; ox++)
                for (int oz = -OmutRadius; oz <= OmutRadius; oz++)
                {
                    int nx = cx + ox, nz = cz + oz;
                    if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;

                    float dist = Mathf.Sqrt(ox * ox + oz * oz);
                    if (dist > OmutRadius) continue;

                    float t = dist / OmutRadius;                       // 0 в центре … 1 на краю
                    float fall = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.5f) / 0.5f));
                    heightMap[nx, nz] -= depthUnits * fall;
                }
        }
    }

    /// <summary>Берёт tileSize из билдера, чтобы шум считался в мировых единицах.</summary>
    private float ResolveTileSize()
    {
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (seamlessBuilder == null) seamlessBuilder = GetComponent<SeamlessTerrainBuilder>();
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        if (seamlessBuilder != null) return seamlessBuilder.tileSize;
        return 1f;
    }

    /// <summary>
    /// Растягивает карту высот в [0, maxHeight]. Возвращает рельефу полную амплитуду,
    /// которую съедают сглаживание и нормировка fBm. После этого maxHeight честно
    /// задаёт высоту, а smoothPasses влияет только на плавность.
    /// </summary>
    private void RenormalizeHeights()
    {
        if (heightMap == null) return;

        float min = float.MaxValue, max = float.MinValue;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float h = heightMap[x, z];
                if (h < min) min = h;
                if (h > max) max = h;
            }

        float range = max - min;
        if (range < 1e-6f) return;

        float inv = maxHeight / range;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = (heightMap[x, z] - min) * inv;
    }

    /// <summary>
    /// Очищает карту высот.
    /// </summary>
    public void Clear()
    {
        heightMap = null;
        isGenerated = false;
    }

    /// <summary>
    /// Безопасно возвращает высоту по индексам ячейки.
    /// </summary>
    public float GetHeight(int x, int z)
    {
        if (heightMap == null) return 0f;
        if (x < 0 || x >= width || z < 0 || z >= depth) return 0f;
        return heightMap[x, z];
    }

    /// <summary>
    /// Возвращает высоту с билинейной интерполяцией.
    /// </summary>
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

    /// <summary>
    /// Возвращает высоту поверхности в произвольной мировой точке.
    /// </summary>
    public float GetHeightAtWorldPos(Vector3 worldPos, float tileSize, Vector3 mapOrigin)
    {
        return GetHeightBilinear(worldPos.x, worldPos.z, tileSize, mapOrigin);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!isGenerated || heightMap == null) return;

        float tileSize = 1f;
        var builder = GetComponent<SeamlessTerrainBuilder>();
        if (builder != null)
            tileSize = builder.tileSize;

        Vector3 mapOrigin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);

        for (int x = 0; x < width; x += Mathf.Max(1, width / 20))
        {
            for (int z = 0; z < depth; z += Mathf.Max(1, depth / 20))
            {
                float h = heightMap[x, z];
                Vector3 pos = mapOrigin + new Vector3(x * tileSize, h, z * tileSize);
                Gizmos.color = Color.Lerp(Color.blue, Color.red, h / maxHeight);
                Gizmos.DrawWireCube(pos, Vector3.one * 0.2f);
            }
        }
    }
#endif
}
    