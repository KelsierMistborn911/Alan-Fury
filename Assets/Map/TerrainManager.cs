using UnityEngine;

/// <summary>
/// Координирует весь процесс генерации ландшафта.
/// Поддерживает два режима:
/// - SeamlessTerrainBuilder (один меш, маленькие карты)
/// - ChunkedTerrainBuilder (чанки, большие карты) + InstancedObjectPlacer
/// </summary>
public class TerrainManager : MonoBehaviour
{
    [Header("Источник высот")]
    public HeightMapGenerator heightGenerator;

    [Header("Меш — выберите один из двух")]
    public SeamlessTerrainBuilder terrainBuilder;       // маленькие карты
    public ChunkedTerrainBuilder chunkedTerrainBuilder; // большие карты (рекомендуется)

    [Header("Объекты — выберите один из двух")]
    public ObjectPlacer objectPlacer;                   // GameObject'ы, нужны коллайдеры
    public InstancedObjectPlacer instancedObjectPlacer; // GPU instancing, без коллайдеров, быстро
    public SpriteVegetationPlacer vegetationPlacer;    // спрайтовая трава/кусты/грибы (может висеть на другом объекте — назначить вручную)

    [Header("Опциональные системы")]
    public TerrainZoneSystem zoneSystem;
    public RoadGenerator roadGenerator;                 // процедурная дорога
    public MapFogCurtain fogCurtain;                    // туман-занавес по краям карты
    public FogPlacer fogPlacer;                         // очаги тумана на карте
    public Pathfinder pathfinder;                       // сетка проходимости для AI (строится после деревьев)
    public MapBoundary mapBoundary;                     // мягкая граница карты (пересчёт после генерации)

    [Header("Настройки запуска")]
    public bool generateOnStart = true;
    public bool clearBeforeGenerate = true;
    public bool logTimings = true;

    void OnEnable()
    {
        if (heightGenerator != null)
        {
            heightGenerator.onHeightMapReady += OnHeightMapReady;
            heightGenerator.onHeightMapGenerated += OnHeightMapGenerated;
        }
    }

    void OnDisable()
    {
        if (heightGenerator != null)
        {
            heightGenerator.onHeightMapReady -= OnHeightMapReady;
            heightGenerator.onHeightMapGenerated -= OnHeightMapGenerated;
        }
    }

    void Start()
    {
        if (generateOnStart) GenerateAll();
    }

    private void OnHeightMapReady()
        => Debug.Log("=== Событие: Карта высот готова! ===");

    private void OnHeightMapGenerated(float[,] heightMap)
        => Debug.Log($"=== Карта высот: {heightMap.GetLength(0)}×{heightMap.GetLength(1)} ===");

