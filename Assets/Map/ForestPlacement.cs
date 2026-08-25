using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Размещение леса: зоны плотности, стена по краю, генерация деревьев в сектора.
/// ТОЛЬКО ДАННЫЕ + расстановка. Отрисовка — ForestRenderer, видимость — ViewOcclusion.
/// </summary>
public class ForestPlacement : MonoBehaviour
{
    public enum Density : byte
    {
        Clear = 0,
        Sparse = 1,
        Grove = 2,
        Dense = 3
    }

    [System.Serializable]
    public class TreePart
    {
        public Mesh mesh;
        public Material material;
        public int subMeshIndex;
        public Matrix4x4 localToRoot;
    }

    [System.Serializable]
    public class TreeType
    {
        public string name = "Tree";
        public GameObject prefab;
        [HideInInspector] public Mesh mesh;
        [HideInInspector] public Material material;
        [HideInInspector] public int subMeshIndex = 0;

        [Header("Плотность внутри зоны")]
        [Range(0f, 1f)] public float densityClear = 0.0f;
        [Range(0f, 1f)] public float densitySparse = 0.40f;
        [Range(0f, 1f)] public float densityGrove = 0.92f;
        [Range(0f, 1f)] public float densityDense = 0.98f;

        public int treesPerCell = 1;

        [Header("Внешний вид")]
        public bool randomRotationY = true;
        public float minScale = 0.9f;
        public float maxScale = 1.2f;
        [Range(0.25f, 1f)] public float trunkThinness = 0.50f;
        public float targetFootprint = 0f;
        public float heightOffset = 0f;
        [Range(0f, 0.15f)] public float cellJitter = 0f;
        public float minDistance = 0f;
        public UnityEngine.Rendering.ShadowCastingMode shadowCasting = UnityEngine.Rendering.ShadowCastingMode.On;
        public bool receiveShadows = true;

        [HideInInspector] public List<TreePart> parts = new List<TreePart>();
        [HideInInspector] public Dictionary<Vector2Int, Matrix4x4[][]> sectors;
        [HideInInspector] public MaterialPropertyBlock propertyBlock;
    }

    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder terrainBuilder;
    public RoadGenerator roadGenerator;
    public Transform player;

    [Header("Типы деревьев")]
    public List<TreeType> treeTypes = new List<TreeType>();

    [Header("Пресет")]
    public bool applyPresetOnPlace = true;

    [Header("Зоны леса")]
    public float zoneNoiseScale = 0.006f;
    public float detailNoiseScale = 0.03f;
    [Range(0f, 1f)] public float sparseThreshold = 0.32f;
    [Range(0f, 1f)] public float groveThreshold = 0.52f;
    [Range(0f, 1f)] public float denseThreshold = 0.72f;
    public float minHeight = 0f;

    [Header("Стена и дорога")]
    public int edgeWallCells = 16;
    public int edgeWallSoftCells = 10;
    public int roadClearanceCells = 5;
    public int roadExitClearance = 5;

    [Header("Размещение деревьев")]
    public float treeSpacing = 14f;
    [Range(1, 8)] public int treeOccupyCells = 4;
    [Range(0f, 0.4f)] public float placementJitter = 0.18f;

    [Header("Секторы")]
    public int sectorSize = 16;
    public float drawRadius = 50f;

    [Header("Live objects")]
    public float liveRadius = 15f;

    [Header("Сид")]
    public bool randomSeed = true;
    public int seed = 42;

    [Header("Pathfinder")]
    public bool denseBlocksMovement = true;

    [Header("Редактор")]
    public bool showZoneGizmos = true;
    public int gizmoStep = 5;
    public Color clearColor = new Color(0.4f, 0.55f, 0.3f, 0.05f);
    public Color sparseColor = new Color(0.35f, 0.55f, 0.25f, 0.14f);
    public Color groveColor = new Color(0.18f, 0.48f, 0.14f, 0.28f);
    public Color denseColor = new Color(0.05f, 0.28f, 0.05f, 0.45f);
    public float gizmoYOffset = 0.15f;

    private bool isGenerated;
    private Density[,] zoneMap;
    private Vector2 noiseOffset;
    private Vector2 detailOffset;
    private const int BatchSize = 1023;

    private float _ts, _sectorWorld;
    private int _w, _d, _drawSectorR;
    private Vector3 _origin;

