using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Делит карту на клетки и хранит по каждой клетке данные о мире.
///
/// Два слоя данных:
///  - zoneMap (ZoneType)  — зона по высоте: болото / полусухо / сухо.
///  - tileMap (TileType)  — тип клетки: земля / вода / дорога. Используется для игровой логики.
///
/// InitializeZones() вызывается из TerrainManager после построения меша.
/// tileSize берётся из ChunkedTerrainBuilder.
/// </summary>
public class TerrainZoneSystem : MonoBehaviour
{
    // Зона по высоте (для визуальной покраски меша).
    public enum ZoneType
    {
        Swamp,
        SemiDry,
        Dry
    }

    // Тип клетки (для игровой логики: вода, дорога и т.д.).
    public enum TileType
    {
        Ground,
        Water,
        Road
    }

    [System.Serializable]
    public class Zone
    {
        public ZoneType type;
        public string zoneName;
        public Color zoneColor = Color.white;
        public float minHeight;
        public float maxHeight;
    }

    [Header("Источник высот")]
    public HeightMapGenerator heightSource;

    [Header("Меш (tileSize)")]
    public ChunkedTerrainBuilder chunkedBuilder;

    [Header("Зоны по высоте")]
    public List<Zone> zones = new List<Zone>();

    [Header("Вода")]
    [Range(0f, 1f)]
    [Tooltip("Доля карты, затопляемая снизу по высоте. 0.2 = нижние 20% тайлов становятся водой.")]
    public float waterPercent = 0.2f;

    [Tooltip("Размер водных пятен (болот). Меньше = крупнее пятна. Вода группируется, а не размазывается по низинам.")]
    public float waterPatchScale = 8f;

    [Tooltip("Спавнить полупрозрачную плоскость воды на уровне waterLevel (заглушка под графику).")]
    public bool showWaterInGame = true;
    [Tooltip("Материал воды. Если пусто — создаётся простой полупрозрачный URP/Unlit.")]
    public Material waterMaterial;
    public Color waterPlaneColor = new Color(0.15f, 0.35f, 0.8f, 0.55f);

    [Header("Визуализация в редакторе")]
    public bool showGizmos = true;
    public bool showZoneLabels = true;
    public bool showWater = true;
    public Color waterGizmoColor = new Color(0.15f, 0.35f, 0.8f, 0.6f);
    public float gizmoHeight = 0.05f;
    public float gizmoAlpha = 0.5f;

    [Header("Покраска меша")]
    public bool updateColorsOnMesh = true;

    private ZoneType[,] zoneMap;
    private TileType[,] tileMap;
    private float waterLevel;       // высота, ниже/равно которой клетка считается водой
    private GameObject waterPlaneGO; // полупрозрачная плоскость воды
    private bool isInitialized = false;

    // NOTE: не инициализируем зоны в Start.
    // TerrainZoneSystem.Start() может сработать раньше TerrainManager.Start(),
    // и в этот момент карты высот ещё не будет.
    // Поэтому вызываем InitializeZones() из TerrainManager.GenerateAll()
    // после BuildTerrain().

