using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Стриминг природы по слоям.
/// Слои: Основной лес / Маленькие деревья / Кусты / Растения / Трава(stub).
/// В каждом слое — варианты с весом (%). Префаб или спрайт.
/// Зоны непрерывные (Perlin). Роща детерминирована, лес/чаща — рандом при загрузке.
/// </summary>
public class NaturePlacement : MonoBehaviour, CollectablePlant.IInstanceRemover
{
    public enum Zone : byte { None = 0, Grove = 1, Forest = 2, Thicket = 3 }
    public enum LayerKind : byte { MainForest = 0, SmallTree = 1, Bush = 2, Plant = 3, Grass = 4 }

    [System.Serializable]
    public class NatureVariant
    {
        public string name = "Variant";
        [Tooltip("Префаб (для деревьев/live).")]
        public GameObject prefab;
        [Tooltip("Меш для инстансинга. Если пусто — из prefab.")]
        public Mesh mesh;
        [Tooltip("Материал с Enable GPU Instancing.")]
        public Material material;
        [Tooltip("Спрайт (кусты/растения).")]
        public Sprite sprite;

        [Range(0f, 100f)]
        [Tooltip("Доля среди вариантов слоя (вес).")]
        public float weight = 100f;

        public float minScale = 0.9f;
        public float maxScale = 1.3f;
        public float heightOffset = 0f;
        public bool randomYRotation = true;
        public bool castShadows = true;
        public Vector3 spriteEuler = Vector3.zero;

        [Header("Укрытие (зрение)")]
        [Tooltip("Свои значения вместо слоя.")]
        public bool overrideCover = false;
        public MapGrid.SightCoverMode sightCover = MapGrid.SightCoverMode.None;
        [Tooltip("Если MaxHeight — высота укрытия над землёй (м).")]
        public float maxCoverHeight = 2.5f;

        [HideInInspector] public Mesh resolvedMesh;
        [HideInInspector] public List<MeshPart> parts;
        [HideInInspector] public MaterialPropertyBlock propertyBlock;
        [HideInInspector] public Dictionary<Vector2Int, Matrix4x4[][]> sectors;
        [HideInInspector] public Dictionary<Vector2Int, List<Matrix4x4>> sectorLists;
    }

    public class MeshPart
    {
        public Mesh mesh;
        public Material material;
        public Matrix4x4 localToRoot;
        public int submesh;
    }

    [System.Serializable]
    public class NatureLayer
    {
        public string name = "Layer";
        public LayerKind kind = LayerKind.MainForest;
        public bool enabled = true;

        [Tooltip("Занятость клеток (только деревья). 3 = 3x3.")]
        public int footprint = 3;

        [Header("Плотность по зонам")]
        [Range(0f, 1f)] public float densityGrove = 0.14f;
        [Range(0f, 1f)] public float densityForest = 0.52f;
        [Range(0f, 1f)] public float densityThicket = 0.82f;

        [Header("Зоны, где растёт")]
        public bool growInGrove = true;
        public bool growInForest = true;
        public bool growInThicket = true;

        [Header("Варианты (вес = доля)")]
        public List<NatureVariant> variants = new List<NatureVariant>();

        [Header("Укрытие (зрение)")]
        [Tooltip("Full — глухое. MaxHeight — закрывает луч ниже maxCoverHeight.")]
        public MapGrid.SightCoverMode sightCover = MapGrid.SightCoverMode.None;
        [Tooltip("Высота укрытия над землёй клетки (м), если режим MaxHeight.")]
        public float maxCoverHeight = 2.5f;

        public bool IsTree => kind == LayerKind.MainForest || kind == LayerKind.SmallTree;
    }

    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder terrainBuilder;
    public MapGrid mapGrid;

    [Header("Слои")]
    public List<NatureLayer> layers = new List<NatureLayer>();

