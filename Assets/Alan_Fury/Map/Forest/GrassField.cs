using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

/// <summary>
/// Поле травы чанками секторов, как NaturePlacement.
/// Меш сектора — только допущенные клетки + материал Dynamic Grass FX / Nature/GrassField.
/// Не слой NaturePlacement.Grass и не SpriteVegetationPlacer.
/// </summary>
public class GrassField : MonoBehaviour
{
    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder terrainBuilder;
    public MapGrid mapGrid;
    public NaturePlacement naturePlacement;

    [Header("Материал пакета")]
    [Tooltip("Материал из Dynamic Grass FX после Import.")]
    public Material grassMaterial;

    [Header("Поверхность")]
    public float heightOffset = 0.03f;
    public int skipBorderCells = 2;
    [Tooltip("Пропуск клетки, если размах высот углов больше этого (м).")]
    public float maxSlope = 1.6f;
    [Tooltip("На сколько резать клетку. 4 = квады ~1м, плотность шейдера не упирается в лимит GS.")]
    [Range(1, 6)] public int cellSubdiv = 2;

    [Header("Плотность / вид (пишется в материал)")]
    [Tooltip("Густота в центре открытого поля.")]
    [Range(1f, 32f)] public float centerBlades = 12f;
    [Tooltip("Густота сразу за кругом кроны.")]
    [Range(0f, 16f)] public float edgeBlades = 2f;
    [Tooltip("Радиус без травы вокруг ствола (м).")]
    public float trunkRadius = 5.2f;
    [Tooltip("За сколько метров густота доходит до центра поля.")]
    public float fadeMeters = 18f;
    public float bladeHeight = 0.85f;
    public float bladeWidth = 0.09f;
    public float windStrength = 0.14f;
    public float windSpeed = 0.35f;
    [Range(1f, 1.3f)] public float screenPad = 1.05f;

    [Header("Игрок")]
    public Transform player;
    [Tooltip("Как NatureRenderer.drawRadius, если matchTreeRadius.")]
    public float drawRadius = 55f;
    public bool matchTreeRadius = true;
    public NatureRenderer natureRenderer;
    public float pushRadius = 1.35f;
    public float pushStrength = 1.05f;
    public float recoverSeconds = 1.35f;
    public float minForwardSpeed = 1.2f;
    [Range(0f, 1f)] public float forwardDot = 0.25f;

    [Header("Стриминг")]
    [Tooltip("Как NatureRenderer.streamCheckEveryNFrames.")]
    public int streamCheckEveryNFrames = 20;
    [Tooltip("За сколько метров дальше drawRadius держать сектор.")]
    public float unloadExtra = 25f;

    [Header("Отступ от тени / деревьев")]
    [Tooltip("Круг вокруг якоря дерева, не вырез клетки.")]
    public bool useTreeCircles = true;

    [Header("Вырезы")]
    public bool skipWater = true;
    public float waterLevel = 0f;
    public bool skipRoads = true;
    [Tooltip("Клетка с флагом Tree в MapGrid. Сектор деревьев должен быть уже загружен.")]
    public bool skipUnderTrees = true;

    [Header("Зоны (как Nature, если naturePlacement задан)")]
    public bool growInGrove = true;
    public bool growInForest = true;
    public bool growInThicket = true;
    public bool growInClearing = true;
    [Range(0f, 1f)] public float densityGrove = 0.62f;
    [Range(0f, 1f)] public float densityForest = 0.22f;
    [Range(0f, 1f)] public float densityThicket = 0.06f;
    [Range(0f, 1f)] public float coverage = 1f;

    [Header("Рендер")]
    public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
    public bool receiveShadows = true;

    public bool IsBuilt => _sectors.Count > 0;

    private class SectorMesh
    {
        public Mesh mesh;
        public GameObject go;
        public MeshRenderer renderer;
        public int treeStamp;
    }