    // Live hybrid
    private class LiveTree
    {
        public TreeType type;
        public Matrix4x4 matrix;
        public GameObject go;
        public long key;
    }

    private readonly List<LiveTree> _live = new List<LiveTree>(48);
    private readonly HashSet<long> _liveKeys = new HashSet<long>();
    private Transform _liveRoot;
    private float _liveRadiusSq;
    private float _demoteRadiusSq;

    // =================== API ===================

    [ContextMenu("Apply Cinematic Preset")]
    public void ApplyCinematicPreset()
    {
        zoneNoiseScale = 0.006f;
        detailNoiseScale = 0.03f;
        sparseThreshold = 0.32f;
        groveThreshold = 0.52f;
        denseThreshold = 0.72f;
        edgeWallCells = 16;
        edgeWallSoftCells = 10;
        roadClearanceCells = 5;
        roadExitClearance = 5;
        treeSpacing = 14f;
        treeOccupyCells = 4;
        placementJitter = 0.18f;
        drawRadius = 50f;

        if (treeTypes == null) treeTypes = new List<TreeType>();
        if (treeTypes.Count == 0) treeTypes.Add(new TreeType());

        foreach (var t in treeTypes)
        {
            t.densityClear = 0.0f;
            t.densitySparse = 0.40f;
            t.densityGrove = 0.92f;
            t.densityDense = 0.98f;
            t.treesPerCell = 1;
            t.minScale = 0.9f;
            t.maxScale = 1.2f;
            t.trunkThinness = 0.50f;
            t.targetFootprint = 0f;
            t.minDistance = 0f;
            t.cellJitter = 0f;
            t.randomRotationY = true;
            t.heightOffset = 0f;
        }
        zoneMap = null;
        Debug.Log("ForestPlacement: preset applied");
    }

    [ContextMenu("Place Forest")]
    public void PlaceForest()
    {
        if (!Validate()) return;
        if (applyPresetOnPlace) ApplyCinematicPreset();

        ClearLiveTrees();

        if (randomSeed) seed = Random.Range(0, 100000);
        Random.InitState(seed);
        noiseOffset = new Vector2(Random.Range(0f, 9999f), Random.Range(0f, 9999f));
        detailOffset = new Vector2(Random.Range(0f, 9999f), Random.Range(0f, 9999f));

        _w = heightSource.width;
        _d = heightSource.depth;
        _ts = terrainBuilder.TileSize;
        _origin = new Vector3(-_w * _ts / 2f, 0f, -_d * _ts / 2f);
        _sectorWorld = sectorSize * _ts;
        _drawSectorR = Mathf.Max(1, Mathf.CeilToInt(drawRadius / _sectorWorld));

        BuildZoneMap(_w, _d);

        int grandTotal = 0;
        foreach (var t in treeTypes)
        {
            t.propertyBlock = new MaterialPropertyBlock();
            if (!ResolveMeshAndMaterial(t))
            {
                t.sectors = null;
                continue;
            }

            var temp = new Dictionary<Vector2Int, List<Matrix4x4>>();
            int count = PlaceTrees(t, _w, _d, _ts, _origin, temp);
            t.sectors = new Dictionary<Vector2Int, Matrix4x4[][]>();
            foreach (var kv in temp)
                t.sectors[kv.Key] = SplitToBatches(kv.Value);

            grandTotal += count;
            Debug.Log($"ForestPlacement: '{t.name}' — {count} trees, {t.sectors.Count} sectors");
        }

        isGenerated = true;
        Debug.Log($"ForestPlacement: total {grandTotal} trees, map {_w}×{_d}");
    }

    [ContextMenu("Clear Forest")]
    public void ClearForest()
    {
        ClearLiveTrees();
        if (treeTypes != null)
            foreach (var t in treeTypes) t.sectors = null;
        zoneMap = null;
        isGenerated = false;
    }

    public Density GetDensity(int x, int z)
    {
        if (zoneMap == null) return Density.Clear;
        if (x < 0 || z < 0 || x >= zoneMap.GetLength(0) || z >= zoneMap.GetLength(1))
            return Density.Clear;
        return zoneMap[x, z];
    }

    public bool BlocksMovement(int x, int z)
    {
        if (!denseBlocksMovement || zoneMap == null) return false;
        return GetDensity(x, z) == Density.Dense;
    }

    public bool IsGenerated => isGenerated;
    public float SectorWorld => _sectorWorld;
    public Vector3 Origin => _origin;
    public int DrawSectorR => _drawSectorR;