    [Header("Зоны (Perlin) — непрерывные")]
    [Tooltip("Меньше = крупнее пятна")]
    public float zoneNoiseScale = 0.028f;
    [Range(0f, 1f)] public float groveThreshold = 0.28f;
    [Range(0f, 1f)] public float thicketThreshold = 0.72f;
    public Vector2 zoneNoiseOffset = new Vector2(17.3f, 91.7f);

    [Header("Карта")]
    public float waterLevel = 0f;
    public int borderCells = 2;
    public int sectorSize = 16;

    [Header("Стриминг")]
    public float streamRadius = 50f;
    public float unloadExtra = 30f;

    public bool IsReady => _ready;

    private bool _ready;
    private readonly HashSet<Vector2Int> _loaded = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> _toUnload = new List<Vector2Int>();
    private const int BatchSize = 1023;

    [HideInInspector] public List<NatureVariant> allVariants = new List<NatureVariant>();
    [HideInInspector] public List<NatureLayer> variantLayer = new List<NatureLayer>();

    [ContextMenu("Fill Default Layers (HP Forest)")]
    public void FillDefaultLayers()
    {
        layers = new List<NatureLayer>
        {
            new NatureLayer
            {
                name = "Основной лес",
                kind = LayerKind.MainForest,
                enabled = true,
                footprint = 3,
                densityGrove = 0.12f,
                densityForest = 0.55f,
                densityThicket = 0.85f,
                growInGrove = true,
                growInForest = true,
                growInThicket = true,
                sightCover = MapGrid.SightCoverMode.Full,
                maxCoverHeight = 0f,
                variants = new List<NatureVariant>
                {
                    new NatureVariant
                    {
                        name = "Tree_Main",
                        weight = 100f,
                        minScale = 2.8f,
                        maxScale = 4.2f,
                        castShadows = true,
                        randomYRotation = true
                    }
                }
            },
            new NatureLayer
            {
                name = "Маленькие деревья",
                kind = LayerKind.SmallTree,
                enabled = false,
                footprint = 2,
                densityGrove = 0.08f,
                densityForest = 0.2f,
                densityThicket = 0.35f,
                sightCover = MapGrid.SightCoverMode.MaxHeight,
                maxCoverHeight = 2.4f,
                variants = new List<NatureVariant>
                {
                    new NatureVariant { name = "SmallTree_Stub", weight = 100f, minScale = 0.5f, maxScale = 0.85f }
                }
            },
            new NatureLayer
            {
                name = "Кусты",
                kind = LayerKind.Bush,
                enabled = false,
                footprint = 1,
                densityGrove = 0.15f,
                densityForest = 0.3f,
                densityThicket = 0.5f,
                sightCover = MapGrid.SightCoverMode.MaxHeight,
                maxCoverHeight = 1.2f,
                variants = new List<NatureVariant>
                {
                    new NatureVariant { name = "Bush_Stub", weight = 100f, minScale = 0.7f, maxScale = 1.1f }
                }
            },
            new NatureLayer
            {
                name = "Растения",
                kind = LayerKind.Plant,
                enabled = false,
                footprint = 1,
                densityGrove = 0.2f,
                densityForest = 0.25f,
                densityThicket = 0.15f,
                variants = new List<NatureVariant>
                {
                    new NatureVariant { name = "Plant_Stub", weight = 100f, minScale = 0.6f, maxScale = 1.0f }
                }
            },
            new NatureLayer
            {
                name = "Трава (Unity later)",
                kind = LayerKind.Grass,
                enabled = false,
                footprint = 1,
                densityGrove = 0f,
                densityForest = 0f,
                densityThicket = 0f,
                variants = new List<NatureVariant>()
            }
        };
        Debug.Log("NaturePlacement: default layers filled (Main Forest ready, rest stubs).");
    }