    private Transform _root;
    private readonly Dictionary<Vector2Int, SectorMesh> _sectors = new Dictionary<Vector2Int, SectorMesh>();
    private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
    private readonly List<Vector2Int> _toUnload = new List<Vector2Int>();
    private readonly List<Vector2> _sectorTrees = new List<Vector2>(64);
    private MaterialPropertyBlock _block;
    private const int StampCount = 8;
    private readonly Vector4[] _stamps = new Vector4[StampCount];
    private readonly Vector4[] _dirs = new Vector4[StampCount];
    private readonly float[] _stampBorn = new float[StampCount];
    private int _stampWrite;
    private Vector3 _lastPlayerPos;
    private bool _hasLastPlayer;
    private float _nextStampTime;
    private int _w, _d, _border, _div, _sectorSize;
    private float _ts;
    private Vector3 _origin;
    private bool _prepared;
    private readonly List<Vector3> _verts = new List<Vector3>(4096);
    private readonly List<int> _tris = new List<int>(4096);
    private readonly List<Vector2> _uvs = new List<Vector2>(4096);
    private readonly List<Color> _cols = new List<Color>(4096);

    private void Start()
    {
        TryPrepare();
    }

    [ContextMenu("Build")]
    public void Build()
    {
        if (!TryPrepare())
        {
            Debug.LogError("GrassField: HeightMap не готов.");
            return;
        }
        if (grassMaterial == null)
        {
            Debug.LogWarning("GrassField: нет grassMaterial — импортируйте Dynamic Grass FX и перетащите материал.");
            return;
        }
        UpdateStreaming();
    }

    [ContextMenu("Clear")]
    public void Clear()
    {
        _toUnload.Clear();
        foreach (var key in _sectors.Keys)
            _toUnload.Add(key);
        for (int i = 0; i < _toUnload.Count; i++)
            UnloadSector(_toUnload[i]);
        _sectors.Clear();
        _renderers.Clear();

        if (_root != null)
        {
            if (Application.isPlaying) Destroy(_root.gameObject);
            else DestroyImmediate(_root.gameObject);
            _root = null;
        }

        var leftover = transform.Find("GrassFields");
        if (leftover != null)
        {
            if (Application.isPlaying) Destroy(leftover.gameObject);
            else DestroyImmediate(leftover.gameObject);
        }

        _prepared = false;
    }

    private void LateUpdate()
    {
        if (!_prepared && !TryPrepare()) return;
        if (grassMaterial == null) return;

        ResolvePlayer();
        if (Time.frameCount % Mathf.Max(1, streamCheckEveryNFrames) == 0)
            UpdateStreaming();

        if (_renderers.Count == 0) return;
        TickStamps();
        FillBlock();
        // штампы стареют каждый кадр, но SetPropertyBlock на все секторы — дорого. Раз в 2 кадра достаточно.
        if ((Time.frameCount & 1) == 0)
        {
            for (int i = 0; i < _renderers.Count; i++)
            {
                if (_renderers[i] != null)
                    _renderers[i].SetPropertyBlock(_block);
            }
        }
    }

    private bool TryPrepare()
    {
        if (_prepared) return true;
        if (heightSource == null) heightSource = GetComponent<HeightMapGenerator>();
        if (terrainBuilder == null) terrainBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();
        if (naturePlacement == null) naturePlacement = GetComponent<NaturePlacement>();
        if (natureRenderer == null) natureRenderer = GetComponent<NatureRenderer>();
        if (heightSource == null || !heightSource.isGenerated) return false;

        _w = heightSource.width;
        _d = heightSource.depth;
        _ts = terrainBuilder != null ? terrainBuilder.tileSize : 4f;
        _origin = new Vector3(-_w * _ts * 0.5f, 0f, -_d * _ts * 0.5f);
        _border = Mathf.Max(0, skipBorderCells);
        _div = Mathf.Max(1, cellSubdiv);
        _sectorSize = naturePlacement != null ? Mathf.Max(1, naturePlacement.sectorSize) : 16;
        EnsureRoot();
        _prepared = true;
        return true;
    }

