using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

/// <summary>
/// Отдельное поле травы: меш только из допущенных клеток + материал Dynamic Grass FX.
/// Не слой NaturePlacement.Grass и не SpriteVegetationPlacer.
/// Чанки земли не меняет. Коллайдера нет.
/// </summary>
public class GrassField : MonoBehaviour
{
    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder terrainBuilder;
    public MapGrid mapGrid;
    public NaturePlacement naturePlacement;
    public RoadGenerator roadGenerator;

    [Header("Материал пакета")]
    [Tooltip("Материал из Dynamic Grass FX после Import.")]
    public Material grassMaterial;

    [Header("Поверхность")]
    public float heightOffset = 0.03f;
    public int skipBorderCells = 2;
    [Tooltip("Пропуск клетки, если размах высот углов больше этого (м).")]
    public float maxSlope = 1.6f;
    public int chunkSize = 10;
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
    [Tooltip("Строить и рисовать только в этом радиусе от игрока.")]
    public float drawRadius = 252f;
    public float pushRadius = 1.35f;
    public float pushStrength = 1.05f;
    public float recoverSeconds = 1.35f;
    public float minForwardSpeed = 1.2f;
    [Range(0f, 1f)] public float forwardDot = 0.25f;

    [Header("Отступ от тени / деревьев")]
    [Tooltip("Круг вокруг якоря дерева, не вырез клетки.")]
    public bool useTreeCircles = true;

    [Header("Вырезы")]
    public bool skipWater = true;
    public float waterLevel = 0f;
    public bool skipRoads = true;
    [Tooltip("Только клетки, уже помеченные Tree в MapGrid. На старте сектор деревьев ещё не загружен.")]
    public bool skipUnderTrees = true;

    [Header("Зоны (как Nature, если naturePlacement задан)")]
    public bool growInGrove = true;
    public bool growInForest = true;
    public bool growInThicket = true;
    [Range(0f, 1f)] public float coverage = 1f;

    [Header("Рендер")]
    public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
    public bool receiveShadows = true;

    public bool IsBuilt => _root != null && _root.childCount > 0;

    private Transform _root;
    private readonly List<Mesh> _owned = new List<Mesh>();
    private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
    private MaterialPropertyBlock _block;
    private const int StampCount = 8;
    private readonly Vector4[] _stamps = new Vector4[StampCount];
    private readonly Vector4[] _dirs = new Vector4[StampCount];
    private readonly float[] _stampBorn = new float[StampCount];
    private int _stampWrite;
    private Vector3 _lastPlayerPos;
    private bool _hasLastPlayer;
    private float _nextStampTime;
    private float[,] _open;
    private List<Vector2> _trees;
    private int _w, _d, _cs, _border, _div, _chunksX, _chunksZ;
    private float _ts;
    private Vector3 _origin;
    private bool _prepared;
    private readonly List<Vector3> _verts = new List<Vector3>(4096);
    private readonly List<int> _tris = new List<int>(4096);
    private readonly List<Vector2> _uvs = new List<Vector2>(4096);
    private readonly List<Color> _cols = new List<Color>(4096);

    [ContextMenu("Build")]
    public void Build()
    {
        if (!ResolveSources()) return;
        if (grassMaterial == null)
        {
            Debug.LogWarning("GrassField: нет grassMaterial — импортируйте Dynamic Grass FX и перетащите материал.");
            return;
        }

        Clear();
        if (!PrepareCache()) return;
        BakeAll();
    }

    [ContextMenu("Clear")]
    public void Clear()
    {
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

        for (int i = 0; i < _owned.Count; i++)
        {
            if (_owned[i] == null) continue;
            if (Application.isPlaying) Destroy(_owned[i]);
            else DestroyImmediate(_owned[i]);
        }
        _owned.Clear();
        _renderers.Clear();
        _prepared = false;
        _open = null;
    }

    private void LateUpdate()
    {
        if (_renderers.Count == 0) return;
        ResolvePlayer();
        TickStamps();
        FillBlock();
        for (int i = 0; i < _renderers.Count; i++)
        {
            if (_renderers[i] != null)
                _renderers[i].SetPropertyBlock(_block);
        }
    }