    [ContextMenu("Init")]
    public void Init()
    {
        if (!Validate()) return;

        if (layers == null || layers.Count == 0)
            FillDefaultLayers();

        allVariants.Clear();
        variantLayer.Clear();

        foreach (var layer in layers)
        {
            if (layer == null || !layer.enabled || layer.variants == null) continue;
            foreach (var v in layer.variants)
            {
                if (v == null) continue;
                if (!ResolveParts(v))
                {
                    Debug.LogWarning($"NaturePlacement: '{layer.name}/{v.name}' — нет частей mesh/material, пропуск.");
                    continue;
                }
                v.propertyBlock = new MaterialPropertyBlock();
                if (v.sprite != null && v.mesh == null && v.prefab == null)
                {
                    v.propertyBlock.SetTexture("_BaseMap", v.sprite.texture);
                    v.propertyBlock.SetTexture("_MainTex", v.sprite.texture);
                }
                if (v.sectors == null) v.sectors = new Dictionary<Vector2Int, Matrix4x4[][]>();
                if (v.sectorLists == null) v.sectorLists = new Dictionary<Vector2Int, List<Matrix4x4>>();
                allVariants.Add(v);
                variantLayer.Add(layer);
            }
        }

        _ready = true;
        int partSum = 0;
        for (int i = 0; i < allVariants.Count; i++)
            if (allVariants[i].parts != null) partSum += allVariants[i].parts.Count;
        Debug.Log($"NaturePlacement: Init OK, variants={allVariants.Count}, parts={partSum}");
    }

    public void UpdateStreaming(Vector3 playerPos)
    {
        if (!_ready || heightSource == null || !heightSource.isGenerated || terrainBuilder == null)
            return;

        float ts = terrainBuilder.TileSize;
        float sectorWorld = sectorSize * ts;
        int w = heightSource.width;
        int d = heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);