    public void InitializeZones()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("TerrainZoneSystem: нужен инициализированный HeightMapGenerator!");
            return;
        }

        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();

        if (zones.Count == 0)
            SetupDefaultZones();

        int w = heightSource.width;
        int d = heightSource.depth;

        // --- Слой зон по высоте ---
        zoneMap = new ZoneType[w, d];
        for (int x = 0; x < w; x++)
            for (int z = 0; z < d; z++)
                zoneMap[x, z] = GetZoneForHeight(heightSource.GetHeight(x, z));

        // --- Слой типов клеток (вода/земля/дорога) ---
        BuildTileMap(w, d);

        isInitialized = true;

        // Плоскость воды (наглядная заглушка).
        BuildWaterPlane();

        // Покраска вершин меша (Seamless) снята — чанки красятся в ChunkedTerrainBuilder.
        if (updateColorsOnMesh)
            UpdateTerrainColors();

        Debug.Log($"TerrainZoneSystem: создано {zones.Count} зон, карта {w}x{d}, уровень воды = {waterLevel:F3}");
    }

    /// <summary>
    /// Строит tileMap. Количество воды задаёт waterPercent (доля карты),
    /// а waterPatchScale группирует её в пятна (болота), а не размазывает по всем низинам.
    /// Логика: берём пул самых низких клеток (с запасом), внутри пула выбираем
    /// клетки с самым высоким значением шума Перлина — они образуют связные пятна.
    /// </summary>
    private void BuildTileMap(int w, int d)
    {
        tileMap = new TileType[w, d];

        // Вода = всё, что ниже линии воды (y=0). Рельеф заякорен в HeightMapGenerator так,
        // что земля строго выше 0, а дно — ниже. waterPercent/waterPatchScale больше не
        // участвуют (долю под водой задаёт seaLevel в HeightMapGenerator).
        for (int x = 0; x < w; x++)
            for (int z = 0; z < d; z++)
                tileMap[x, z] = heightSource.GetHeight(x, z) < 0f ? TileType.Water : TileType.Ground;

        // Плоскость воды стоит на 0.
        waterLevel = 0f;
    }

    private void SetupDefaultZones()
    {
        zones.Clear();

        // NOTE: пороги завязаны на heightSource.maxHeight — относительные доли.
        float max = heightSource != null ? heightSource.maxHeight : 1f;

        zones.Add(new Zone
        {
            type = ZoneType.Swamp,
            zoneName = "Болото",
            zoneColor = new Color(0.3f, 0.2f, 0.1f),
            minHeight = 0f,
            maxHeight = max * 0.25f
        });

        zones.Add(new Zone
        {
            type = ZoneType.SemiDry,
            zoneName = "Полусухо",
            zoneColor = new Color(0.5f, 0.4f, 0.25f),
            minHeight = max * 0.25f,
            maxHeight = max * 0.6f
        });

        zones.Add(new Zone
        {
            type = ZoneType.Dry,
            zoneName = "Сухо",
            zoneColor = new Color(0.7f, 0.6f, 0.4f),
            minHeight = max * 0.6f,
            maxHeight = max
        });
    }

    private ZoneType GetZoneForHeight(float height)
    {
        foreach (Zone zone in zones)
        {
            if (height >= zone.minHeight && height <= zone.maxHeight)
                return zone.type;
        }
        return ZoneType.SemiDry;
    }

    /// <summary>
    /// Раньше красила единый Seamless-меш по ZoneType. Для Chunked — no-op
    /// (цвет чанков задаёт ChunkedTerrainBuilder по высоте).
    /// </summary>
    public void UpdateTerrainColors()
    {
        // Seamless path removed. Chunked uses its own lowColor/highColor gradient.
    }

    // =================== Резолверы геометрии ===================

    private float ResolveTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 1f;
    }

    private Vector3 ResolveMapOrigin()
    {
        float ts = ResolveTileSize();
        return new Vector3(
            -heightSource.width * ts / 2f,
            0,
            -heightSource.depth * ts / 2f
        );
    }

    // =================== Доступ к зонам ===================

    public ZoneType GetZoneAtWorldPosition(Vector3 worldPos)
    {
        if (!isInitialized || heightSource == null)
            return ZoneType.SemiDry;

        float ts = ResolveTileSize();
        Vector3 mapOrigin = ResolveMapOrigin();

        int cellX = Mathf.Clamp(Mathf.FloorToInt((worldPos.x - mapOrigin.x) / ts + 0.5f), 0, heightSource.width - 1);
        int cellZ = Mathf.Clamp(Mathf.FloorToInt((worldPos.z - mapOrigin.z) / ts + 0.5f), 0, heightSource.depth - 1);

        return GetZoneAtCell(cellX, cellZ);
    }

    public ZoneType GetZoneAtCell(int x, int z)
    {
        if (!isInitialized || zoneMap == null) return ZoneType.SemiDry;

        if (x >= 0 && x < zoneMap.GetLength(0) && z >= 0 && z < zoneMap.GetLength(1))
            return zoneMap[x, z];

        return ZoneType.SemiDry;
    }

    public Color GetColorForZone(ZoneType zone)
    {
        foreach (Zone z in zones)
            if (z.type == zone) return z.zoneColor;

        return Color.gray;
    }

    public List<Vector2Int> GetZoneCells(ZoneType zone)
    {
        var cells = new List<Vector2Int>();
        if (!isInitialized || zoneMap == null) return cells;

        for (int x = 0; x < zoneMap.GetLength(0); x++)
            for (int z = 0; z < zoneMap.GetLength(1); z++)
                if (zoneMap[x, z] == zone)
                    cells.Add(new Vector2Int(x, z));

        return cells;
    }

    // =================== Доступ к типам клеток (вода/дорога) ===================

    public TileType GetTileType(int x, int z)
    {
        if (!isInitialized || tileMap == null) return TileType.Ground;

        if (x >= 0 && x < tileMap.GetLength(0) && z >= 0 && z < tileMap.GetLength(1))
            return tileMap[x, z];

        return TileType.Ground;
    }

    public TileType GetTileTypeAtWorldPosition(Vector3 worldPos)
    {
        if (!isInitialized || heightSource == null) return TileType.Ground;

        float ts = ResolveTileSize();
        Vector3 mapOrigin = ResolveMapOrigin();

        int cellX = Mathf.Clamp(Mathf.FloorToInt((worldPos.x - mapOrigin.x) / ts + 0.5f), 0, heightSource.width - 1);
        int cellZ = Mathf.Clamp(Mathf.FloorToInt((worldPos.z - mapOrigin.z) / ts + 0.5f), 0, heightSource.depth - 1);

        return GetTileType(cellX, cellZ);
    }

    public bool IsWaterAtCell(int x, int z) => GetTileType(x, z) == TileType.Water;

    public bool IsWaterAtWorldPosition(Vector3 worldPos)
        => GetTileTypeAtWorldPosition(worldPos) == TileType.Water;

    /// <summary>Задел под фазу C: пометить клетку (например дорогой).</summary>
    public void SetTile(int x, int z, TileType type)
    {
        if (tileMap == null) return;
        if (x >= 0 && x < tileMap.GetLength(0) && z >= 0 && z < tileMap.GetLength(1))
            tileMap[x, z] = type;
    }

    public List<Vector2Int> GetTileCells(TileType type)
    {
        var cells = new List<Vector2Int>();
        if (!isInitialized || tileMap == null) return cells;

        for (int x = 0; x < tileMap.GetLength(0); x++)
            for (int z = 0; z < tileMap.GetLength(1); z++)
                if (tileMap[x, z] == type)
                    cells.Add(new Vector2Int(x, z));

        return cells;
    }

    public float WaterLevel => waterLevel;
    public TileType[,] GetTileMap() => tileMap;

    // =================== Плоскость воды ===================

    /// <summary>
    /// Создаёт одну плоскую полупрозрачную плоскость на уровне waterLevel,
    /// размером со всю карту. Тайлы ниже уровня визуально оказываются под водой.
    /// </summary>
    private void BuildWaterPlane()
    {
        DestroyWaterPlane();

        if (!showWaterInGame) return;

        float ts = ResolveTileSize();
        float sizeX = heightSource.width * ts;
        float sizeZ = heightSource.depth * ts;

        waterPlaneGO = GameObject.CreatePrimitive(PrimitiveType.Plane);
        waterPlaneGO.name = "WaterPlane";
        waterPlaneGO.transform.SetParent(transform);

        // Вода жёстко на y=0. Рельеф заякорен (AnchorToSeaLevel) так, что линия воды
        // попадает в зазор между уровнями террас — на 0 нет верхов клеток, совпадать не с чем,
        // z-fighting исключён на любом сиде. Земля > 0, дно < 0.
        waterPlaneGO.transform.position = new Vector3(0f, 0f, 0f);
        waterPlaneGO.transform.localScale = new Vector3(sizeX / 10f, 1f, sizeZ / 10f);

        // Коллайдер воде не нужен — мешает рейкастам и движению.
        var col = waterPlaneGO.GetComponent<Collider>();
        if (col != null)
        {
            if (Application.isPlaying) Destroy(col);
            else DestroyImmediate(col);
        }

        var mr = waterPlaneGO.GetComponent<MeshRenderer>();
        mr.sharedMaterial = waterMaterial != null ? waterMaterial : CreateDefaultWaterMaterial();
    }

    /// <summary>
    /// Простой полупрозрачный материал URP/Unlit (Standard в URP даёт розовый).
    /// </summary>
    private Material CreateDefaultWaterMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            Debug.LogWarning("TerrainZoneSystem: не найден шейдер URP/Unlit — проверь, что проект на URP.");
            shader = Shader.Find("Sprites/Default"); // запасной прозрачный вариант
        }

        var mat = new Material(shader);
        // Настройка прозрачности для URP/Unlit.
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // 1 = Transparent
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);     // 0 = Alpha
        if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_SURFACE_TYPE_OPAQUE");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", waterPlaneColor);
        else mat.color = waterPlaneColor;

        return mat;
    }

    private void DestroyWaterPlane()
    {
        if (waterPlaneGO == null) return;
        if (Application.isPlaying) Destroy(waterPlaneGO);
        else DestroyImmediate(waterPlaneGO);
        waterPlaneGO = null;
    }

    // =================== Прочее ===================

    public void RegenerateZones() => InitializeZones();

    public void ClearZones()
    {
        zoneMap = null;
        tileMap = null;
        DestroyWaterPlane();
        isInitialized = false;
    }

    public ZoneType[,] GetZoneMap() => zoneMap;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!showGizmos || !Application.isPlaying) return;
        if (!isInitialized || zoneMap == null || heightSource == null) return;

        float ts = ResolveTileSize();
        Vector3 mapOrigin = ResolveMapOrigin();

        int w = zoneMap.GetLength(0);
        int d = zoneMap.GetLength(1);

        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < d; z++)
            {
                // Вода рисуется поверх зоны отдельным цветом — видно затопленные клетки.
                bool isWater = showWater && tileMap != null && tileMap[x, z] == TileType.Water;

                Color cellColor;
                if (isWater)
                {
                    cellColor = waterGizmoColor;
                }
                else
                {
                    cellColor = GetColorForZone(zoneMap[x, z]);
                    cellColor.a = gizmoAlpha;
                }

                float height = heightSource.GetHeight(x, z);
                Vector3 center = mapOrigin + new Vector3(
                    x * ts,
                    height + gizmoHeight,
                    z * ts
                );

                Gizmos.color = cellColor;
                Gizmos.DrawCube(center, new Vector3(ts * 0.95f, 0.05f, ts * 0.95f));

                cellColor.a = 0.8f;
                Gizmos.color = cellColor;
                Gizmos.DrawWireCube(center, new Vector3(ts * 0.95f, 0.05f, ts * 0.95f));
            }
        }
    }
#endif
}