    // =================== Зоны ===================

    private void BuildZoneMap(int w, int d)
    {
        zoneMap = new Density[w, d];

        int macro = 1;
        if (heightSource != null && heightSource.macroSize > 1)
            macro = heightSource.macroSize;
        int zoneBlock = Mathf.Max(macro, 8);

        int bw = Mathf.CeilToInt((float)w / zoneBlock);
        int bd = Mathf.CeilToInt((float)d / zoneBlock);
        var blockDens = new Density[bw, bd];

        for (int bx = 0; bx < bw; bx++)
        {
            for (int bz = 0; bz < bd; bz++)
            {
                int cx = Mathf.Min(bx * zoneBlock + zoneBlock / 2, w - 1);
                int cz = Mathf.Min(bz * zoneBlock + zoneBlock / 2, d - 1);

                if (IsRoadCell(cx, cz) || IsNearRoad(cx, cz) || IsNearRoadExit(cx, cz))
                {
                    blockDens[bx, bz] = Density.Clear;
                    continue;
                }

                int distEdge = Mathf.Min(cx, cz, w - 1 - cx, d - 1 - cz);
                int hardWall = Mathf.Max(0, edgeWallCells - edgeWallSoftCells);

                if (distEdge < hardWall)
                {
                    blockDens[bx, bz] = Density.Dense;
                    continue;
                }
                if (distEdge < edgeWallCells)
                {
                    float soft = Mathf.Max(1f, edgeWallSoftCells);
                    float t = (distEdge - hardWall) / soft;
                    if (t < 0.4f) blockDens[bx, bz] = Density.Dense;
                    else if (t < 0.75f) blockDens[bx, bz] = Density.Grove;
                    else blockDens[bx, bz] = Density.Sparse;
                    continue;
                }

                float n = Mathf.PerlinNoise(
                    (cx + noiseOffset.x) * zoneNoiseScale,
                    (cz + noiseOffset.y) * zoneNoiseScale);

                if (n >= denseThreshold) blockDens[bx, bz] = Density.Dense;
                else if (n >= groveThreshold) blockDens[bx, bz] = Density.Grove;
                else if (n >= sparseThreshold) blockDens[bx, bz] = Density.Sparse;
                else blockDens[bx, bz] = Density.Clear;
            }
        }

        for (int x = 0; x < w; x++)
            for (int z = 0; z < d; z++)
            {
                if (IsRoadCell(x, z) || IsNearRoad(x, z) || IsNearRoadExit(x, z))
                {
                    zoneMap[x, z] = Density.Clear;
                    continue;
                }
                int bx = Mathf.Min(x / zoneBlock, bw - 1);
                int bz = Mathf.Min(z / zoneBlock, bd - 1);
                zoneMap[x, z] = blockDens[bx, bz];
            }

        ApplyZoneTransitions(w, d);
    }

    private void ApplyZoneTransitions(int w, int d)
    {
        var copy = (Density[,])zoneMap.Clone();
        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

        for (int x = 1; x < w - 1; x++)
        {
            for (int z = 1; z < d - 1; z++)
            {
                if (copy[x, z] == Density.Clear) continue;
                if (IsRoadCell(x, z) || IsNearRoad(x, z)) continue;

                Density cur = copy[x, z];
                Density minN = cur;
                for (int k = 0; k < 8; k++)
                {
                    Density n = copy[x + dx[k], z + dz[k]];
                    if (n < minN) minN = n;
                }

                if (minN < cur)
                {
                    if (cur == Density.Dense && minN <= Density.Grove)
                        zoneMap[x, z] = Density.Grove;
                    else if (cur == Density.Grove && minN <= Density.Sparse)
                        zoneMap[x, z] = Density.Sparse;
                    else if (cur == Density.Sparse && minN == Density.Clear)
                        zoneMap[x, z] = Density.Sparse;
                }
            }
        }
    }

    private bool IsRoadCell(int x, int z)
        => roadGenerator != null && roadGenerator.IsRoad(x, z);

