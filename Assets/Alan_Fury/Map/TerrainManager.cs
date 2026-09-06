using UnityEngine;

/// <summary>
/// Координирует весь процесс генерации ландшафта.
/// Меш: ChunkedTerrainBuilder.
/// </summary>
public class TerrainManager : MonoBehaviour
{
    [Header("Источник высот")]
    public HeightMapGenerator heightGenerator;

    [Header("Меш")]
    public ChunkedTerrainBuilder chunkedTerrainBuilder;

    [Header("Объекты")]
    public ObjectPlacer objectPlacer;                   // GameObject'ы, нужны коллайдеры
    public SpriteVegetationPlacer vegetationPlacer;    // legacy спрайтовая растительность
    public NaturePlacement naturePlacement;            // единая система деревья + растительность
    public NatureRenderer natureRenderer;              // отрисовка + live

    [Header("Сетка занятости")]
    public MapGrid mapGrid;                             // единая occupancy-сетка (base/sector/region)

    [Header("Опциональные системы")]
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

        // 1.5 Сетка занятости (пустая, writers заполнят позже)
        if (mapGrid != null)
        {
            mapGrid.Build();
            LogStep("MapGrid", ref sw);
        }

        // 2. Меш
        if (chunkedTerrainBuilder != null)
        {
            chunkedTerrainBuilder.BuildTerrain();
            LogStep("Чанки меша", ref sw);
        }

        // 3. Дорога (до объектов — чтобы деревья её обходили)
        if (roadGenerator != null)
        {
            roadGenerator.GenerateRoad();
            LogStep("Дорога", ref sw);
        }

        // 3.5 Дорога сгладила карту высот → пересобрать меш
        if (roadGenerator != null && roadGenerator.flattenAlongRoad && chunkedTerrainBuilder != null)
        {
            chunkedTerrainBuilder.BuildTerrain();
            LogStep("Пересборка меша после дороги", ref sw);
        }

        // 4. Объекты
        if (objectPlacer != null)
        {
            objectPlacer.PlaceAllObjects();
            LogStep("Объекты", ref sw);
        }

        // 4.1 Nature (стриминг — только Init, генерация по мере движения игрока)
        if (naturePlacement != null)
        {
            naturePlacement.Init();
            LogStep("NaturePlacement Init", ref sw);
        }

        // 4.2 Legacy спрайтовая растительность
        if (vegetationPlacer != null)
        {
            vegetationPlacer.PlaceAll();
            LogStep("Растительность (legacy)", ref sw);
        }

        // 4.5 Сетка проходимости для AI + пересчёт границы карты.
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
        if (objectPlacer != null) objectPlacer.ClearOldObjects();
        if (naturePlacement != null) naturePlacement.UnloadAll();
        if (natureRenderer != null) natureRenderer.ClearLive();
        if (vegetationPlacer != null) vegetationPlacer.ClearAll();
        if (mapGrid != null) mapGrid.Clear();
        if (heightGenerator != null) heightGenerator.Clear();
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
        if (chunkedTerrainBuilder == null) chunkedTerrainBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (objectPlacer == null) objectPlacer = GetComponent<ObjectPlacer>();
        if (naturePlacement == null) naturePlacement = GetComponent<NaturePlacement>();
        if (natureRenderer == null) natureRenderer = GetComponent<NatureRenderer>();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();
        if (roadGenerator == null) roadGenerator = GetComponent<RoadGenerator>();
        if (fogCurtain == null) fogCurtain = GetComponent<MapFogCurtain>();
        if (fogPlacer == null) fogPlacer = GetComponent<FogPlacer>();
        if (pathfinder == null) pathfinder = GetComponent<Pathfinder>();
        if (mapBoundary == null) mapBoundary = GetComponent<MapBoundary>();

        if (mapGrid != null)
        {
            if (mapGrid.heightSource == null) mapGrid.heightSource = heightGenerator;
            if (mapGrid.chunkedBuilder == null) mapGrid.chunkedBuilder = chunkedTerrainBuilder;
        }
        if (roadGenerator != null && roadGenerator.mapGrid == null)
            roadGenerator.mapGrid = mapGrid;
        if (objectPlacer != null && objectPlacer.mapGrid == null)
            objectPlacer.mapGrid = mapGrid;
        if (pathfinder != null && pathfinder.mapGrid == null)
            pathfinder.mapGrid = mapGrid;
    }

    private bool ValidateComponents()
    {
        if (heightGenerator == null)
        {
            Debug.LogError("TerrainManager: HeightMapGenerator не найден!");
            return false;
        }
        if (chunkedTerrainBuilder == null)
        {
            Debug.LogError("TerrainManager: нужен ChunkedTerrainBuilder!");
            return false;
        }
        return true;
    }
}
