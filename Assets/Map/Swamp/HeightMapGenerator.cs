// HeightMapGenerator
// Модель: равнина + холмы над ней. Ямы убраны из генерации (пока).
// Порядок: FillPlain (Перлин 2 октавы) -> PlaceHills -> corridor -> террасы -> кластер -> сглаживание границ.
// Всё в мировых единицах, без Normalize/Anchor. См. HeightMapGenerator_Инструкция_v2.1.md
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Генерирует карту высот болота: равнина -5..+2, холмы +3..+10 над ней (сухие).
/// Единственный источник высот для остальных скриптов.
/// </summary>
public class HeightMapGenerator : MonoBehaviour
{
    [Header("Размеры карты")]
    public int width = 60;
    public int depth = 60;

    [Header("Масштаб мира (источник tileSize)")]
    public ChunkedTerrainBuilder chunkedBuilder;

    [Header("Сид")]
    public bool randomSeed = true;
    public int seed;

    [Header("Равнина (базовая поверхность = проходимое болото)")]
    public float puddleDepth = 4f;            // глубина луж под водой, юниты
    public float moundHeight = 3f;            // высота сухих кочек над водой, юниты
    public float spotSize = 60f;              // характерный размер пятна суши/воды, мировые юниты
    [Range(0f, 1f)] public float landFraction = 0.4f;  // доля болота над водой (y=0): суша ↔ мелководье; самокалибруется под сид

    [Header("Холмы (над равниной, сухие)")]
    [Range(0f, 1f)] public float hillDensity = 0.5f;
    public float hillMinSeparation = 0.12f;   // доля короткой стороны
    public float edgeMargin = 0.1f;           // доля короткой стороны
    public float maxHeight = 10f;             // верх диапазона высот холмов (главная величина)
    [Range(0f, 1f)] public float hillMinHeightFrac = 0.3f; // нижний холм = доля от maxHeight
    public Vector2 hillSizeRange = new Vector2(0.08f, 0.2f); // доля короткой стороны; размер растёт с высотой
    public float hillSizeScale = 1f;          // множитель размера холма поверх hillSizeRange — регулирует средний размер отдельно от высоты/частоты

    [Header("Направление гряды (ориентация холмов)")]
    public float ridgeDirection = 0f;         // градусы, 0 = вдоль X
    public float ridgeDirectionSpread = 25f;  // разброс угла на холм, градусы
    public float ridgeElongation = 2f;        // вытянутость эллипса вдоль направления (1 = круглый)

    [Header("Коридор под дорогу")]
    public float corridorHalfWidth = 3f;      // полуширина коридора, мировые единицы
    public float corridorHeight = 0.5f;       // мин. высота коридора (держим над водой)

    [Header("Террасы и очистка")]
    public float terraceStep = 0.5f;
    public int minClusterCells = 5;           // пятно одной высоты меньше этого числа — прилипает к соседу
    public int boundarySmoothPasses = 2;      // проходы сглаживания границ уровней: клетка с 3+ соседями одного чужого уровня прилипает к нему (0 = выкл)

    // Публичные данные — доступ для всех скриптов
    public float[,] heightMap { get; private set; }
    public bool isGenerated { get; private set; }

    // ============ СОБЫТИЯ ============
    public System.Action onHeightMapReady;
    public System.Action<float[,]> onHeightMapGenerated;

    // Число холмов от площади карты в МИРОВЫХ единицах (метры^2), не в тайлах —
    // так плотность не скачет при смене tileSize. Константы пересчитаны из старых
    // "тайловых" (400/2200) под tileSize=4, чтобы поведение при текущих настройках не изменилось.
    private const float WorldAreaPerHillMin = 6400f;
    private const float WorldAreaPerHillMax = 35200f;
    private const int AbsoluteMinHills = 3;
    private const int AbsoluteMaxHills = 40;

    private struct Hill
    {
        public Vector2 pos;
        public float height;
        public float size;
        public float angle; // радианы, ориентация эллипса
    }