    private bool IsNearRoad(int x, int z)
    {
        if (roadGenerator == null || roadClearanceCells <= 0) return false;
        int w = heightSource.width, d = heightSource.depth, r = roadClearanceCells;
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d) continue;
                if (roadGenerator.IsRoad(nx, nz)) return true;
            }
        return false;
    }

    private bool IsNearRoadExit(int x, int z)
    {
        if (roadGenerator == null || roadExitClearance <= 0) return false;
        int w = heightSource.width, d = heightSource.depth;
        bool nearEdge = x < edgeWallCells || x >= w - edgeWallCells
                     || z < edgeWallCells || z >= d - edgeWallCells;
        if (!nearEdge) return false;
        int r = roadExitClearance;
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d) continue;
                if (roadGenerator.IsRoad(nx, nz)) return true;
            }
        return false;
    }

    private int PlaceTrees(TreeType t, int w, int d, float ts, Vector3 origin,
                           Dictionary<Vector2Int, List<Matrix4x4>> temp)
    {
        int total = 0;
        float thin = Mathf.Clamp(t.trunkThinness, 0.2f, 1f);

        float meshXZ = 1f;
        if (t.parts != null && t.parts.Count > 0)
        {
            Bounds combined = new Bounds();
            bool first = true;
            foreach (var part in t.parts)
            {
                if (part.mesh == null) continue;
                Bounds mb = part.mesh.bounds;
                Vector3 worldCenter = part.localToRoot.MultiplyPoint3x4(mb.center);
                Vector3 lossy = part.localToRoot.lossyScale;
                Vector3 size = new Vector3(
                    mb.size.x * Mathf.Abs(lossy.x),
                    mb.size.y * Mathf.Abs(lossy.y),
                    mb.size.z * Mathf.Abs(lossy.z));
                Bounds pb = new Bounds(worldCenter, size);
                if (first) { combined = pb; first = false; }
                else combined.Encapsulate(pb);
            }
            meshXZ = Mathf.Max(combined.size.x, combined.size.z);
            if (meshXZ < 0.01f) meshXZ = 1f;
        }
        else if (t.mesh != null)
        {
            var b = t.mesh.bounds.size;
            meshXZ = Mathf.Max(b.x, b.z);
            if (meshXZ < 0.01f) meshXZ = 1f;
        }

        int occupy = Mathf.Clamp(treeOccupyCells, 1, 8);
        float target = t.targetFootprint > 0.1f
            ? t.targetFootprint
            : (occupy * ts) * 0.95f;
        float fitSxz = target / meshXZ;

        float spacingWorld = Mathf.Max(ts, treeSpacing);
        int step = Mathf.Max(1, Mathf.RoundToInt(spacingWorld / ts));
        float jitterMax = spacingWorld * Mathf.Clamp01(placementJitter);

        for (int bx = 0; bx < w; bx += step)
        {
            for (int bz = 0; bz < d; bz += step)
            {
                int cx = Mathf.Min(bx + step / 2, w - 1);
                int cz = Mathf.Min(bz + step / 2, d - 1);

                if (IsRoadCell(cx, cz) || IsNearRoad(cx, cz) || IsNearRoadExit(cx, cz)) continue;
                if (heightSource.GetHeight(cx, cz) < minHeight) continue;

                Density dens = zoneMap[cx, cz];
                float chance = dens == Density.Dense ? t.densityDense
                              : dens == Density.Grove ? t.densityGrove
                              : dens == Density.Sparse ? t.densitySparse
                              : t.densityClear;
                if (chance <= 0f || Random.value > chance) continue;

                float px = origin.x + (cx + 0.5f) * ts;
                float pz = origin.z + (cz + 0.5f) * ts;
                if (jitterMax > 0.01f)
                {
                    px += Random.Range(-jitterMax, jitterMax);
                    pz += Random.Range(-jitterMax, jitterMax);
                }

                int jx = Mathf.Clamp(Mathf.FloorToInt((px - origin.x) / ts), 0, w - 1);
                int jz = Mathf.Clamp(Mathf.FloorToInt((pz - origin.z) / ts), 0, d - 1);
                if (IsRoadCell(jx, jz) || IsNearRoad(jx, jz)) continue;

                float py = heightSource.GetHeight(jx, jz) + t.heightOffset;
                float rotY = t.randomRotationY ? Random.Range(0f, 360f) : 0f;
                float sxz = fitSxz;
                float sy = (sxz / thin) * Random.Range(t.minScale, t.maxScale);

                Vector2Int sector = new Vector2Int(jx / sectorSize, jz / sectorSize);
                if (!temp.TryGetValue(sector, out var list))
                {
                    list = new List<Matrix4x4>(8);
                    temp[sector] = list;
                }

                list.Add(Matrix4x4.TRS(
                    new Vector3(px, py, pz),
                    Quaternion.Euler(0f, rotY, 0f),
                    new Vector3(sxz, sy, sxz)));
                total++;

                int half = occupy / 2;
                int x0 = Mathf.Max(0, jx - half);
                int z0 = Mathf.Max(0, jz - half);
                int x1 = Mathf.Min(w, jx - half + occupy);
                int z1 = Mathf.Min(d, jz - half + occupy);
                for (int x = x0; x < x1; x++)
                    for (int z = z0; z < z1; z++)
                        zoneMap[x, z] = Density.Dense;
            }
        }
        return total;
    }

    private static Matrix4x4[][] SplitToBatches(List<Matrix4x4> list)
    {
        int n = list.Count;
        if (n == 0) return System.Array.Empty<Matrix4x4[]>();
        int batches = (n + BatchSize - 1) / BatchSize;
        var result = new Matrix4x4[batches][];
        for (int b = 0; b < batches; b++)
        {
            int start = b * BatchSize;
            int count = Mathf.Min(BatchSize, n - start);
            var batch = new Matrix4x4[count];
            for (int i = 0; i < count; i++) batch[i] = list[start + i];
            result[b] = batch;
        }
        return result;
    }

    // =================== Обход матриц (без отрисовки!) ===================

    public void EnsureDrawCache()
    {
        if (terrainBuilder == null || heightSource == null) return;
        _ts = terrainBuilder.TileSize;
        _sectorWorld = sectorSize * _ts;
        _drawSectorR = Mathf.Max(1, Mathf.CeilToInt(drawRadius / _sectorWorld));
        _w = heightSource.width;
        _d = heightSource.depth;
        _origin = new Vector3(-_w * _ts / 2f, 0f, -_d * _ts / 2f);
    }

    public void ForEachVisibleMatrix(Vector3 focus, System.Action<TreeType, Matrix4x4> fn)
    {
        ForEachVisibleMatrix(focus, -1f, fn);
    }

    public void ForEachVisibleMatrix(Vector3 focus, float radiusOverride, System.Action<TreeType, Matrix4x4> fn)
    {
        if (!isGenerated || fn == null) return;
        if (_sectorWorld <= 0f) EnsureDrawCache();
        if (_sectorWorld <= 0f) return;

        int pcx = Mathf.FloorToInt((focus.x - _origin.x) / _sectorWorld);
        int pcz = Mathf.FloorToInt((focus.z - _origin.z) / _sectorWorld);
        float useRadius = radiusOverride > 0f ? radiusOverride : drawRadius;
        int rad = Mathf.Max(1, Mathf.CeilToInt(useRadius / _sectorWorld));

        foreach (var t in treeTypes)
        {
            if (t.sectors == null || t.parts == null || t.parts.Count == 0) continue;
            for (int sx = pcx - rad; sx <= pcx + rad; sx++)
                for (int sz = pcz - rad; sz <= pcz + rad; sz++)
                {
                    if (!t.sectors.TryGetValue(new Vector2Int(sx, sz), out var batches)) continue;
                    for (int b = 0; b < batches.Length; b++)
                    {
                        var batch = batches[b];
                        for (int i = 0; i < batch.Length; i++)
                        {
                            fn(t, batch[i]);
                        }
                    }
                }
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!showZoneGizmos) return;
        if (heightSource == null || !heightSource.isGenerated || terrainBuilder == null) return;
        EnsurePreviewZoneMap();

        int ww = heightSource.width, dd = heightSource.depth;
        float ts = terrainBuilder.TileSize;
        Vector3 origin = new Vector3(-ww * ts / 2f, 0f, -dd * ts / 2f);
        int step = gizmoStep;
        if (heightSource.macroSize > 1)
            step = heightSource.macroSize;
        step = Mathf.Max(1, step);

        for (int x = 0; x < ww; x += step)
        for (int z = 0; z < dd; z += step)
        {
            Density dens = zoneMap[x, z];
            int hx = Mathf.Min(x + step / 2, ww - 1);
            int hz = Mathf.Min(z + step / 2, dd - 1);
            float h = heightSource.GetHeight(hx, hz);
            Vector3 center = new Vector3(
                origin.x + (x + step * 0.5f) * ts,
                h + gizmoYOffset,
                origin.z + (z + step * 0.5f) * ts);
            
            Color fill = dens == Density.Dense ? denseColor
                       : dens == Density.Grove ? groveColor
                       : dens == Density.Sparse ? sparseColor
                       : clearColor;
            if (fill.a < 0.01f) continue;
            Gizmos.color = fill;
            Gizmos.DrawCube(center, new Vector3(ts * step * 0.98f, 0.05f, ts * step * 0.98f));
        }
    }

    private void EnsurePreviewZoneMap()
    {
        if (zoneMap != null && zoneMap.GetLength(0) == heightSource.width) return;
        Random.InitState(seed);
        noiseOffset = new Vector2((seed % 1000) * 0.17f, (seed % 1000) * 0.31f);
        detailOffset = new Vector2((seed % 1000) * 0.23f, (seed % 1000) * 0.41f);
        BuildZoneMap(heightSource.width, heightSource.depth);
    }