    [ContextMenu("Generate All")]
    public void GenerateAll()
    {
        AutoAssignComponents();
        if (!ValidateComponents()) return;

        Debug.Log("=== Начинаем генерацию ===");
        if (clearBeforeGenerate) ClearAll();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Карта высот
        heightGenerator.Generate();
        LogStep("Карта высот", ref sw);

        // 2. Меш
        if (chunkedTerrainBuilder != null)
        {
            chunkedTerrainBuilder.BuildTerrain();
            LogStep("Чанки меша", ref sw);
        }
        else if (terrainBuilder != null)
        {
            terrainBuilder.BuildTerrain();
            LogStep("Меш", ref sw);
        }

        // 3. Зоны
        if (zoneSystem != null)
        {
            zoneSystem.InitializeZones();
            LogStep("Зоны", ref sw);
        }

        // 3.5 Дорога (после зон — нужна вода; до объектов — чтобы деревья её обходили)
        if (roadGenerator != null)
        {
            roadGenerator.GenerateRoad();
            LogStep("Дорога", ref sw);
        }

        // 3.6 Дорога сгладила карту высот → пересобрать меш и перекрасить зоны,
        //     чтобы выровненная поверхность под дорогой отобразилась (вариант A).
        if (roadGenerator != null && roadGenerator.flattenAlongRoad)
        {
            if (chunkedTerrainBuilder != null) chunkedTerrainBuilder.BuildTerrain();
            else if (terrainBuilder != null) terrainBuilder.BuildTerrain();
            if (zoneSystem != null) zoneSystem.UpdateTerrainColors();
            LogStep("Пересборка меша после дороги", ref sw);
        }

        // 4. Объекты
        if (instancedObjectPlacer != null)
        {
            instancedObjectPlacer.PlaceAllObjects();
            LogStep("Instanced объекты", ref sw);
        }
        else if (objectPlacer != null)
        {
            objectPlacer.PlaceAllObjects();
            LogStep("Объекты", ref sw);
        }

        // 4.2 Спрайтовая растительность
        if (vegetationPlacer != null)
        {
            vegetationPlacer.PlaceAll();
            LogStep("Растительность", ref sw);
        }

        // 4.5 Сетка проходимости для AI (после деревьев — они стены) + пересчёт границы карты.
        if (pathfinder != null)
        {
            pathfinder.Build();
            LogStep("Сетка путей", ref sw);
        }
        if (mapBoundary != null)
        {
            mapBoundary.Recompute();
            LogStep("Граница карты", ref sw);
        }

        // 5. Туман по краям
        if (fogCurtain != null)
        {
            fogCurtain.BuildCurtain();
            LogStep("Туман-занавес", ref sw);
        }

        // 6. Очаги тумана на карте
        if (fogPlacer != null)
        {
            fogPlacer.PlaceFog();
            LogStep("Очаги тумана", ref sw);
        }

        Debug.Log("=== Генерация завершена! ===");
    }

    [ContextMenu("Clear All")]
    public void ClearAll()
    {
        if (chunkedTerrainBuilder != null) chunkedTerrainBuilder.ClearTerrain();
        if (terrainBuilder != null) terrainBuilder.ClearTerrain();
        if (objectPlacer != null) objectPlacer.ClearOldObjects();
        if (instancedObjectPlacer != null) instancedObjectPlacer.ClearObjects();
        if (vegetationPlacer != null) vegetationPlacer.ClearAll();
        if (heightGenerator != null) heightGenerator.Clear();
        if (zoneSystem != null) zoneSystem.ClearZones();
        if (roadGenerator != null) roadGenerator.ClearRoad();
        if (fogCurtain != null) fogCurtain.ClearCurtain();
        if (fogPlacer != null) fogPlacer.ClearFog();
    }

    private void LogStep(string name, ref System.Diagnostics.Stopwatch sw)
    {
        if (!logTimings) return;
        sw.Stop();
        Debug.Log($"   {name}: {sw.ElapsedMilliseconds}ms");
        sw.Restart();
    }

    private void AutoAssignComponents()
    {
        if (heightGenerator == null) heightGenerator = GetComponent<HeightMapGenerator>();
        if (terrainBuilder == null) terrainBuilder = GetComponent<SeamlessTerrainBuilder>();
        if (chunkedTerrainBuilder == null) chunkedTerrainBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (objectPlacer == null) objectPlacer = GetComponent<ObjectPlacer>();
        if (instancedObjectPlacer == null) instancedObjectPlacer = GetComponent<InstancedObjectPlacer>();
        if (zoneSystem == null) zoneSystem = GetComponent<TerrainZoneSystem>();
        if (roadGenerator == null) roadGenerator = GetComponent<RoadGenerator>();
        if (fogCurtain == null) fogCurtain = GetComponent<MapFogCurtain>();
        if (fogPlacer == null) fogPlacer = GetComponent<FogPlacer>();
        if (pathfinder == null) pathfinder = GetComponent<Pathfinder>();
        if (mapBoundary == null) mapBoundary = GetComponent<MapBoundary>();
    }

    private bool ValidateComponents()
    {
        if (heightGenerator == null)
        {
            Debug.LogError("TerrainManager: HeightMapGenerator не найден!");
            return false;
        }
        if (chunkedTerrainBuilder == null && terrainBuilder == null)
        {
            Debug.LogError("TerrainManager: нужен хотя бы один из TerrainBuilder!");
            return false;
        }
        return true;
    }
}