    /// <summary>Запускает генерацию карты высот.</summary>
    public void Generate()
    {
        if (randomSeed)
            seed = UnityEngine.Random.Range(0, 100000);

        heightMap = new float[width, depth];

        float ts = ResolveTileSize();

        FillPlain(ts);
        var hills = PlaceHills(ts);
        BuildField(hills, ts);          // холмы поднимаются над равниной
        BuildCorridor(hills, ts);       // приподнятый коридор под дорогу край-в-край
        ApplyTerraces();
        CleanupSmallClusters();
        SmoothLevelBoundaries();       // зубцы, одиночные выступы и вмятины на границах уровней прилипают к преобладающему соседу

        isGenerated = true;
        onHeightMapReady?.Invoke();
        onHeightMapGenerated?.Invoke(heightMap);
    }

    /// <summary>Берёт tileSize из билдера, чтобы всё считалось в мировых единицах.</summary>
    private float ResolveTileSize()
    {
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 1f;
    }

    /// <summary>
    /// Заливает карту базовым болотом. Гладкий Перлин в 2 октавы (крупная форма + лёгкая деталь),
    /// размер пятен задаёт spotSize в мировых юнитах. Порог-квантиль: выше него лежит ровно
    /// landFraction клеток (точная доля суши при любом сиде). Выше порога → кочки 0..moundHeight,
    /// ниже → мелководье/лужи -puddleDepth..0.
    /// </summary>
    private void FillPlain(float tileSize)
    {
        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        float seedOffset = seed * 0.13f;
        float freq = 1f / Mathf.Max(1f, spotSize);

        // 1) шум по всем клеткам (копию складываем в массив для сортировки)
        float[,] noise = new float[width, depth];
        float[] sorted = new float[width * depth];
        int idx = 0;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float wx = origin.x + x * tileSize;
                float wz = origin.z + z * tileSize;
                float n = Mathf.PerlinNoise(wx * freq + seedOffset, wz * freq + seedOffset)
                        + 0.35f * Mathf.PerlinNoise(wx * freq * 2f + seedOffset + 50f, wz * freq * 2f + seedOffset + 50f);
                noise[x, z] = n;
                sorted[idx++] = n;
            }

        // 2) порог: доля клеток выше него ≈ landFraction (квантиль вместо линейного мапа — Перлин кучкуется у середины)
        System.Array.Sort(sorted);
        float lf = Mathf.Clamp01(landFraction);
        int qi = Mathf.Clamp(Mathf.RoundToInt((1f - lf) * (sorted.Length - 1)), 0, sorted.Length - 1);
        float threshold = sorted[qi];

