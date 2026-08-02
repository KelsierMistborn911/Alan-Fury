// SwampHeightMapGenerator v2.6 (бывший HeightMapGenerator)
// Модель: равнина + холмы над ней. Ямы убраны из генерации (пока).
// Порядок: FillPlain -> PlaceHills -> BuildField -> corridor -> сглаживание -> террасы -> чистка.
// Всё в мировых единицах, без Normalize/Anchor. См. HeightMapGenerator_Инструкция_v2.1.md
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Генерирует карту высот болота: равнина -5..+2, холмы +3..+10 над ней (сухие).
/// Общая часть (размеры, сид, террасы, доступ к высотам) — в базовом HeightMapGenerator.
/// </summary>
public class SwampHeightMapGenerator : HeightMapGenerator
{
    [Header("Равнина (базовая поверхность = проходимое болото)")]
    public Vector2 plainRange = new Vector2(-5f, 2f); // x = макс. глубина лужиц (низ), y = макс. высота сухих кочек (верх)
    public float plainNoiseScale = 0.04f;
    [Range(0f, 1f)] public float landFraction = 0.4f;  // доля болота над водой (y=0): суша ↔ мелководье; самокалибруется под сид

    [Header("Холмы (над равниной, сухие)")]
    [Range(0f, 1f)] public float hillDensity = 0.5f;
    public float hillMinSeparation = 0.12f;   // доля короткой стороны
    public float edgeMargin = 0.1f;           // доля короткой стороны
    [Range(0f, 1f)] public float hillMinHeightFrac = 0.3f; // нижний холм = доля от maxHeight
    public Vector2 hillSizeRange = new Vector2(0.08f, 0.2f); // доля короткой стороны; размер растёт с высотой
    public float hillSizeScale = 1f;          // множитель размера холма поверх hillSizeRange

    [Header("Направление гряды (ориентация холмов)")]
    public float ridgeDirection = 0f;         // градусы, 0 = вдоль X
    public float ridgeDirectionSpread = 25f;  // разброс угла на холм, градусы
    public float ridgeElongation = 2f;        // вытянутость эллипса вдоль направления (1 = круглый)

    [Header("Коридор под дорогу")]
    public float corridorHalfWidth = 3f;      // полуширина коридора, мировые единицы
    public float corridorHeight = 0.5f;       // мин. высота коридора (держим над водой)

    [Header("Форма (domain-warp)")]
    public float warpStrength = 0f;
    public float warpScale = 0.05f;

    [Header("Сглаживание")]
    public int smoothPasses = 2;              // сглаживание поверхности перед квантованием

    // Число холмов от площади карты в МИРОВЫХ единицах (метры^2), не в тайлах —
    // так плотность не скачет при смене tileSize.
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

    protected override void BuildHeights(float tileSize)
    {
        FillPlain(tileSize);
        var hills = PlaceHills(tileSize);
        BuildField(hills, tileSize);    // холмы поднимаются над равниной
        BuildCorridor(hills, tileSize); // приподнятый коридор под дорогу край-в-край
        SmoothHeightMap(smoothPasses);  // шире площадки одной высоты
        ApplyTerraces();
        FlattenMinorSteps();            // мелкие переходы прилипают, крупные обрывы сохраняются
        CleanupSmallClusters();
    }

    /// <summary>
    /// Заливает карту базовым болотом. Считает шум по всем клеткам, находит порог,
    /// выше которого лежит ровно landFraction клеток (самокалибровка под сид), и мапит
    /// относительно воды (y=0): выше порога → сухие кочки 0..plainRange.y,
    /// ниже → мелководье/лужи plainRange.x..0.
    /// </summary>
    private void FillPlain(float tileSize)
    {
        Vector3 origin = MapOrigin(tileSize);
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
                float n = 1f - Mathf.Abs(2f * Mathf.PerlinNoise(wx * plainNoiseScale + seedOffset, wz * plainNoiseScale + seedOffset) - 1f); // ridged: тонкие извилистые полосы суши
                noise[x, z] = n;
                sorted[idx++] = n;
            }

        // 2) порог: доля клеток выше него ≈ landFraction (квантиль вместо линейного мапа)
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

        float areaWorld = width * depth * tileSize * tileSize;
        int minN = Mathf.Clamp(Mathf.RoundToInt(areaWorld / WorldAreaPerHillMax), AbsoluteMinHills, AbsoluteMaxHills);
        int maxN = Mathf.Clamp(Mathf.RoundToInt(areaWorld / WorldAreaPerHillMin), minN, AbsoluteMaxHills);
        int count = Mathf.RoundToInt(Mathf.Lerp(minN, maxN, Mathf.Clamp01(hillDensity)));

        var rng = new System.Random(seed);
        Vector3 origin = MapOrigin(tileSize);
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
    /// Позиция искажается domain-warp'ом → берега не круглые.
    /// </summary>
    private void BuildField(List<Hill> hills, float tileSize)
    {
        Vector3 origin = MapOrigin(tileSize);
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
    /// Приподнятый коридор под дорогу край-в-край: ломаная через холмы, отсортированные
    /// вдоль длинной оси, продлённая до краёв карты. Внутри полуширины держим высоту не ниже
    /// corridorHeight (max), холмы выше него остаются как есть.
    /// </summary>
    private void BuildCorridor(List<Hill> hills, float tileSize)
    {
        if (hills.Count == 0) return;

        Vector3 origin = MapOrigin(tileSize);
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

#if UNITY_EDITOR
    protected override float GizmoMinHeight => plainRange.x;
#endif
}