    private bool PrepareCache()
    {
        _w = heightSource.width;
        _d = heightSource.depth;
        _ts = terrainBuilder != null ? terrainBuilder.tileSize : 4f;
        _origin = new Vector3(-_w * _ts * 0.5f, 0f, -_d * _ts * 0.5f);
        _cs = Mathf.Max(1, terrainBuilder != null ? terrainBuilder.chunkSize : chunkSize);
        _border = Mathf.Max(0, skipBorderCells);
        _div = Mathf.Max(1, cellSubdiv);
        _chunksX = Mathf.CeilToInt((float)_w / _cs);
        _chunksZ = Mathf.CeilToInt((float)_d / _cs);
        EnsureRoot();
        if (_trees == null) _trees = new List<Vector2>(256);
        else _trees.Clear();
        CollectTreePoints(_trees, _w, _d, _ts, _origin);
        _open = BuildOpennessField();
        _prepared = true;
        FillBlock();
        return true;
    }

    private void BakeAll()
    {
        _verts.Clear();
        _tris.Clear();
        _uvs.Clear();
        _cols.Clear();

        for (int x = 0; x < _w; x++)
        {
            for (int z = 0; z < _d; z++)
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

                int x1c = Mathf.Min(x + 1, _w);
                int z1c = Mathf.Min(z + 1, _d);
                float o00 = _open[x, z];
                float o10 = _open[x1c, z];
                float o11 = _open[x1c, z1c];
                float o01 = _open[x, z1c];
                if (o00 + o10 + o11 + o01 < 0.02f) continue;

                float yOff = heightOffset;
                for (int sx = 0; sx < _div; sx++)
                {
                    for (int sz = 0; sz < _div; sz++)
                    {
                        float u0 = sx / (float)_div;
                        float v0 = sz / (float)_div;
                        float u1 = (sx + 1) / (float)_div;
                        float v1 = (sz + 1) / (float)_div;
                        int i = _verts.Count;
                        _verts.Add(new Vector3(_origin.x + (x + u0) * _ts, Hlerp(h00, h10, h01, h11, u0, v0) + yOff, _origin.z + (z + v0) * _ts));
                        _verts.Add(new Vector3(_origin.x + (x + u1) * _ts, Hlerp(h00, h10, h01, h11, u1, v0) + yOff, _origin.z + (z + v0) * _ts));
                        _verts.Add(new Vector3(_origin.x + (x + u1) * _ts, Hlerp(h00, h10, h01, h11, u1, v1) + yOff, _origin.z + (z + v1) * _ts));
                        _verts.Add(new Vector3(_origin.x + (x + u0) * _ts, Hlerp(h00, h10, h01, h11, u0, v1) + yOff, _origin.z + (z + v1) * _ts));
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

        if (_verts.Count == 0) return;

        var mesh = new Mesh();
        mesh.name = "GrassField";
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(_verts);
        mesh.SetTriangles(_tris, 0);
        mesh.SetUVs(0, _uvs);
        mesh.SetColors(_cols);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        _owned.Add(mesh);

        var go = new GameObject("GrassFieldMesh");
        go.transform.SetParent(_root, false);
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = grassMaterial;
        mr.SetPropertyBlock(_block);
        mr.shadowCastingMode = shadowCasting;
        mr.receiveShadows = receiveShadows;
        _renderers.Add(mr);
    }

    private void ResolvePlayer()
    {
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
        Vector3 p = player != null ? player.position : new Vector3(9999f, 0f, 9999f);
        _block.SetVector("_PlayerPos", p);
        _block.SetFloat("_DrawRadius", drawRadius);
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
        }

        if (coverage < 1f && Hash01(x, z, 71, 19) > coverage)
            return false;

        var zone = ResolveZone(x, z);
        switch (zone)
        {
            case NaturePlacement.Zone.Grove: return growInGrove;
            case NaturePlacement.Zone.Forest: return growInForest;
            case NaturePlacement.Zone.Thicket: return growInThicket;
            default: return true;
        }
    }

    private NaturePlacement.Zone ResolveZone(int x, int z)
    {
        float scale = naturePlacement != null ? naturePlacement.zoneNoiseScale : 0.028f;
        float grove = naturePlacement != null ? naturePlacement.groveThreshold : 0.28f;
        float thicket = naturePlacement != null ? naturePlacement.thicketThreshold : 0.72f;
        Vector2 off = naturePlacement != null ? naturePlacement.zoneNoiseOffset : new Vector2(17.3f, 91.7f);
        float n = Mathf.PerlinNoise((x + off.x) * scale, (z + off.y) * scale);
        if (n < grove) return NaturePlacement.Zone.Grove;
        if (n >= thicket) return NaturePlacement.Zone.Thicket;
        return NaturePlacement.Zone.Forest;
    }

    private bool IsTrunkCell(int x, int z)
    {
        if (mapGrid != null && mapGrid.IsReady)
        {
            if (mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Tree)) return true;
            if (mapGrid.GetSightCoverMode(x, z) == MapGrid.SightCoverMode.Full) return true;
        }
        return PredictTreeCell(x, z);
    }

    private void CollectTreePoints(List<Vector2> dst, int w, int d, float ts, Vector3 origin)
    {
        if (naturePlacement != null)
            naturePlacement.CollectTreeAnchors(dst);
        if (mapGrid == null || !mapGrid.IsReady) return;
        for (int x = 0; x < w; x++)
        {
            for (int z = 0; z < d; z++)
            {
                if (!mapGrid.HasFlag(x, z, MapGrid.OccupancyFlags.Tree)) continue;
                dst.Add(new Vector2(origin.x + (x + 0.5f) * ts, origin.z + (z + 0.5f) * ts));
            }
        }
    }

    private float[,] BuildOpennessField()
    {
        var dist = new float[_w + 1, _d + 1];
        for (int x = 0; x <= _w; x++)
            for (int z = 0; z <= _d; z++)
                dist[x, z] = 999f;

        var q = new Queue<Vector2Int>(_w * 4);
        if (useTreeCircles && _trees != null)
        {
            for (int i = 0; i < _trees.Count; i++)
            {
                int ix = Mathf.Clamp(Mathf.RoundToInt((_trees[i].x - _origin.x) / _ts), 0, _w);
                int iz = Mathf.Clamp(Mathf.RoundToInt((_trees[i].y - _origin.z) / _ts), 0, _d);
                if (dist[ix, iz] > 0f)
                {
                    dist[ix, iz] = 0f;
                    q.Enqueue(new Vector2Int(ix, iz));
                }
            }
        }

        int[] ox = { 1, -1, 0, 0 };
        int[] oz = { 0, 0, 1, -1 };
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            float nd = dist[c.x, c.y] + _ts;
            for (int k = 0; k < 4; k++)
            {
                int nx = c.x + ox[k];
                int nz = c.y + oz[k];
                if (nx < 0 || nz < 0 || nx > _w || nz > _d) continue;
                if (nd >= dist[nx, nz]) continue;
                dist[nx, nz] = nd;
                q.Enqueue(new Vector2Int(nx, nz));
            }
        }

        float inner = Mathf.Max(0.2f, trunkRadius);
        float fade = Mathf.Max(0.2f, fadeMeters);
        var open = new float[_w + 1, _d + 1];
        for (int x = 0; x <= _w; x++)
            for (int z = 0; z <= _d; z++)
                open[x, z] = Mathf.Clamp01((dist[x, z] - inner) / fade);
        return open;
    }

