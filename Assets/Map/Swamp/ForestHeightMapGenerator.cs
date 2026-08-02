// ForestHeightMapGenerator v1.0
// Плоский лес: база 0, поверх — вложенные широкие плато с очень низкими уступами.
// Воды нет: минимум карты = 0, TerrainZoneSystem считает водой только height < 0.
// Порядок: шум -> пороги по квантилям -> квантование -> чистка рванины и мелких пятен.
using UnityEngine;

/// <summary>
/// Генератор рельефа леса. Крупный низкочастотный шум режется на уровни по квантилям:
/// каждый следующий уровень занимает долю площади предыдущего (levelFalloff), поэтому
/// верхние плато редкие и лежат внутри нижних — как контурные линии на карте.
/// Высота уровня i = i * terraceStep (terraceStep унаследован от базы).
/// </summary>
public class ForestHeightMapGenerator : HeightMapGenerator
{
    [Header("Плато")]
    [Tooltip("Сколько уровней над базовым. Высоты: 0, terraceStep, 2*terraceStep ... maxSteps*terraceStep.")]
    public int maxSteps = 4;

    [Tooltip("Крупность пятен. Умножается на мировые координаты: меньше = шире и реже плато.")]
    public float plateauScale = 0.008f;

    [Range(0f, 1f)]
    [Tooltip("Доля карты выше базового уровня (первый уступ).")]
    public float firstLevelFraction = 0.35f;

    [Range(0.05f, 1f)]
    [Tooltip("Каждый следующий уровень занимает эту долю площади предыдущего. 0.4 = сужение вчетверо к верху.")]
    public float levelFalloff = 0.4f;

    protected override void BuildHeights(float tileSize)
    {
        maxHeight = terraceStep * Mathf.Max(1, maxSteps); // держим верх диапазона в согласии с реальным рельефом

        BuildPlateaus(tileSize);
        FlattenMinorSteps();   // подчищает зубцы на границах плато
        CleanupSmallClusters();
    }

    /// <summary>
    /// Считает шум по всем клеткам, набирает пороги по квантилям (площадь каждого следующего
    /// уровня = levelFalloff от предыдущего) и присваивает клетке высоту = число пройденных порогов.
    /// Квантили вместо фиксированных порогов — доля площади держится одинаковой при любом сиде.
    /// </summary>
    private void BuildPlateaus(float tileSize)
    {
        Vector3 origin = MapOrigin(tileSize);
        float seedOffset = seed * 0.17f;

        // 1) шум по всем клеткам + копия для сортировки
        float[,] noise = new float[width, depth];
        float[] sorted = new float[width * depth];
        int idx = 0;
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float wx = origin.x + x * tileSize;
                float wz = origin.z + z * tileSize;
                float n = Mathf.PerlinNoise(wx * plateauScale + seedOffset, wz * plateauScale + seedOffset);
                noise[x, z] = n;
                sorted[idx++] = n;
            }
        System.Array.Sort(sorted);

        // 2) пороги: доля площади сужается на каждом уровне
        int levels = Mathf.Max(1, maxSteps);
        float[] thresholds = new float[levels];
        float frac = Mathf.Clamp01(firstLevelFraction);
        for (int i = 0; i < levels; i++)
        {
            int qi = Mathf.Clamp(Mathf.RoundToInt((1f - frac) * (sorted.Length - 1)), 0, sorted.Length - 1);
            thresholds[i] = sorted[qi];
            frac *= Mathf.Clamp(levelFalloff, 0.05f, 1f);
        }

        // 3) высота = число пройденных порогов * шаг
        for (int x = 0; x < width; x++)
            for (int z = 0; z < depth; z++)
            {
                float n = noise[x, z];
                int step = 0;
                for (int i = 0; i < levels; i++)
                {
                    if (n < thresholds[i]) break;
                    step++;
                }
                heightMap[x, z] = step * terraceStep;
            }
    }
}