        // 3) ремап относительно воды (y=0)
        float top = sorted[sorted.Length - 1];
        float upSpan = Mathf.Max(1e-4f, top - threshold);  // защита от деления на ~0 при landFraction→0/1
        float downSpan = Mathf.Max(1e-4f, threshold - sorted[0]);
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float n = noise[x, z];
                heightMap[x, z] = (n >= threshold)
                    ? Mathf.Lerp(0f, moundHeight, (n - threshold) / upSpan)          // сухие кочки над водой
                    : Mathf.Lerp(-puddleDepth, 0f, (n - sorted[0]) / downSpan);      // мелководье → лужи под водой
            }
    }

    /// <summary>
    /// Расставляет холмы: число от площади, позиции с мин. дистанцией, высота от maxHeight и доли,
    /// размер растёт с высотой, ориентация — ridgeDirection с разбросом.
    /// </summary>
    private List<Hill> PlaceHills(float tileSize)
    {
        var list = new List<Hill>();

        float areaWorld = width * depth * tileSize * tileSize;
        int minN = Mathf.Clamp(Mathf.RoundToInt(areaWorld / WorldAreaPerHillMax), AbsoluteMinHills, AbsoluteMaxHills);
        int maxN = Mathf.Clamp(Mathf.RoundToInt(areaWorld / WorldAreaPerHillMin), minN, AbsoluteMaxHills);
        int count = Mathf.RoundToInt(Mathf.Lerp(minN, maxN, Mathf.Clamp01(hillDensity)));

        var rng = new System.Random(seed);
        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        float mapX = width * tileSize, mapZ = depth * tileSize;
        float shorter = Mathf.Min(width, depth) * tileSize;
        float sep = hillMinSeparation * shorter;
        float margin = edgeMargin * shorter;
        float dirRad = ridgeDirection * Mathf.Deg2Rad;

        int attempts = 0, maxAttempts = count * 50;
        while (list.Count < count && attempts < maxAttempts)
        {
            attempts++;
            float wx = origin.x + margin + (float)rng.NextDouble() * (mapX - 2f * margin);
            float wz = origin.z + margin + (float)rng.NextDouble() * (mapZ - 2f * margin);
            var p = new Vector2(wx, wz);

            bool tooClose = false;
            foreach (var o in list)
                if (Vector2.Distance(p, o.pos) < sep) { tooClose = true; break; }
            if (tooClose) continue;

            float hFrac = (float)rng.NextDouble();
            float hgt = Mathf.Lerp(maxHeight * hillMinHeightFrac, maxHeight, hFrac);
            float sz = Mathf.Lerp(hillSizeRange.x, hillSizeRange.y, hFrac) * shorter * hillSizeScale; // размер от высоты + общий множитель
            float ang = dirRad + ((float)rng.NextDouble() - 0.5f) * 2f * ridgeDirectionSpread * Mathf.Deg2Rad;

            list.Add(new Hill { pos = p, height = hgt, size = sz, angle = ang });
        }

        return list;
    }

    /// <summary>
    /// Ядро. Холмы поднимаются над равниной: для клетки берём max(равнина, вклад холмов).
    /// Вклад холма — эллиптический спад (вытянут вдоль angle через ridgeElongation).
    /// </summary>
    private void BuildField(List<Hill> hills, float tileSize)
    {
        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float plain = heightMap[x, z];
                Vector2 sp = new Vector2(origin.x + x * tileSize, origin.z + z * tileSize);

                float h = plain;
                foreach (var hill in hills)
                {
                    float d = EllipticalDistance(sp, hill);
                    float t = Mathf.Clamp01(d / hill.size);
                    float fall = 1f - Mathf.SmoothStep(0f, 1f, t);
                    float contrib = Mathf.Lerp(plain, hill.height, fall);
                    if (contrib > h) h = contrib;
                }
                heightMap[x, z] = h;
            }
    }

    /// <summary>
    /// Приподнятый коридор под дорогу край-в-край: ломаная через холмы, отсортированные
    /// вдоль длинной оси, продлённая до краёв карты. Внутри полуширины держим высоту не ниже
    /// corridorHeight (max), холмы выше него остаются как есть.
    /// </summary>
    private void BuildCorridor(List<Hill> hills, float tileSize)
    {
        if (hills.Count == 0) return;

        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        float mapX = width * tileSize, mapZ = depth * tileSize;
        bool longX = width >= depth;

        var order = new List<int>();
        for (int i = 0; i < hills.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            float ka = longX ? hills[a].pos.x : hills[a].pos.y;
            float kb = longX ? hills[b].pos.x : hills[b].pos.y;
            return ka.CompareTo(kb);
        });

        // ломаная: край -> холмы по порядку -> противоположный край
        var pts = new List<Vector2>();
        Vector2 first = hills[order[0]].pos;
        Vector2 last = hills[order[order.Count - 1]].pos;
        if (longX)
        {
            pts.Add(new Vector2(origin.x, first.y));
            foreach (var idx in order) pts.Add(hills[idx].pos);
            pts.Add(new Vector2(origin.x + mapX, last.y));
        }
        else
        {
            pts.Add(new Vector2(first.x, origin.z));
            foreach (var idx in order) pts.Add(hills[idx].pos);
            pts.Add(new Vector2(last.x, origin.z + mapZ));
        }

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                Vector2 pos = new Vector2(origin.x + x * tileSize, origin.z + z * tileSize);
                float dPerp = DistanceToPolyline(pos, pts);
                float t = Mathf.Clamp01(dPerp / corridorHalfWidth);
                float fall = 1f - Mathf.SmoothStep(0f, 1f, t);
                if (fall <= 0f) continue;

                float cur = heightMap[x, z];
                float lifted = Mathf.Lerp(cur, corridorHeight, fall);
                if (lifted > cur) heightMap[x, z] = lifted; // только приподнимаем, холмы не режем
            }
    }

    /// <summary>Квантует высоты одним шагом terraceStep → плоские уступы, обрывы рисует меш.</summary>
    private void ApplyTerraces()
    {
        if (terraceStep <= 0f) return;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = (Mathf.Floor(heightMap[x, z] / terraceStep) + 0.1f) * terraceStep;
    }

    /// <summary>
    /// Прилепляет мелкие пятна одной высоты (меньше minClusterCells клеток) к соседнему уровню —
    /// убирает микроступеньки в 1-2 клетки. 4-связность, применяется после квантования.
    /// </summary>
    private void CleanupSmallClusters()
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

    /// <summary>
    /// Сглаживает границы уровней после всех чисток: клетка, у которой 3+ из 4 соседей
    /// стоят на одном и том же чужом уровне, прилипает к нему. Убирает зубцы, одиночные
    /// выступы и вмятины на береговых линиях и обрывах, не меняя форму самих пятен.
    /// Каждый проход читает копию — результат не зависит от порядка обхода.
    /// </summary>
    private void SmoothLevelBoundaries()
    {
        if (boundarySmoothPasses <= 0 || heightMap == null) return;

        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int p = 0; p < boundarySmoothPasses; p++)
        {
            var src = (float[,])heightMap.Clone();
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
                        freq.TryGetValue(nv, out int f);
                        freq[nv] = f + 1;
                    }

                    foreach (var kv in freq)
                        if (kv.Value >= 3) { heightMap[x, z] = kv.Key; break; }
                }
        }
    }

    /// <summary>Расстояние от точки до эллипса холма (вытянут вдоль hill.angle на ridgeElongation).</summary>
    private float EllipticalDistance(Vector2 p, Hill hill)
    {
        Vector2 delta = p - hill.pos;
        float cos = Mathf.Cos(hill.angle), sin = Mathf.Sin(hill.angle);
        float along = delta.x * cos + delta.y * sin;   // вдоль гряды
        float perp = -delta.x * sin + delta.y * cos;   // поперёк
        float e = Mathf.Max(1f, ridgeElongation);
        along /= e;                                     // сжимаем вдоль → форма вытянута вдоль гряды
        return Mathf.Sqrt(along * along + perp * perp);
    }

    /// <summary>Мин. расстояние от точки до ломаной (набор сегментов).</summary>
    private float DistanceToPolyline(Vector2 p, List<Vector2> pts)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            float t = ClosestPointT(p, pts[i], pts[i + 1]);
            Vector2 closest = Vector2.Lerp(pts[i], pts[i + 1], t);
            float d = Vector2.Distance(p, closest);
            if (d < best) best = d;
        }
        return best;
    }

    private static float ClosestPointT(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 1e-6f) return 0f;
        return Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
    }

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
    private void OnDrawGizmos()
    {
        if (!isGenerated || heightMap == null) return;

        float tileSize = chunkedBuilder != null ? chunkedBuilder.tileSize : 1f;
        Vector3 mapOrigin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);

        for (int x = 0; x < width; x += Mathf.Max(1, width / 20))
        {
            for (int z = 0; z < depth; z += Mathf.Max(1, depth / 20))
            {
                float h = heightMap[x, z];
                Vector3 pos = mapOrigin + new Vector3(x * tileSize, h, z * tileSize);
                float t = Mathf.InverseLerp(-puddleDepth, maxHeight, h);
                Gizmos.color = Color.Lerp(Color.blue, Color.red, t);
                Gizmos.DrawWireCube(pos, Vector3.one * 0.2f);
            }
        }
    }
#endif
}