        int pcx = Mathf.FloorToInt((playerPos.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((playerPos.z - origin.z) / sectorWorld);
        int loadR = Mathf.CeilToInt(streamRadius / sectorWorld);
        int unloadR = Mathf.CeilToInt((streamRadius + unloadExtra) / sectorWorld);

        for (int sx = pcx - loadR; sx <= pcx + loadR; sx++)
        {
            for (int sz = pcz - loadR; sz <= pcz + loadR; sz++)
            {
                var key = new Vector2Int(sx, sz);
                if (_loaded.Contains(key)) continue;
                if (sx < 0 || sz < 0 || sx * sectorSize >= w || sz * sectorSize >= d) continue;
                GenerateSector(sx, sz);
                _loaded.Add(key);
            }
        }

        _toUnload.Clear();
        foreach (var key in _loaded)
        {
            if (Mathf.Abs(key.x - pcx) > unloadR || Mathf.Abs(key.y - pcz) > unloadR)
                _toUnload.Add(key);
        }
        for (int i = 0; i < _toUnload.Count; i++)
        {
            UnloadSector(_toUnload[i].x, _toUnload[i].y);
            _loaded.Remove(_toUnload[i]);
        }
    }

    [ContextMenu("Unload All")]
    public void UnloadAll()
    {
        _toUnload.Clear();
        foreach (var k in _loaded) _toUnload.Add(k);
        for (int i = 0; i < _toUnload.Count; i++)
            UnloadSector(_toUnload[i].x, _toUnload[i].y);
        _loaded.Clear();
    }

    public bool RemoveInstance(int typeIndex, Vector2Int sector, Vector3 worldPos)
    {
        if (typeIndex < 0 || typeIndex >= allVariants.Count) return false;
        var v = allVariants[typeIndex];
        if (v.sectorLists == null || !v.sectorLists.TryGetValue(sector, out var list)) return false;

        for (int i = 0; i < list.Count; i++)
        {
            Vector3 p = list[i].GetColumn(3);
            if ((p - worldPos).sqrMagnitude < 0.0001f)
            {
                list.RemoveAt(i);
                v.sectors[sector] = SplitToBatches(list);
                return true;
            }
        }
        return false;
    }

    private void GenerateSector(int sx, int sz)
    {
        float ts = terrainBuilder.TileSize;
        int w = heightSource.width;
        int d = heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);

        int x0 = sx * sectorSize;
        int z0 = sz * sectorSize;
        int x1 = Mathf.Min(w, x0 + sectorSize);
        int z1 = Mathf.Min(d, z0 + sectorSize);
        var key = new Vector2Int(sx, sz);

        for (int vi = 0; vi < allVariants.Count; vi++)
        {
            var v = allVariants[vi];
            var layer = variantLayer[vi];
            if (v.parts == null || v.parts.Count == 0) continue;

            if (!v.sectorLists.TryGetValue(key, out var list))
            {
                list = new List<Matrix4x4>();
                v.sectorLists[key] = list;
            }
            else list.Clear();

            int fp = layer.IsTree ? Mathf.Max(1, layer.footprint) : 1;
            int half = fp / 2;
            bool useGrid = mapGrid != null && mapGrid.IsReady && layer.IsTree;
            int step = layer.IsTree ? Mathf.Max(1, fp) : 1;
            Quaternion spriteRot = Quaternion.Euler(v.spriteEuler);

            float layerWeightSum = 0f;
            foreach (var ov in layer.variants) if (ov != null) layerWeightSum += Mathf.Max(0f, ov.weight);
            float myChance = layerWeightSum > 0f ? Mathf.Max(0f, v.weight) / layerWeightSum : 1f;

            for (int x = Mathf.Max(borderCells + half, x0); x < Mathf.Min(w - borderCells - half, x1); x += step)
            {
                for (int z = Mathf.Max(borderCells + half, z0); z < Mathf.Min(d - borderCells - half, z1); z += step)
                {
                    float h = heightSource.GetHeight(x, z);
                    if (h <= waterLevel) continue;

                    Zone zone = GetZone(x, z);
                    if (!CanGrow(layer, zone)) continue;

                    bool stable = (zone == Zone.Grove);
                    float roll = stable ? Hash01(x, z, vi, 0) : Random.value;
                    float dens = GetDensity(layer, zone);
                    if (roll > dens) continue;

                    float shareRoll = stable ? Hash01(x, z, vi, 1) : Random.value;
                    if (shareRoll > myChance) continue;

                    if (useGrid)
                    {
                        if (!mapGrid.CanPlace(x, z, fp, fp, anchorCenter: true)) continue;
                        if (mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Road)) continue;
                        if (FootprintHitsRoad(x, z, fp)) continue;
                    }

                    Vector3 pos;
                    if (layer.IsTree)
                    {
                        pos = origin + new Vector3((x + 0.5f) * ts, 0f, (z + 0.5f) * ts);
                        pos.y = h + v.heightOffset;
                    }
                    else
                    {
                        float m = ts * 0.15f;
                        float rx = stable ? Hash01(x, z, vi, 10) : Random.value;
                        float rz = stable ? Hash01(x, z, vi, 20) : Random.value;
                        pos = new Vector3(
                            origin.x + x * ts + Mathf.Lerp(m, ts - m, rx),
                            h + v.heightOffset,
                            origin.z + z * ts + Mathf.Lerp(m, ts - m, rz));
                    }

                    float scale = stable
                        ? Mathf.Lerp(v.minScale, v.maxScale, Hash01(x, z, vi, 30))
                        : Random.Range(v.minScale, v.maxScale);
                    Quaternion rot;
                    if (v.randomYRotation)
                    {
                        float yaw = stable ? Hash01(x, z, vi, 40) * 360f : Random.Range(0f, 360f);
                        rot = Quaternion.Euler(0f, yaw, 0f);
                    }
                    else
                        rot = v.sprite != null ? spriteRot : Quaternion.identity;

                    list.Add(Matrix4x4.TRS(pos, rot, Vector3.one * scale));

                    if (useGrid)
                    {
                        mapGrid.Occupy(x, z, fp, fp, MapGrid.OccupancyFlags.Tree, anchorCenter: true);
                        ResolveCover(layer, v, out var coverMode, out var coverH);
                        mapGrid.SetSightCover(x, z, fp, fp, coverMode, coverH, anchorCenter: true);
                    }
                }
            }

            v.sectors[key] = SplitToBatches(list);
        }
    }

    private void UnloadSector(int sx, int sz)
    {
        var key = new Vector2Int(sx, sz);
        int w = heightSource != null ? heightSource.width : 0;
        int d = heightSource != null ? heightSource.depth : 0;
        int x0 = sx * sectorSize;
        int z0 = sz * sectorSize;
        int x1 = Mathf.Min(w, x0 + sectorSize);
        int z1 = Mathf.Min(d, z0 + sectorSize);

        for (int vi = 0; vi < allVariants.Count; vi++)
        {
            var v = allVariants[vi];
            var layer = variantLayer[vi];
            if (v.sectorLists != null) v.sectorLists.Remove(key);
            if (v.sectors != null) v.sectors.Remove(key);

            if (layer.IsTree && mapGrid != null && mapGrid.IsReady)
            {
                int fp = Mathf.Max(1, layer.footprint);
                int half = fp / 2;
                for (int x = Mathf.Max(0, x0 - half); x < Mathf.Min(w, x1 + half); x++)
                {
                    for (int z = Mathf.Max(0, z0 - half); z < Mathf.Min(d, z1 + half); z++)
                    {
                        mapGrid.ClearOccupancy(x, z, 1, 1, MapGrid.OccupancyFlags.Tree, anchorCenter: false);
                        mapGrid.ClearSightCover(x, z, 1, 1, anchorCenter: false);
                    }
                }
            }
        }
    }

    private Zone GetZone(int x, int z)
    {
        float n = Mathf.PerlinNoise(
            (x + zoneNoiseOffset.x) * zoneNoiseScale,
            (z + zoneNoiseOffset.y) * zoneNoiseScale);
        if (n < groveThreshold) return Zone.Grove;
        if (n >= thicketThreshold) return Zone.Thicket;
        return Zone.Forest;
    }

    private static bool CanGrow(NatureLayer layer, Zone zone)
    {
        switch (zone)
        {
            case Zone.Grove: return layer.growInGrove;
            case Zone.Forest: return layer.growInForest;
            case Zone.Thicket: return layer.growInThicket;
            default: return false;
        }
    }

    public static void ResolveCover(NatureLayer layer, NatureVariant v,
        out MapGrid.SightCoverMode mode, out float height)
    {
        if (v != null && v.overrideCover)
        {
            mode = v.sightCover;
            height = v.maxCoverHeight;
        }
        else if (layer != null)
        {
            mode = layer.sightCover;
            height = layer.maxCoverHeight;
        }
        else
        {
            mode = MapGrid.SightCoverMode.None;
            height = 0f;
        }

        if (mode == MapGrid.SightCoverMode.MaxHeight && height <= 0f)
            mode = MapGrid.SightCoverMode.None;
    }

    private static float GetDensity(NatureLayer layer, Zone zone)
    {
        switch (zone)
        {
            case Zone.Grove: return layer.densityGrove;
            case Zone.Forest: return layer.densityForest;
            case Zone.Thicket: return layer.densityThicket;
            default: return 0f;
        }
    }

    private static float Hash01(int x, int z, int typeIndex, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + z * 668265263 + typeIndex * 1274126177 + salt * 374761393);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= (h >> 16);
            return (h & 0xFFFFFF) / 16777215f;
        }
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

    private bool ResolveParts(NatureVariant v)
    {
        if (v.parts == null) v.parts = new List<MeshPart>();
        else v.parts.Clear();
        v.resolvedMesh = null;

        if (v.prefab != null)
            CollectPrefabParts(v.prefab, v.parts);

        if (v.parts.Count == 0 && v.mesh != null && v.material != null)
            v.parts.Add(new MeshPart { mesh = v.mesh, material = v.material, localToRoot = Matrix4x4.identity, submesh = 0 });

        if (v.parts.Count == 0 && v.sprite != null && v.material != null)
        {
            v.resolvedMesh = BuildQuadFromSprite(v.sprite);
            v.parts.Add(new MeshPart { mesh = v.resolvedMesh, material = v.material, localToRoot = Matrix4x4.identity, submesh = 0 });
        }

        for (int i = v.parts.Count - 1; i >= 0; i--)
        {
            var p = v.parts[i];
            if (p.mesh == null || p.material == null)
            {
                v.parts.RemoveAt(i);
                continue;
            }
            EnsureInstancing(p.material);
        }

        if (v.parts.Count == 0) return false;
        v.resolvedMesh = v.parts[0].mesh;
        if (v.material == null) v.material = v.parts[0].material;
        return true;
    }

    private static void CollectPrefabParts(GameObject prefab, List<MeshPart> dst)
    {
        Transform root = prefab.transform;
        var lodGroup = prefab.GetComponentInChildren<LODGroup>();
        if (lodGroup != null)
        {
            var lods = lodGroup.GetLODs();
            if (lods != null && lods.Length > 0 && lods[0].renderers != null)
            {
                for (int i = 0; i < lods[0].renderers.Length; i++)
                    AddRendererParts(lods[0].renderers[i], root, dst);
                return;
            }
        }

        var renderers = prefab.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            AddRendererParts(renderers[i], root, dst);
    }

    private static void AddRendererParts(Renderer r, Transform root, List<MeshPart> dst)
    {
        if (r == null || r is ParticleSystemRenderer) return;

        Mesh mesh = null;
        if (r is MeshRenderer)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
        }
        else if (r is SkinnedMeshRenderer smr)
            mesh = smr.sharedMesh;

        if (mesh == null) return;

        Matrix4x4 local = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
        var mats = r.sharedMaterials;
        int subCount = Mathf.Max(1, mesh.subMeshCount);
        for (int s = 0; s < subCount; s++)
        {
            Material mat = null;
            if (mats != null && mats.Length > 0)
                mat = mats[Mathf.Min(s, mats.Length - 1)];
            if (mat == null) continue;
            dst.Add(new MeshPart
            {
                mesh = mesh,
                material = mat,
                localToRoot = local,
                submesh = s
            });
        }
    }

    private static void EnsureInstancing(Material mat)
    {
        if (mat != null && !mat.enableInstancing)
            mat.enableInstancing = true;
    }

    private Mesh BuildQuadFromSprite(Sprite s)
    {
        float wWorld = s.rect.width / s.pixelsPerUnit;
        float hWorld = s.rect.height / s.pixelsPerUnit;
        float hw = wWorld * 0.5f;
        Rect tr = s.textureRect;
        float tw = s.texture.width, th = s.texture.height;
        Vector2 uvMin = new Vector2(tr.xMin / tw, tr.yMin / th);
        Vector2 uvMax = new Vector2(tr.xMax / tw, tr.yMax / th);
        var mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-hw, 0, 0), new Vector3(hw, 0, 0),
            new Vector3(-hw, hWorld, 0), new Vector3(hw, hWorld, 0)
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(uvMin.x, uvMin.y), new Vector2(uvMax.x, uvMin.y),
            new Vector2(uvMin.x, uvMax.y), new Vector2(uvMax.x, uvMax.y)
        };
        mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.RecalculateNormals();
        return mesh;
    }

    private Matrix4x4[][] SplitToBatches(List<Matrix4x4> list)
    {
        int batchCount = (list.Count + BatchSize - 1) / BatchSize;
        var result = new Matrix4x4[batchCount][];
        for (int i = 0; i < batchCount; i++)
        {
            int start = i * BatchSize;
            int len = Mathf.Min(BatchSize, list.Count - start);
            result[i] = new Matrix4x4[len];
            list.CopyTo(start, result[i], 0, len);
        }
        return result;
    }

    private bool Validate()
    {
        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("NaturePlacement: HeightMapGenerator не готов!");
            return false;
        }
        if (terrainBuilder == null)
        {
            Debug.LogError("NaturePlacement: нет ChunkedTerrainBuilder!");
            return false;
        }
        return true;
    }
}