    private void UpdateStreaming()
    {
        if (!_prepared || grassMaterial == null) return;
        ResolvePlayer();
        if (player == null) return;

        if (naturePlacement != null && naturePlacement.IsReady)
            naturePlacement.UpdateStreaming(player.position);

        float sectorWorld = Mathf.Max(0.01f, _sectorSize * _ts);
        Vector3 playerLocal = terrainBuilder != null
            ? terrainBuilder.WorldToMapLocal(player.position)
            : player.position;
        int pcx = Mathf.FloorToInt((playerLocal.x - _origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((playerLocal.z - _origin.z) / sectorWorld);

        if (matchTreeRadius && natureRenderer != null)
            drawRadius = natureRenderer.drawRadius;
        float loadMeters = Mathf.Max(drawRadius, 40f);
        int loadR = Mathf.Max(1, Mathf.CeilToInt(loadMeters / sectorWorld));
        int unloadR = Mathf.Max(loadR + 1, Mathf.CeilToInt((loadMeters + unloadExtra) / sectorWorld));

        for (int sx = pcx - loadR; sx <= pcx + loadR; sx++)
        {
            for (int sz = pcz - loadR; sz <= pcz + loadR; sz++)
            {
                if (sx < 0 || sz < 0 || sx * _sectorSize >= _w || sz * _sectorSize >= _d) continue;
                var key = new Vector2Int(sx, sz);
                int stamp = TreeStamp(sx, sz);
                if (_sectors.TryGetValue(key, out var existing))
                {
                    if (existing.treeStamp == stamp) continue;
                    UnloadSector(key);
                }
                BakeSector(sx, sz, stamp);
            }
        }

        _toUnload.Clear();
        foreach (var key in _sectors.Keys)
        {
            if (Mathf.Abs(key.x - pcx) > unloadR || Mathf.Abs(key.y - pcz) > unloadR)
                _toUnload.Add(key);
        }
        for (int i = 0; i < _toUnload.Count; i++)
            UnloadSector(_toUnload[i]);
    }

    private void BakeSector(int sx, int sz, int treeStamp)
    {
        float t0 = Time.realtimeSinceStartup;
        int x0 = sx * _sectorSize;
        int z0 = sz * _sectorSize;
        int x1 = Mathf.Min(_w, x0 + _sectorSize);
        int z1 = Mathf.Min(_d, z0 + _sectorSize);
        CollectSectorTrees(x0, z0, x1, z1);

        _verts.Clear();
        _tris.Clear();
        _uvs.Clear();
        _cols.Clear();

        for (int x = x0; x < x1; x++)
        {
            for (int z = z0; z < z1; z++)
            {
                if (x < _border || z < _border || x >= _w - _border || z >= _d - _border)
                    continue;
                if (!AcceptCell(x, z)) continue;

                float h00 = heightSource.GetHeight(x, z);
                float h10 = heightSource.GetHeight(Mathf.Min(x + 1, _w - 1), z);
                float h11 = heightSource.GetHeight(Mathf.Min(x + 1, _w - 1), Mathf.Min(z + 1, _d - 1));
                float h01 = heightSource.GetHeight(x, Mathf.Min(z + 1, _d - 1));
                if (Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h11, h01)) - Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(h11, h01)) > maxSlope)
                    continue;

                float mapX = _origin.x + x * _ts;
                float mapZ = _origin.z + z * _ts;
                float o00 = OpenAt(mapX, mapZ);
                float o10 = OpenAt(mapX + _ts, mapZ);
                float o11 = OpenAt(mapX + _ts, mapZ + _ts);
                float o01 = OpenAt(mapX, mapZ + _ts);
                float zm = Mathf.Lerp(0.35f, 1f, GrassDensity(ResolveZone(x, z)));
                o00 *= zm; o10 *= zm; o11 *= zm; o01 *= zm;
                if (o00 + o10 + o11 + o01 < 0.02f) continue;

                float yOff = heightOffset;
                for (int ix = 0; ix < _div; ix++)
                {
                    for (int iz = 0; iz < _div; iz++)
                    {
                        float u0 = ix / (float)_div;
                        float v0 = iz / (float)_div;
                        float u1 = (ix + 1) / (float)_div;
                        float v1 = (iz + 1) / (float)_div;
                        int i = _verts.Count;
                        _verts.Add(ToMesh(mapX + u0 * _ts, Hlerp(h00, h10, h01, h11, u0, v0) + yOff, mapZ + v0 * _ts));
                        _verts.Add(ToMesh(mapX + u1 * _ts, Hlerp(h00, h10, h01, h11, u1, v0) + yOff, mapZ + v0 * _ts));
                        _verts.Add(ToMesh(mapX + u1 * _ts, Hlerp(h00, h10, h01, h11, u1, v1) + yOff, mapZ + v1 * _ts));
                        _verts.Add(ToMesh(mapX + u0 * _ts, Hlerp(h00, h10, h01, h11, u0, v1) + yOff, mapZ + v1 * _ts));
                        _uvs.Add(new Vector2(u0, v0));
                        _uvs.Add(new Vector2(u1, v0));
                        _uvs.Add(new Vector2(u1, v1));
                        _uvs.Add(new Vector2(u0, v1));
                        _cols.Add(Oc(Hlerp(o00, o10, o01, o11, u0, v0)));
                        _cols.Add(Oc(Hlerp(o00, o10, o01, o11, u1, v0)));
                        _cols.Add(Oc(Hlerp(o00, o10, o01, o11, u1, v1)));
                        _cols.Add(Oc(Hlerp(o00, o10, o01, o11, u0, v1)));
                        _tris.Add(i); _tris.Add(i + 3); _tris.Add(i + 2);
                        _tris.Add(i); _tris.Add(i + 2); _tris.Add(i + 1);
                    }
                }
            }
        }

