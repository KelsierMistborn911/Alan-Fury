using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ����������� ������� �� ���������� ��������������� ���������.
/// ���������� HeightMapGenerator ��� ��������� ����� � ������ ���.
/// </summary>
public class ObjectPlacer : MonoBehaviour
{
    [System.Serializable]
    public class ObjectType
    {
        public string name;
        public GameObject prefab;
        public int count = 50;
        public float minHeight = 0f;
        public float maxHeight = 1f;
        public bool randomRotation = true;
        public float minScale = 0.8f;
        public float maxScale = 1.2f;
        public bool alignToSurface = false;
        public bool snapToCellCenter = false; // ставить по клеткам (деревья), через MapGrid
        [Tooltip("Размер занятости в клетках (дерево 3×3 → footprint=3).")]
        public int footprint = 1;
        public float heightOffset = 0f;
        [Range(0f, 1f)]
        public float spawnChance = 1f;
        public float minDistanceBetweenObjects = 1f;
        public LayerMask objectLayer;
        [Tooltip("Флаг MapGrid при Occupy (по умолчанию Tree).")]
        public MapGrid.OccupancyFlags occupyFlag = MapGrid.OccupancyFlags.Tree;
    }

    [Header("Источник высот")]
    public HeightMapGenerator heightSource;

    [Header("Меш / сетка")]
    public ChunkedTerrainBuilder chunkedBuilder;
    public MapGrid mapGrid;

    [Header("���� ��������")]
    public List<ObjectType> objectTypes = new List<ObjectType>();

    [Header("�������� ��� ��������")]
    public Transform objectsParent;

    [Header("��������� ����������")]
    public int maxAttemptsPerObject = 30;

    [Header("������������������")]
    public bool useSpacialGrid = true;
    public float gridCellSize = 10f;

    // ���������� ������
    private float[,] heights;
    private int width, depth;
    private float tileSize;
    private Vector3 mapOrigin;
    private List<Vector3> placedPositions = new List<Vector3>();
    private Dictionary<Vector2Int, List<Vector3>> spatialGrid;

    /// <summary>
    /// ��������� ����������� ���� ��������.
    /// </summary>
    public void PlaceAllObjects()
    {
        if (!ValidateInputs())
            return;

        FetchData();
        ClearOldObjects();

        if (useSpacialGrid)
            spatialGrid = new Dictionary<Vector2Int, List<Vector3>>();

        foreach (ObjectType objType in objectTypes)
        {
            if (objType.snapToCellCenter)
                PlaceObjectsOnCells(objType);
            else
                PlaceObjectsOfType(objType);
        }
    }

    /// <summary>
    /// ������� ��� ����� ����������� �������.
    /// </summary>
    public void ClearOldObjects()
    {
        if (objectsParent == null)
        {
            objectsParent = new GameObject("PlacedObjects").transform;
            objectsParent.SetParent(transform);
        }

        for (int i = objectsParent.childCount - 1; i >= 0; i--)
        {
            Transform child = objectsParent.GetChild(i);
            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }

        placedPositions.Clear();
        if (spatialGrid != null)
            spatialGrid.Clear();
    }

    // ==================== ��������� ������ ====================

