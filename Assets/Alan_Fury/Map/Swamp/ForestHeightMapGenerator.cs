// ForestHeightMapGenerator v1.3 — простые настройки
// Лесной рельеф: крупные плато + макро 5×5 и ступенчатые переходы (база v3.1).
using UnityEngine;

/// <summary>
/// Карта высот леса: плоские площадки разных уровней, ступени между ними.
/// Три ручки: размер площадок, число уровней, холмистость.
/// </summary>
public class ForestHeightMapGenerator : HeightMapGenerator
{
    public const string ForestVersion = "1.3";

    [Header("Рельеф")]
    [Tooltip("Крупность площадок. Больше = шире плоские зоны (примерно в клетках макро).")]
    [Range(20f, 200f)] public float plateauSize = 80f;

    [Tooltip("Сколько ступеней высоты (1 = почти плоско, 4–5 = заметный рельеф).")]
    [Range(1, 6)] public int heightLevels = 4;

    [Tooltip("0 = больше низких площадок, 1 = больше высоких.")]
    [Range(0f, 1f)] public float hilliness = 0.45f;

    protected override void BuildHeights(float tileSize)
    {
        FillPlateauLevels();
        ApplyTerraces();
        FlattenMinorSteps();
        CleanupSmallClusters();
        ApplyMacroBlocksAndTransitions();
    }

    private void FillPlateauLevels()
    {
        float stepH = terraceStep > 0f ? terraceStep : 1f;
        int levels = Mathf.Max(1, heightLevels);

        // Пороги от hilliness: низкий hilliness → длинный низ, высокий → больше верхних уровней
        float[] thresholds = BuildThresholds(levels, hilliness);

        // plateauSize в «клетках шума»: scale = 1/size
        float scale = 1f / Mathf.Max(8f, plateauSize);

        int m = Mathf.Max(1, macroSize);
        int mw = Mathf.CeilToInt((float)width / m);
        int md = Mathf.CeilToInt((float)depth / m);

        float ox = (seed % 997) * 0.13f;
        float oz = (seed % 991) * 0.17f;

        for (int mx = 0; mx < mw; mx++)
        {
            for (int mz = 0; mz < md; mz++)
            {
                float cx = mx * m + m * 0.5f;
                float cz = mz * m + m * 0.5f;

                float n = Mathf.PerlinNoise((cx + ox) * scale, (cz + oz) * scale);

                int level = 0;
                for (int i = 0; i < levels; i++)
                {
                    if (n <= thresholds[i]) { level = i; break; }
                    level = i;
                }

                float h = level * stepH;

                int x0 = mx * m;
                int z0 = mz * m;
                int x1 = Mathf.Min(x0 + m, width);
                int z1 = Mathf.Min(z0 + m, depth);
                for (int x = x0; x < x1; x++)
                    for (int z = z0; z < z1; z++)
                        heightMap[x, z] = h;
            }
        }
    }

    /// <summary>
    /// thresholds[i] — верхняя граница шума для уровня i.
    /// hilliness сдвигает массу в сторону высоких уровней.
    /// </summary>
    private static float[] BuildThresholds(int levels, float hilliness)
    {
        var t = new float[levels];
        // базовое распределение: больше низа
        float lowBias = Mathf.Lerp(0.55f, 0.2f, hilliness);
        float remain = 1f;
        float acc = 0f;
        for (int i = 0; i < levels; i++)
        {
            float w;
            if (i == 0)
                w = lowBias;
            else
            {
                // оставшиеся уровни — почти равномерно, чуть сжимая верх при низком hilliness
                float topSqueeze = Mathf.Lerp(1.2f, 0.7f, hilliness);
                float idx = (i) / (float)(levels - 1);
                w = (1f - lowBias) / (levels - 1) * Mathf.Lerp(1f, topSqueeze, idx);
            }
            acc += w;
            t[i] = acc;
            remain -= w;
        }
        // нормализация к 1
        float last = Mathf.Max(t[levels - 1], 1e-4f);
        for (int i = 0; i < levels; i++)
            t[i] = Mathf.Clamp01(t[i] / last);
        t[levels - 1] = 1f;
        return t;
    }
}
