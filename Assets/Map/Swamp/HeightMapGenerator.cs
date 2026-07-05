// HeightMapGenerator v2.5
// Модель: сначала поверхность (равнина + холмы над ней), потом ямы в низинах.
// Порядок: FillPlain -> PlaceHills -> pits -> corridor -> террасы -> кластер>=5.
// Всё в мировых единицах, без Normalize/Anchor. См. HeightMapGenerator_Инструкция_v2.1.md
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Генерирует карту высот болота: равнина -5..+2, холмы +3..+10 над ней (сухие),
/// глубокие ямы -6..-10 в низинах вдали от холмов, приподнятый коридор под дорогу.
/// Единственный источник высот для остальных скриптов.
/// </summary>
public class HeightMapGenerator : MonoBehaviour
{
    public const string Version = "2.5";

    [Header("Размеры карты")]
    public int width = 60;
    public int depth = 60;

    [Header("Масштаб мира (источник tileSize)")]
    public ChunkedTerrainBuilder chunkedBuilder;

    [Header("Сид")]
    public bool randomSeed = true;
    public int seed;

    [Header("Равнина (базовая поверхность = проходимое болото)")]
    public Vector2 plainRange = new Vector2(-5f, 2f); // x = макс. глубина лужиц (низ), y = макс. высота сухих кочек (верх)
    public float plainNoiseScale = 0.04f;
    [Range(0f, 1f)] public float landFraction = 0.4f;  // доля болота над водой (y=0): суша ↔ мелководье; самокалибруется под сид

    [Header("Холмы (над равниной, сухие)")]
    [Range(0f, 1f)] public float hillDensity = 0.5f;
    public float hillMinSeparation = 0.12f;   // доля короткой стороны
    public float edgeMargin = 0.1f;           // доля короткой стороны
    public float maxHeight = 10f;             // верх диапазона высот холмов (главная величина)
    [Range(0f, 1f)] public float hillMinHeightFrac = 0.3f; // нижний холм = доля от maxHeight
    public Vector2 hillSizeRange = new Vector2(0.08f, 0.2f); // доля короткой стороны; размер растёт с высотой

    [Header("Направление гряды (ориентация холмов)")]
    public float ridgeDirection = 0f;         // градусы, 0 = вдоль X
    public float ridgeDirectionSpread = 25f;  // разброс угла на холм, градусы
    public float ridgeElongation = 2f;        // вытянутость эллипса вдоль направления (1 = круглый)

    [Header("Глубокие ямы (в низинах, вдали от холмов)")]
    [Range(0f, 1f)] public float pitDensity = 0.4f;
    public Vector2 pitRange = new Vector2(-6f, -10f);
    public Vector2 pitSizeRange = new Vector2(0.05f, 0.12f); // доля короткой стороны
    public float pitClearance = 0.15f;        // мин. дистанция ямы до холма, доля короткой стороны
    public float pitLowlandThreshold = 0f;    // копаем только там, где поверхность ниже этого уровня

    [Header("Коридор под дорогу")]
    public float corridorHalfWidth = 3f;      // полуширина коридора, мировые единицы
    public float corridorHeight = 0.5f;       // мин. высота коридора (держим над водой)

    [Header("Форма (domain-warp)")]
    public float warpStrength = 0f;
    public float warpScale = 0.05f;

    [Header("Террасы и очистка")]
    public int smoothPasses = 2;              // сглаживание поверхности перед квантованием (шире площадки одной высоты)
    public float terraceStep = 0.5f;
    public int maxSmoothStep = 1;             // перепад с соседями в СТУПЕНЯХ <= этого — прилипает (мелкая рванина); больше = обрыв, не трогаем
    public int minClusterCells = 5;           // пятно одной высоты меньше этого числа — прилипает к соседу

    // Публичные данные — доступ для всех скриптов
    public float[,] heightMap { get; private set; }
    public bool isGenerated { get; private set; }

    // ============ СОБЫТИЯ ============
    public System.Action onHeightMapReady;
    public System.Action<float[,]> onHeightMapGenerated;

    // Число холмов от площади карты (тайлы^2), а не константой в инспекторе.
    private const float TilesPerHillMin = 400f;
    private const float TilesPerHillMax = 2200f;
    private const int AbsoluteMinHills = 3;
    private const int AbsoluteMaxHills = 40;

