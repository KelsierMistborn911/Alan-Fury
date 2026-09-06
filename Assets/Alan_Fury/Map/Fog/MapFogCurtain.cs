using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Туман-занавес по периметру карты — расстановка префаба тумана цепочкой
/// вдоль края. Использует ТОТ ЖЕ префаб, что и очаги на карте (FogPlacer),
/// поэтому край и очаги выглядят одинаково и настраиваются в одном месте — префабе.
///
/// Точки считаются от размеров карты (HeightMapGenerator + tileSize), поэтому
/// при изменении размера карты занавес сам перестраивается под новый периметр.
/// Вызывать BuildCurtain() из TerrainManager после построения меша.
///
/// Равномерность: шаг считается через Ceil (фактический шаг ≤ spacing),
/// разброс — только ВДОЛЬ края и заперт внутри слота (±step * jitterFraction),
/// поэтому соседи не могут ни слипнуться, ни разойтись больше чем на 1.5 шага.
/// </summary>
public class MapFogCurtain : MonoBehaviour
{
    [Header("Источники размеров")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;     // приоритетный источник tileSize
    public SeamlessTerrainBuilder seamlessBuilder;   // запасной

    [Header("Префаб тумана (тот же, что на карте)")]
    public GameObject fogPrefab;
    public Transform parent;

    [Header("Расстановка")]
    [Tooltip("Максимальный шаг между очагами вдоль края (м). Фактический шаг не больше этого.")]
    public float spacing = 18f;
    [Tooltip("Смещение пояса: + внутрь карты, − наружу за край.")]
    public float inset = -6f;
    [Tooltip("Подъём над поверхностью (м).")]
    public float heightOffset = 0f;
    [Tooltip("Случайный разброс ВДОЛЬ края, доля шага (0..0.4). " +
             "Соседи не могут слипнуться или разойтись больше чем на 1.5 шага.")]
    [Range(0f, 0.4f)] public float jitterFraction = 0.25f;

    private readonly List<GameObject> _placed = new List<GameObject>();

    public void BuildCurtain()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("MapFogCurtain: карта высот не готова!");
            return;
        }
        if (fogPrefab == null)
        {
            Debug.LogError("MapFogCurtain: не задан fogPrefab!");
            return;
        }

        ClearCurtain();
        EnsureParent();

        float ts = GetTileSize();
        float halfW = heightSource.width * ts / 2f;
        float halfD = heightSource.depth * ts / 2f;

        // Прямоугольный контур пояса
        float cX = halfW - inset;
        float cZ = halfD - inset;

        // Точки вдоль 4 сторон: Ceil → фактический шаг никогда не больше spacing
        int countX = Mathf.Max(1, Mathf.CeilToInt((cX * 2f) / Mathf.Max(0.1f, spacing)));
        int countZ = Mathf.Max(1, Mathf.CeilToInt((cZ * 2f) / Mathf.Max(0.1f, spacing)));
        float stepX = (cX * 2f) / countX;   // фактический шаг на стороне
        float stepZ = (cZ * 2f) / countZ;

        // Стороны Z- и Z+ (тянутся вдоль X)
        for (int i = 0; i <= countX; i++)
        {
            float x = -cX + i * stepX;
            SpawnAt(x, -cZ, ts, alongX: true, step: stepX);
            SpawnAt(x, cZ, ts, alongX: true, step: stepX);
        }
        // Стороны X- и X+ (тянутся вдоль Z), углы пропускаем (i от 1 до count-1)
        for (int i = 1; i < countZ; i++)
        {
            float z = -cZ + i * stepZ;
            SpawnAt(-cX, z, ts, alongX: false, step: stepZ);
            SpawnAt(cX, z, ts, alongX: false, step: stepZ);
        }

        Debug.Log($"MapFogCurtain: цепочка из {_placed.Count} очагов по периметру {cX * 2f:F0}×{cZ * 2f:F0} м, " +
                  $"шаг X={stepX:F1}, Z={stepZ:F1}");
    }

    private void SpawnAt(float x, float z, float ts, bool alongX, float step)
    {
        Vector3 origin = new Vector3(-heightSource.width * ts / 2f, 0, -heightSource.depth * ts / 2f);

        // Разброс только вдоль стороны, в пределах своего слота —
        // поперёк края не дёргаем, чтобы линия не рассыпалась.
        if (jitterFraction > 0f)
        {
            float j = Random.Range(-1f, 1f) * step * jitterFraction;
            if (alongX) x += j; else z += j;
        }

        Vector3 pos = new Vector3(x, 0f, z);
        float y = heightSource.GetHeightAtWorldPos(pos, ts, origin);
        pos.y = y + heightOffset;

        var go = InstantiatePrefab(fogPrefab, pos);
        go.name = $"EdgeFog_{_placed.Count}";
        _placed.Add(go);
    }

    public void ClearCurtain()
    {
        EnsureParent();
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var c = parent.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(c);
            else DestroyImmediate(c);
        }
        _placed.Clear();
    }

    private void EnsureParent()
    {
        if (parent != null) return;
        var existing = transform.Find("FogCurtain");
        parent = existing != null ? existing : new GameObject("FogCurtain").transform;
        parent.SetParent(transform);
    }

    private GameObject InstantiatePrefab(GameObject prefab, Vector3 pos)
    {
        if (Application.isPlaying)
            return Instantiate(prefab, pos, Quaternion.identity, parent);
#if UNITY_EDITOR
        var go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
        go.transform.position = pos;
        return go;
#else
        return Instantiate(prefab, pos, Quaternion.identity, parent);
#endif
    }

    private float GetTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.TileSize;
        if (seamlessBuilder != null) return seamlessBuilder.tileSize;
        return 4f;
    }

    void OnDestroy() => ClearCurtain();
}