        var entry = new SectorMesh { treeStamp = treeStamp };
        if (_verts.Count > 0)
        {
            var mesh = new Mesh();
            mesh.name = "GrassField_" + sx + "_" + sz;
            mesh.indexFormat = _verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_verts);
            mesh.SetTriangles(_tris, 0);
            mesh.SetUVs(0, _uvs);
            mesh.SetColors(_cols);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(mesh.name);
            go.transform.SetParent(_root, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = grassMaterial;
            mr.shadowCastingMode = shadowCasting;
            mr.receiveShadows = receiveShadows;
            if (_block != null) mr.SetPropertyBlock(_block);
            _renderers.Add(mr);

            entry.mesh = mesh;
            entry.go = go;
            entry.renderer = mr;
        }
        _sectors[new Vector2Int(sx, sz)] = entry;
        float ms = (Time.realtimeSinceStartup - t0) * 1000f;
        Debug.Log($"[AF.Load] GrassField.BakeSector {sx},{sz} {ms:0.0}ms verts={_verts.Count}");
    }

    private void UnloadSector(Vector2Int key)
    {
        if (!_sectors.TryGetValue(key, out var s)) return;
        if (s.renderer != null) _renderers.Remove(s.renderer);
        if (s.go != null)
        {
            if (Application.isPlaying) Destroy(s.go);
            else DestroyImmediate(s.go);
        }
        if (s.mesh != null)
        {
            if (Application.isPlaying) Destroy(s.mesh);
            else DestroyImmediate(s.mesh);
        }
        _sectors.Remove(key);
    }

    private int TreeStamp(int sx, int sz)
    {
        if (!useTreeCircles || mapGrid == null || !mapGrid.IsReady) return 0;
        int x0 = sx * _sectorSize;
        int z0 = sz * _sectorSize;
        int margin = Mathf.CeilToInt((trunkRadius + fadeMeters) / Mathf.Max(0.01f, _ts));
        int xa = Mathf.Max(0, x0 - margin);
        int za = Mathf.Max(0, z0 - margin);
        int xb = Mathf.Min(_w - 1, x0 + _sectorSize + margin);
        int zb = Mathf.Min(_d - 1, z0 + _sectorSize + margin);
        int n = 0;
        for (int x = xa; x <= xb; x++)
            for (int z = za; z <= zb; z++)
                if (mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Tree))
                    n++;
        return n;
    }

    private void CollectSectorTrees(int x0, int z0, int x1, int z1)
    {
        _sectorTrees.Clear();
        if (!useTreeCircles) return;

        float margin = trunkRadius + fadeMeters;
        if (naturePlacement != null && naturePlacement.IsReady && naturePlacement.allVariants != null)
        {
            float sectorWorld = Mathf.Max(0.01f, _sectorSize * _ts);
            int ms = Mathf.CeilToInt(margin / sectorWorld);
            int sx0 = x0 / _sectorSize - ms;
            int sz0 = z0 / _sectorSize - ms;
            int sx1 = (x1 - 1) / _sectorSize + ms;
            int sz1 = (z1 - 1) / _sectorSize + ms;
            var variants = naturePlacement.allVariants;
            var layers = naturePlacement.variantLayer;
            for (int vi = 0; vi < variants.Count; vi++)
            {
                if (layers == null || vi >= layers.Count || !layers[vi].IsTree) continue;
                var lists = variants[vi].sectorLists;
                if (lists == null) continue;
                for (int sx = sx0; sx <= sx1; sx++)
                {
                    for (int sz = sz0; sz <= sz1; sz++)
                    {
                        if (!lists.TryGetValue(new Vector2Int(sx, sz), out var list) || list == null) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            Vector3 p = list[i].GetColumn(3);
                            _sectorTrees.Add(new Vector2(p.x, p.z));
                        }
                    }
                }
            }
        }

        if (_sectorTrees.Count > 0 || mapGrid == null || !mapGrid.IsReady) return;
        int m = Mathf.CeilToInt(margin / Mathf.Max(0.01f, _ts));
        int xa = Mathf.Max(0, x0 - m);
        int za = Mathf.Max(0, z0 - m);
        int xb = Mathf.Min(_w - 1, x1 + m);
        int zb = Mathf.Min(_d - 1, z1 + m);
        for (int x = xa; x <= xb; x++)
        {
            for (int z = za; z <= zb; z++)
            {
                if (!mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Tree)) continue;
                Vector3 w = WorldOf(_origin.x + (x + 0.5f) * _ts, 0f, _origin.z + (z + 0.5f) * _ts);
                _sectorTrees.Add(new Vector2(w.x, w.z));
            }
        }
    }

    private float OpenAt(float mapX, float mapZ)
    {
        if (!useTreeCircles || _sectorTrees.Count == 0) return 1f;
        Vector3 w = WorldOf(mapX, 0f, mapZ);
        float best = float.MaxValue;
        for (int i = 0; i < _sectorTrees.Count; i++)
        {
            float dx = w.x - _sectorTrees[i].x;
            float dz = w.z - _sectorTrees[i].y;
            float d2 = dx * dx + dz * dz;
            if (d2 < best) best = d2;
        }
        float dist = Mathf.Sqrt(best);
        float inner = Mathf.Max(0.2f, trunkRadius);
        float fade = Mathf.Max(0.2f, fadeMeters);
        return Mathf.Clamp01((dist - inner) / fade);
    }

    private Vector3 WorldOf(float x, float y, float z)
    {
        return terrainBuilder != null
            ? terrainBuilder.MapLocalToWorld(new Vector3(x, y, z))
            : new Vector3(x, y, z);
    }

    private void ResolvePlayer()
    {
        if (natureRenderer == null) natureRenderer = GetComponent<NatureRenderer>();
        if (natureRenderer != null && natureRenderer.player != null)
        {
            player = natureRenderer.player;
            return;
        }
        if (player != null) return;
        player = PlayerRegistry.ResolvePrimary();
    }

    private void TickStamps()
    {
        float now = Time.time;
        float rec = Mathf.Max(0.05f, recoverSeconds);
        if (player == null)
        {
            _hasLastPlayer = false;
            return;
        }

        Vector3 pos = player.position;
        if (_hasLastPlayer)
        {
            Vector3 delta = pos - _lastPlayerPos;
            delta.y = 0f;
            float speed = delta.magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 face = player.forward; face.y = 0f;
            if (face.sqrMagnitude < 0.001f) face = delta;
            face.Normalize();
            bool forward = speed >= minForwardSpeed && Vector3.Dot(delta.normalized, face) >= forwardDot;
            if (forward && now >= _nextStampTime)
            {
                _stamps[_stampWrite] = new Vector4(pos.x, pos.y, pos.z, 1f);
                _dirs[_stampWrite] = new Vector4(face.x, 0f, face.z, 0f);
                _stampBorn[_stampWrite] = now;
                _stampWrite = (_stampWrite + 1) % StampCount;
                _nextStampTime = now + 0.08f;
            }
        }
        _lastPlayerPos = pos;
        _hasLastPlayer = true;

        for (int i = 0; i < StampCount; i++)
        {
            float age = now - _stampBorn[i];
            float w = (_stampBorn[i] <= 0f) ? 0f : 1f - Mathf.Clamp01(age / rec);
            w = w * w;
            var s = _stamps[i];
            s.w = w;
            _stamps[i] = s;
        }
    }

    private void FillBlock()
    {
        if (_block == null) _block = new MaterialPropertyBlock();
        _block.SetFloat("_CenterDensity", centerBlades);
        _block.SetFloat("_EdgeDensity", edgeBlades);
        _block.SetFloat("_BladeHeight", bladeHeight);
        _block.SetFloat("_BladeWidth", bladeWidth);
        _block.SetFloat("_ScreenPad", screenPad);
        _block.SetFloat("_WindStrength", windStrength);
        _block.SetFloat("_WindSpeed", windSpeed);
        _block.SetFloat("_PushRadius", pushRadius);
        _block.SetFloat("_PushStrength", pushStrength);
        if (matchTreeRadius)
        {
            if (natureRenderer == null) natureRenderer = GetComponent<NatureRenderer>();
            if (natureRenderer != null) drawRadius = natureRenderer.drawRadius;
        }
        ResolvePlayer();
        Vector3 p;
        if (player != null) p = player.position;
        else if (Camera.main != null) p = Camera.main.transform.position;
        else p = Vector3.zero;
        _block.SetVector("_PlayerPos", p);
        _block.SetFloat("_DrawRadius", Mathf.Max(drawRadius, 40f));
        _block.SetVectorArray("_PushStamps", _stamps);
        _block.SetVectorArray("_PushDirs", _dirs);
    }

    private bool AcceptCell(int x, int z)
    {
        float h = heightSource.GetHeight(x, z);
        if (skipWater && h <= waterLevel) return false;

        if (mapGrid != null && mapGrid.IsReady)
        {
            if (skipRoads && mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Road))
                return false;
            if (skipUnderTrees && mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Tree))
                return false;
        }

        var zone = ResolveZone(x, z);
        if (!ZoneAllowsGrass(zone)) return false;
        if (coverage < 1f && Hash01(x, z, 71, 19) > coverage) return false;
        return true;
    }

    private bool ZoneAllowsGrass(NaturePlacement.Zone zone)
    {
        switch (zone)
        {
            case NaturePlacement.Zone.Grove: return growInGrove;
            case NaturePlacement.Zone.Forest: return growInForest;
            case NaturePlacement.Zone.Thicket: return growInThicket;
            case NaturePlacement.Zone.Clearing: return growInClearing;
            default: return false;
        }
    }

    private float GrassDensity(NaturePlacement.Zone zone)
    {
        switch (zone)
        {
            case NaturePlacement.Zone.Grove: return densityGrove;
            case NaturePlacement.Zone.Forest: return densityForest;
            case NaturePlacement.Zone.Thicket: return densityThicket;
            case NaturePlacement.Zone.Clearing: return densityGrove;
            default: return 0f;
        }
    }

    private NaturePlacement.Zone ResolveZone(int x, int z)
    {
        if (naturePlacement == null) naturePlacement = GetComponent<NaturePlacement>();
        if (naturePlacement != null) return naturePlacement.GetZone(x, z);
        return NaturePlacement.Zone.Forest;
    }

    private static Color Oc(float o)
    {
        o = Mathf.Clamp01(o);
        return new Color(o, o, o, 1f);
    }

    private static float Hlerp(float h00, float h10, float h01, float h11, float u, float v)
    {
        float a = Mathf.Lerp(h00, h10, u);
        float b = Mathf.Lerp(h01, h11, u);
        return Mathf.Lerp(a, b, v);
    }

    private static float Hash01(int x, int z, int a, int b)
    {
        uint h = (uint)(x * 374761393 + z * 668265263 + a * 1274126177 + b);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) * (1f / 4294967295f);
    }

    private void EnsureRoot()
    {
        var existing = transform.Find("GrassFields");
        if (existing != null)
            _root = existing;
        else
        {
            var go = new GameObject("GrassFields");
            go.transform.SetParent(transform, false);
            _root = go.transform;
        }
        _root.localPosition = Vector3.zero;
        _root.localRotation = Quaternion.identity;
        _root.localScale = Vector3.one;
    }

    private Vector3 ToMesh(float x, float y, float z)
    {
        Vector3 world = WorldOf(x, y, z);
        return _root != null ? _root.InverseTransformPoint(world) : world;
    }
}