    // Число ям — тоже от площади, свои константы (ям меньше, чем холмов).
    private const float TilesPerPitMin = 900f;
    private const float TilesPerPitMax = 4000f;
    private const int AbsoluteMaxPits = 25;

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
        CarvePits(hills, ts);           // ямы в низинах вдали от холмов
        BuildCorridor(hills, ts);       // приподнятый коридор под дорогу край-в-край
        SmoothHeightMap();              // сглаживаем поверхность → шире площадки одной высоты
        ApplyTerraces();
        FlattenMinorSteps();           // мелкие одноступенчатые переходы прилипают, крупные обрывы сохраняются
        CleanupSmallClusters();

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
    /// Заливает карту базовым болотом. Считает шум по всем клеткам, находит порог,
    /// выше которого лежит ровно landFraction клеток (самокалибровка под сид), и мапит
    /// относительно воды (y=0): выше порога → сухие кочки 0..plainRange.y,
    /// ниже → мелководье/лужи plainRange.x..0. Так доля суши задаётся точно при любом сиде.
    /// </summary>
    private void FillPlain(float tileSize)
    {
        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        float seedOffset = seed * 0.13f;

        // 1) шум по всем клеткам (копию складываем в массив для сортировки)
        float[,] noise = new float[width, depth];
        float[] sorted = new float[width * depth];
        int idx = 0;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float wx = origin.x + x * tileSize;
                float wz = origin.z + z * tileSize;
                float n = 1f - Mathf.Abs(2f * Mathf.PerlinNoise(wx * plainNoiseScale + seedOffset, wz * plainNoiseScale + seedOffset) - 1f); // ridged: гребни там, где Перлин ≈ 0.5 → тонкие извилистые полосы суши
                noise[x, z] = n;
                sorted[idx++] = n;
            }

        // 2) порог: доля клеток выше него ≈ landFraction (квантиль вместо линейного мапа — Перлин кучкуется у 0.5)
        System.Array.Sort(sorted);
        float lf = Mathf.Clamp01(landFraction);
        int qi = Mathf.Clamp(Mathf.RoundToInt((1f - lf) * (sorted.Length - 1)), 0, sorted.Length - 1);
        float threshold = sorted[qi];