    private bool ValidateInputs()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("ObjectPlacer: ����� ������������������ HeightMapGenerator!");
            return false;
        }
        if (chunkedBuilder == null)
        {
            Debug.LogError("ObjectPlacer: нужен ChunkedTerrainBuilder!");
            return false;
        }
        if (objectTypes.Count == 0)
        {
            Debug.LogWarning("ObjectPlacer: ��� ����� �������� ��� �����������.");
            return false;
        }
        return true;
    }

    private void FetchData()
    {
        heights = heightSource.heightMap;
        width = heights.GetLength(0);
        depth = heights.GetLength(1);
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();
        tileSize = ResolveTileSize();
        mapOrigin = new Vector3(-width * tileSize / 2f, 0, -depth * tileSize / 2f);
    }

    private void AddToSpatialGrid(Vector3 position)
    {
        if (!useSpacialGrid || spatialGrid == null) return;

        Vector2Int cell = new Vector2Int(
            Mathf.FloorToInt(position.x / gridCellSize),
            Mathf.FloorToInt(position.z / gridCellSize)
        );

        if (!spatialGrid.ContainsKey(cell))
            spatialGrid[cell] = new List<Vector3>();

        spatialGrid[cell].Add(position);
    }

    private bool IsTooClose(Vector3 position, float minDistance)
    {
        if (minDistance <= 0) return false;

        return useSpacialGrid && spatialGrid != null
            ? IsTooCloseGrid(position, minDistance)
            : IsTooCloseBruteForce(position, minDistance);
    }

    private bool IsTooCloseGrid(Vector3 position, float minDistance)
    {
        Vector2Int cell = new Vector2Int(
            Mathf.FloorToInt(position.x / gridCellSize),
            Mathf.FloorToInt(position.z / gridCellSize)
        );

        float minDistSqr = minDistance * minDistance;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                Vector2Int neighbor = new Vector2Int(cell.x + dx, cell.y + dz);
                if (spatialGrid.TryGetValue(neighbor, out var positions))
                {
                    foreach (var pos in positions)
                    {
                        if ((position - pos).sqrMagnitude < minDistSqr)
                            return true;
                    }
                }
            }
        }
        return false;
    }

    private bool IsTooCloseBruteForce(Vector3 position, float minDistance)
    {
        float minDistSqr = minDistance * minDistance;
        foreach (var pos in placedPositions)
        {
            if ((position - pos).sqrMagnitude < minDistSqr)
                return true;
        }
        return false;
    }

    private void PlaceObjectsOfType(ObjectType objType)
    {
        if (objType.prefab == null)
        {
            Debug.LogWarning($"ObjectPlacer: � ���� '{objType.name}' ��� �������, ����������.");
            return;
        }

        // ������ �� ����������� margin ����� ����� ������� ���������
        float margin = tileSize * 2;
        float rangeX = width * tileSize - margin * 2;
        float rangeZ = depth * tileSize - margin * 2;
        if (rangeX <= 0 || rangeZ <= 0)
        {
            Debug.LogWarning($"ObjectPlacer: ����� ������� ���� ��� ��������, margin ������� � 0 ��� '{objType.name}'.");
            margin = 0f;
            rangeX = width * tileSize;
            rangeZ = depth * tileSize;
        }

        int placed = 0;
        int attempts = 0;
        int maxTotalAttempts = objType.count * maxAttemptsPerObject;

        while (placed < objType.count && attempts < maxTotalAttempts)
        {
            attempts++;

            // FIX: spawnChance ����������� ��� ������� ������� ��������
            if (Random.value > objType.spawnChance)
                continue;

            float rx = Random.Range(margin, margin + rangeX);
            float rz = Random.Range(margin, margin + rangeZ);
            Vector3 worldPos = mapOrigin + new Vector3(rx, 0, rz);

            float h = heightSource.GetHeightAtWorldPos(worldPos, tileSize, mapOrigin);

            if (h < objType.minHeight || h > objType.maxHeight)
                continue;

            worldPos.y = h + objType.heightOffset;

            // FIX: ���������� minDistanceBetweenObjects �� ������ ���� �������
            if (IsTooClose(worldPos, objType.minDistanceBetweenObjects))
                continue;

            Quaternion rotation = objType.randomRotation
                ? Quaternion.Euler(0, Random.Range(0f, 360f), 0)
                : Quaternion.identity;

            if (objType.alignToSurface && !objType.randomRotation)
                rotation = GetSurfaceRotation(worldPos);

            GameObject obj = InstantiatePrefab(objType.prefab, worldPos, rotation);
            obj.name = $"{objType.name}_{placed}";
            float scale = Random.Range(objType.minScale, objType.maxScale);
            obj.transform.localScale = Vector3.one * scale;

            ApplyLayer(obj, objType);

            placedPositions.Add(worldPos);
            AddToSpatialGrid(worldPos);
            placed++;
        }

        Debug.Log($"ObjectPlacer: ��������� {placed}/{objType.count} �������� '{objType.name}' �� {attempts} �������.");
    }

    private float ResolveTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 1f;
    }

    /// <summary>
    /// Размещение по клеткам через MapGrid: CanPlace + Occupy (footprint).
    /// Не ставит на Tree (blockMask) и на Road.
    /// </summary>
    private void PlaceObjectsOnCells(ObjectType objType)
    {
        if (objType.prefab == null)
        {
            Debug.LogWarning($"ObjectPlacer: у типа '{objType.name}' нет префаба, пропуск.");
            return;
        }

        int fp = Mathf.Max(1, objType.footprint);
        int half = fp / 2;
        const int cellMargin = 2;
        int margin = cellMargin + half;
        int marginX = width > margin * 2 + 1 ? margin : 0;
        int marginZ = depth > margin * 2 + 1 ? margin : 0;

        bool useGrid = mapGrid != null && mapGrid.IsReady;
        var flag = objType.occupyFlag != MapGrid.OccupancyFlags.None
            ? objType.occupyFlag
            : MapGrid.OccupancyFlags.Tree;

        int placed = 0;
        int attempts = 0;
        int maxTotalAttempts = objType.count * maxAttemptsPerObject;

        while (placed < objType.count && attempts < maxTotalAttempts)
        {
            attempts++;
            if (Random.value > objType.spawnChance) continue;

            int cx = Random.Range(marginX, width - marginX);
            int cz = Random.Range(marginZ, depth - marginZ);

            if (useGrid)
            {
                if (!mapGrid.CanPlace(cx, cz, fp, fp, anchorCenter: true)) continue;
                if (mapGrid.HasFlag(cx, cz, MapGrid.OccupancyFlags.Road)) continue;
                // footprint пересечение с Road
                if (FootprintHitsRoad(cx, cz, fp)) continue;
            }

            float h = heightSource.GetHeight(cx, cz);
            if (h < objType.minHeight || h > objType.maxHeight) continue;

            Vector3 worldPos = mapOrigin + new Vector3(cx * tileSize, 0f, cz * tileSize);
            worldPos.y = h + objType.heightOffset;

            if (IsTooClose(worldPos, objType.minDistanceBetweenObjects)) continue;

            Quaternion rotation = objType.randomRotation
                ? Quaternion.Euler(0, Random.Range(0f, 360f), 0)
                : Quaternion.identity;
            if (objType.alignToSurface && !objType.randomRotation)
                rotation = GetSurfaceRotation(worldPos);

            GameObject obj = InstantiatePrefab(objType.prefab, worldPos, rotation);
            obj.name = $"{objType.name}_{placed}";
            float scale = Random.Range(objType.minScale, objType.maxScale);
            obj.transform.localScale = Vector3.one * scale;
            ApplyLayer(obj, objType);

            placedPositions.Add(worldPos);
            AddToSpatialGrid(worldPos);
            if (useGrid)
                mapGrid.Occupy(cx, cz, fp, fp, flag, anchorCenter: true);
            placed++;
        }

        Debug.Log($"ObjectPlacer: по клеткам {placed}/{objType.count} '{objType.name}' (fp={fp}) за {attempts} попыток.");
    }

    private bool FootprintHitsRoad(int cx, int cz, int fp)
    {
        int hx = fp / 2;
        int x0 = cx - hx, z0 = cz - hx;
        int x1 = x0 + fp - 1, z1 = z0 + fp - 1;
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                if (mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Road))
                    return true;
        return false;
    }

    /// <summary>Назначает слой заспавненному объекту и его детям (если в типе задан objectLayer).</summary>
    private void ApplyLayer(GameObject obj, ObjectType objType)
    {
        int layer = MaskToLayer(objType.objectLayer);
        if (layer < 0) return;
        SetLayerRecursive(obj.transform, layer);
    }

    private static int MaskToLayer(LayerMask mask)
    {
        int v = mask.value;
        if (v == 0) return -1;                 // Nothing → не менять слой
        for (int i = 0; i < 32; i++)
            if ((v & (1 << i)) != 0) return i; // берём первый выбранный слой
        return -1;
    }

    private static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursive(t.GetChild(i), layer);
    }

    private GameObject InstantiatePrefab(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (Application.isPlaying)
        {
            return Instantiate(prefab, position, rotation, objectsParent);
        }
        else
        {
#if UNITY_EDITOR
            GameObject obj = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, objectsParent);
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            return obj;
#else
            return Instantiate(prefab, position, rotation, objectsParent);
#endif
        }
    }

    private Quaternion GetSurfaceRotation(Vector3 worldPos)
    {
        float rayLength = 20f;
        if (Physics.Raycast(worldPos + Vector3.up * rayLength, Vector3.down, out RaycastHit hit, rayLength * 2f))
            return Quaternion.FromToRotation(Vector3.up, hit.normal);

        return Quaternion.identity;
    }
}