#endif

    private bool Validate()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("ForestPlacement: HeightMapGenerator не готов!");
            return false;
        }
        if (terrainBuilder == null)
        {
            Debug.LogError("ForestPlacement: нет ChunkedTerrainBuilder!");
            return false;
        }
        if (treeTypes == null || treeTypes.Count == 0)
            Debug.LogWarning("ForestPlacement: нет типов деревьев — добавь Prefab.");
        return true;
    }

    private static bool ResolveMeshAndMaterial(TreeType t)
    {
        t.parts = t.parts ?? new List<TreePart>();
        t.parts.Clear();

        if (t.prefab != null)
        {
            if (string.IsNullOrEmpty(t.name) || t.name == "Tree" || t.name == "TreePrefab")
                t.name = t.prefab.name;

            Transform root = t.prefab.transform;
            var filters = t.prefab.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                Matrix4x4 local = RelativeMatrix(root, mf.transform);
                int subCount = mf.sharedMesh.subMeshCount;
                Material[] mats = null;
                if (mr != null) mats = mr.sharedMaterials;

                for (int s = 0; s < subCount; s++)
                {
                    Material mat = null;
                    if (mats != null && s < mats.Length) mat = mats[s];
                    if (mat == null && mr != null) mat = mr.sharedMaterial;
                    if (mat == null) mat = CreateTestMaterial(t.name);
                    if (!mat.enableInstancing) mat.enableInstancing = true;

                    t.parts.Add(new TreePart
                    {
                        mesh = mf.sharedMesh,
                        material = mat,
                        subMeshIndex = s,
                        localToRoot = local
                    });
                }
            }
        }

        if (t.parts.Count > 0)
        {
            t.mesh = t.parts[0].mesh;
            t.material = t.parts[0].material;
            t.subMeshIndex = t.parts[0].subMeshIndex;
        }

        if (t.parts.Count == 0)
        {
            Debug.LogWarning($"ForestPlacement: '{t.name}' — нет mesh в префабе.");
            return false;
        }

        return true;
    }

    private static Matrix4x4 RelativeMatrix(Transform root, Transform t)
    {
        if (t == null || root == null) return Matrix4x4.identity;
        if (t == root) return Matrix4x4.identity;
        Matrix4x4 m = Matrix4x4.identity;
        Transform cur = t;
        while (cur != null && cur != root)
        {
            m = Matrix4x4.TRS(cur.localPosition, cur.localRotation, cur.localScale) * m;
            cur = cur.parent;
        }
        return m;
    }

    private static Material CreateTestMaterial(string treeName)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader != null ? shader : Shader.Find("Hidden/InternalErrorShader"));
        mat.name = $"ForestTest_{treeName}";
        mat.enableInstancing = true;
        Color green = new Color(0.18f, 0.32f, 0.12f, 1f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", green);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", green);
        return mat;
    }

    // =================== Live hybrid (GameObject'ы, не отрисовка) ===================

    public bool IsLiveAt(Vector3 worldPos) => _liveKeys.Contains(MakeKey(worldPos));

    private static long MakeKey(Vector3 p)
    {
        int x = Mathf.RoundToInt(p.x * 4f);
        int z = Mathf.RoundToInt(p.z * 4f);
        return ((long)x << 32) | (uint)z;
    }

    private void ClearLiveTrees()
    {
        for (int i = 0; i < _live.Count; i++)
        {
            var lt = _live[i];
            if (lt.go != null)
            {
                if (Application.isPlaying) Destroy(lt.go);
                else DestroyImmediate(lt.go);
            }
        }
        _live.Clear();
        _liveKeys.Clear();
    }

    void OnDestroy() => ClearLiveTrees();
}