        // 3) ремап относительно воды (y=0)
        float upSpan = Mathf.Max(1e-4f, 1f - threshold);   // защита от деления на ~0 при landFraction→0/1
        float downSpan = Mathf.Max(1e-4f, threshold);
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float n = noise[x, z];
                heightMap[x, z] = (n >= threshold)
                    ? Mathf.Lerp(0f, plainRange.y, (n - threshold) / upSpan)   // сухие кочки над водой
                    : Mathf.Lerp(plainRange.x, 0f, n / downSpan);              // мелководье → лужи под водой
            }
    }

    /// <summary>
    /// Расставляет холмы: число от площади, позиции с мин. дистанцией, высота от maxHeight и доли,
    /// размер растёт с высотой, ориентация — ridgeDirection с разбросом.
    /// </summary>
    private List<Hill> PlaceHills(float tileSize)
    {
        var list = new List<Hill>();

        float areaTiles = width * depth;
        int minN = Mathf.Clamp(Mathf.RoundToInt(areaTiles / TilesPerHillMax), AbsoluteMinHills, AbsoluteMaxHills);
        int maxN = Mathf.Clamp(Mathf.RoundToInt(areaTiles / TilesPerHillMin), minN, AbsoluteMaxHills);
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
            float sz = Mathf.Lerp(hillSizeRange.x, hillSizeRange.y, hFrac) * shorter; // размер от высоты
            float ang = dirRad + ((float)rng.NextDouble() - 0.5f) * 2f * ridgeDirectionSpread * Mathf.Deg2Rad;

            list.Add(new Hill { pos = p, height = hgt, size = sz, angle = ang });
        }

        return list;
    }

    /// <summary>
    /// Ядро. Холмы поднимаются над равниной: для клетки берём max(равнина, вклад холмов).
    /// Вклад холма — эллиптический спад (вытянут вдоль angle через ridgeElongation).
    /// Позиция искажается domain-warp'ом → берега не круглые.
    /// </summary>
    private void BuildField(List<Hill> hills, float tileSize)
    {
        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        float seedOffset = seed * 0.1f;

        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float plain = heightMap[x, z];
                Vector2 pos = new Vector2(origin.x + x * tileSize, origin.z + z * tileSize);
                Vector2 sp = SampleWarp(pos, seedOffset);

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
    /// Копает глубокие ямы pitRange в низинах, удалённых от холмов (вариант «расстояние до вершин»).
    /// Центры выбираются там, где поверхность ниже порога и далеко от холмов; спад через min.
    /// </summary>
    private void CarvePits(List<Hill> hills, float tileSize)
    {
        float areaTiles = width * depth;
        int maxN = Mathf.Clamp(Mathf.RoundToInt(areaTiles / TilesPerPitMin), 0, AbsoluteMaxPits);
        int minN = Mathf.Clamp(Mathf.RoundToInt(areaTiles / TilesPerPitMax), 0, maxN);
        int count = Mathf.RoundToInt(Mathf.Lerp(minN, maxN, Mathf.Clamp01(pitDensity)));
        if (count <= 0) return;

        var rng = new System.Random(seed + 777);
        Vector3 origin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
        float mapX = width * tileSize, mapZ = depth * tileSize;
        float shorter = Mathf.Min(width, depth) * tileSize;
        float clearance = pitClearance * shorter;
        float margin = edgeMargin * shorter;

        var pits = new List<(Vector2 pos, float depth, float size)>();
        int attempts = 0, maxAttempts = count * 80;
        while (pits.Count < count && attempts < maxAttempts)
        {
            attempts++;
            float wx = origin.x + margin + (float)rng.NextDouble() * (mapX - 2f * margin);
            float wz = origin.z + margin + (float)rng.NextDouble() * (mapZ - 2f * margin);
            var p = new Vector2(wx, wz);

            // только низины
            int cx = Mathf.Clamp(Mathf.RoundToInt((wx - origin.x) / tileSize), 0, width - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt((wz - origin.z) / tileSize), 0, depth - 1);
            if (heightMap[cx, cz] > pitLowlandThreshold) continue;

            // далеко от холмов
            bool nearHill = false;
            foreach (var hill in hills)
                if (Vector2.Distance(p, hill.pos) < clearance) { nearHill = true; break; }
            if (nearHill) continue;

            float depthVal = Mathf.Lerp(pitRange.x, pitRange.y, (float)rng.NextDouble());
            float sz = Mathf.Lerp(pitSizeRange.x, pitSizeRange.y, (float)rng.NextDouble()) * shorter;
            pits.Add((p, depthVal, sz));
        }

        float seedOffset = seed * 0.1f;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                Vector2 pos = new Vector2(origin.x + x * tileSize, origin.z + z * tileSize);
                Vector2 sp = SampleWarp(pos, seedOffset);
                float h = heightMap[x, z];
                foreach (var pit in pits)
                {
                    float d = Vector2.Distance(sp, pit.pos);
                    float t = Mathf.Clamp01(d / pit.size);
                    float fall = 1f - Mathf.SmoothStep(0f, 1f, t);
                    float dug = Mathf.Lerp(h, pit.depth, fall);
                    if (dug < h) h = dug;
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

    /// <summary>
    /// Box-blur 3×3 за smoothPasses проходов по готовой карте высот (до квантования).
    /// Давит мелкую рябь равнины и смягчает склоны холмов → площадки одной высоты крупнее,
    /// меньше тонких контурных ступеней после террас.
    /// </summary>
    private void SmoothHeightMap()
    {
        if (smoothPasses <= 0 || heightMap == null) return;

        float[,] tmp = new float[width, depth];
        for (int p = 0; p < smoothPasses; p++)
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

    /// <summary>Квантует высоты одним шагом terraceStep → плоские уступы, обрывы рисует меш.</summary>
    private void ApplyTerraces()
    {
        if (terraceStep <= 0f) return;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
                heightMap[x, z] = (Mathf.Floor(heightMap[x, z] / terraceStep) + 0.1f) * terraceStep;
    }

    /// <summary>
    /// Прибирает мелкую рванину после квантования: клетка, отличающаяся от самого частого
    /// соседнего уровня не больше чем на maxSmoothStep ступеней, прилипает к нему.
    /// Крупные перепады (обрыв/берег, больше порога) сохраняются. 4-связность, один проход в копию.
    /// </summary>
    private void FlattenMinorSteps()
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

                if (bestF >= 2) heightMap[x, z] = best; // прилипаем, только если сосед преобладает (не одиночный)
            }
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

    /// <summary>Сдвигает позицию клетки по шуму → линии равной высоты кривые, а не круги.</summary>
    private Vector2 SampleWarp(Vector2 pos, float seedOffset)
    {
        if (warpStrength <= 0f) return pos;
        float wx = (Mathf.PerlinNoise(pos.x * warpScale + seedOffset, pos.y * warpScale + seedOffset) - 0.5f) * 2f * warpStrength;
        float wz = (Mathf.PerlinNoise(pos.x * warpScale + seedOffset + 100f, pos.y * warpScale + seedOffset + 100f) - 0.5f) * 2f * warpStrength;
        return new Vector2(pos.x + wx, pos.y + wz);
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
                float t = Mathf.InverseLerp(pitRange.x, maxHeight, h);
                Gizmos.color = Color.Lerp(Color.blue, Color.red, t);
                Gizmos.DrawWireCube(pos, Vector3.one * 0.2f);
            }
        }
    }
#endif
}