    private static Color Oc(float o)
    {
        o = Mathf.Clamp01(o);
        return new Color(o, o, o, 1f);
    }

    private bool PredictTreeCell(int x, int z)
    {
        if (naturePlacement == null || naturePlacement.layers == null) return false;
        var zone = ResolveZone(x, z);
        for (int i = 0; i < naturePlacement.layers.Count; i++)
        {
            var layer = naturePlacement.layers[i];
            if (layer == null || !layer.enabled || !layer.IsTree) continue;
            if (zone == NaturePlacement.Zone.Grove && !layer.growInGrove) continue;
            if (zone == NaturePlacement.Zone.Forest && !layer.growInForest) continue;
            if (zone == NaturePlacement.Zone.Thicket && !layer.growInThicket) continue;
            float dens = zone == NaturePlacement.Zone.Grove ? layer.densityGrove
                : zone == NaturePlacement.Zone.Thicket ? layer.densityThicket
                : layer.densityForest;
            if (Hash01(x, z, i, 0) <= dens) return true;
        }
        return false;
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

    private bool ResolveSources()
    {
        if (heightSource == null) heightSource = GetComponent<HeightMapGenerator>();
        if (terrainBuilder == null) terrainBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (mapGrid == null) mapGrid = GetComponent<MapGrid>();
        if (naturePlacement == null) naturePlacement = GetComponent<NaturePlacement>();
        if (roadGenerator == null) roadGenerator = GetComponent<RoadGenerator>();

        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("GrassField: HeightMap не готов.");
            return false;
        }
        return true;
    }

    private void EnsureRoot()
    {
        var existing = transform.Find("GrassFields");
        if (existing != null)
        {
            _root = existing;
            return;
        }
        var go = new GameObject("GrassFields");
        go.transform.SetParent(transform, false);
        _root = go.transform;
    }
}
