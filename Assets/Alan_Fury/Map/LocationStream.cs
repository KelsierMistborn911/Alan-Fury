using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Стриминг местности от гексагональной карты регионов.
/// Карта не квадратная и не фиксированного размера — чанки строятся вокруг игрока.
/// Высота — органические террасы: клетка целиком плоская, перепад только на границе клеток.
/// Сторона гекса = 100 м. Деревья/трава не хранятся — генерируются заново.
/// </summary>
[DefaultExecutionOrder(-20)]
public class LocationStream : MonoBehaviour, ICellGround
{
    [Header("Игрок")]
    public Transform player;

    [Header("Сетка")]
    public int seed = 42;
    public float tileSize = 4f;
    public float hexSize = 200f;          // сторона гекса в метрах (~400 м в ширину)
    public int chunkCells = 16;

    [Header("Окно стрима")]
    public int loadRadiusChunks = 3;
    public int unloadRadiusChunks = 5;
    public int chunksPerFrame = 2;

    [Header("Рельеф")]
    public float heightStep = 0.25f;
    public float forestBase = 2.2f;
    public float forestVariation = 0.4f;
    public float terraceAmp = 2.2f;        // редкие холмы и понижения
    public float hexBlend = 0.55f;         // плавность перехода к соседнему гексу
    public int minTerraceCells = 10;       // площадка меньше — прилипает
    public float roadMaxStep = 0.1f;       // макс. подъём дороги
    public float roadStepDist = 10f;       // на сколько метров

    [Header("Связи")]
    public NaturePlacement nature;
    public ChunkedTerrainBuilder chunkBuilder;
    public bool driveWorld = true;

    public string CurrentPoi { get; private set; }
    public Vector3 CellOrigin => Vector3.zero;   // мир непрерывный, origin = 0
    public int CurrentHexQ { get; private set; }
    public int CurrentHexR { get; private set; }

    private readonly Dictionary<Vector2Int, Chunk> _chunks = new Dictionary<Vector2Int, Chunk>(64);
    private readonly List<Vector2Int> _drop = new List<Vector2Int>(32);
    private readonly Queue<Vector2Int> _queue = new Queue<Vector2Int>(32);
    private readonly HashSet<Vector2Int> _queued = new HashSet<Vector2Int>();
    private Transform _root;
    private MapGrid _grid;
    private int _pcx = int.MinValue;
    private int _pcz = int.MinValue;
    private bool _begun;
    private float _holdHexUntil;

    // кэш высот гексов (q,r) → height
    private readonly Dictionary<long, float> _hexHeights = new Dictionary<long, float>(128);

    private class Chunk
    {
        public GameObject go;
        public int sx, sz;
    }

    public void Begin()
    {
        if (_begun) return;
        _begun = true;

        if (player == null)
        {
            var move = FindObjectOfType<PlayerMovement3D>();
            if (move != null) player = move.transform;
        }
        if (nature == null)
        {
            var leshiy = FindObjectOfType<Leshiy>();
            if (leshiy != null)
            {
                leshiy.EnsureDrawObject();
                nature = leshiy.placement;
            }
        }
        if (nature == null) nature = GetComponent<NaturePlacement>();
        if (chunkBuilder == null) chunkBuilder = GetComponent<ChunkedTerrainBuilder>();
        if (chunkBuilder == null) chunkBuilder = GetComponentInParent<ChunkedTerrainBuilder>();
        if (chunkBuilder == null) chunkBuilder = GetComponentInChildren<ChunkedTerrainBuilder>();
        if (chunkBuilder == null) chunkBuilder = FindObjectOfType<ChunkedTerrainBuilder>();
        if (nature != null)
        {
            nature.streamGround = this;
            if (!nature.IsReady) nature.Init();
        }

        Debug.Log($"LocationStream.Begin request={HexSpawnRequest.HasRequest} q={CurrentHexQ} r={CurrentHexR}");

        // если пришли из RegionMap — берём сид и стартовый гекс
        HexSpawnRequest.TryRestore();
        if (HexSpawnRequest.HasRequest)
        {
            seed = HexSpawnRequest.Seed;
            hexSize = HexSpawnRequest.HexWorldSize > 1f ? HexSpawnRequest.HexWorldSize : 100f;
            CurrentHexQ = HexSpawnRequest.Q;
            CurrentHexR = HexSpawnRequest.R;
            RegionLayout.Build(seed);
            Debug.Log($"LocationStream: с карты региона → гекс {CurrentHexQ},{CurrentHexR}");
        }
        else
        {
            RegionLayout.Build(seed);
            CurrentHexQ = RegionLayout.Village.x;
            CurrentHexR = RegionLayout.Village.y;
        }

        // сначала земля под ногами, потом игрок
        var center = HexToWorld(CurrentHexQ, CurrentHexR);
        BuildChunksAround(center);
        EnsureGrid(center);
        Debug.Log($"LocationStream: чанков {_chunks.Count}, старт {CurrentHexQ},{CurrentHexR}, builder={(chunkBuilder != null)}");
        SnapToHex(CurrentHexQ, CurrentHexR);
        if (HexSpawnRequest.HasRequest)
        {
            _holdHexUntil = Time.time + 2f;
            // дорожный PlayerSpawnPoint не должен перебить центр
            var points = FindObjectsOfType<PlayerSpawnPoint>();
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i] == null) continue;
                points[i].playOnStart = false;
                points[i].enabled = false;
            }
        }

        UpdatePoi();
    }

    void EnsureGrid(Vector3 world)
    {
        if (_grid == null) _grid = FindObjectOfType<MapGrid>();
        if (_grid == null) return;
        _grid.streamGround = this;
        if (!_grid.IsReady || !_grid.Streaming)
            _grid.BuildStream(world);
    }

    void BuildChunksAround(Vector3 world)
    {
        int cx = Mathf.FloorToInt(world.x / tileSize);
        int cz = Mathf.FloorToInt(world.z / tileSize);
        int cs = Mathf.Max(4, chunkCells);
        int pcx = Mathf.FloorToInt(cx / (float)cs);
        int pcz = Mathf.FloorToInt(cz / (float)cs);
        _pcx = pcx;
        _pcz = pcz;
        int r = Mathf.Max(1, loadRadiusChunks);
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dz * dz > r * r + r) continue;
                int sx = pcx + dx;
                int sz = pcz + dz;
                var key = new Vector2Int(sx, sz);
                if (_chunks.ContainsKey(key)) continue;
                BuildChunk(sx, sz);
                _queued.Remove(key);
            }
    }

    /// <summary>Точка внутри кольца чанков, которое печатается вокруг игрока.</summary>
    public bool IsTerrainPrinted(Vector3 world, Vector3 around)
    {
        int cs = Mathf.Max(4, chunkCells);
        int pcx = Mathf.FloorToInt(Mathf.FloorToInt(around.x / tileSize) / (float)cs);
        int pcz = Mathf.FloorToInt(Mathf.FloorToInt(around.z / tileSize) / (float)cs);
        int wx = Mathf.FloorToInt(Mathf.FloorToInt(world.x / tileSize) / (float)cs);
        int wz = Mathf.FloorToInt(Mathf.FloorToInt(world.z / tileSize) / (float)cs);
        int dx = wx - pcx;
        int dz = wz - pcz;
        int r = Mathf.Max(1, loadRadiusChunks);
        return dx * dx + dz * dz <= r * r + r;
    }

    /// <summary>Чанк под точкой уже построен, не только в радиусе печати.</summary>
    public bool HasBuiltChunk(Vector3 world)
    {
        int cs = Mathf.Max(4, chunkCells);
        int sx = Mathf.FloorToInt(Mathf.FloorToInt(world.x / tileSize) / (float)cs);
        int sz = Mathf.FloorToInt(Mathf.FloorToInt(world.z / tileSize) / (float)cs);
        return _chunks != null && _chunks.ContainsKey(new Vector2Int(sx, sz));
    }

    /// <summary>Построить чанки вокруг точки (логово, спавн вдали от игрока).</summary>
    public void EnsureAround(Vector3 world)
    {
        if (!_begun) Begin();
        BuildChunksAround(world);
    }

    void Start()
    {
        if (driveWorld) Begin();
    }

    void LateUpdate()
    {
        if (_holdHexUntil <= 0f) return;
        if (Time.time < _holdHexUntil)
        {
            SnapToHex(CurrentHexQ, CurrentHexR);
            return;
        }
        _holdHexUntil = 0f;
        if (HexSpawnRequest.HasRequest)
            HexSpawnRequest.Clear();
    }

    void Update()
    {
        if (!_begun || player == null) return;

        // в каком гексе сейчас
        var hex = WorldToHex(player.position);
        if (hex.x != CurrentHexQ || hex.y != CurrentHexR)
        {
            CurrentHexQ = hex.x;
            CurrentHexR = hex.y;
            UpdatePoi();
        }

        // сдвиг окна MapGrid при необходимости
        if (_grid == null)
        {
            _grid = FindObjectOfType<MapGrid>();
            if (_grid == null) _grid = GetComponent<MapGrid>();
        }
        if (_grid != null && _grid.Streaming)
            _grid.UpdateStream(player.position);

        // чанки вокруг игрока (в клетках)
        int cx = Mathf.FloorToInt(player.position.x / tileSize);
        int cz = Mathf.FloorToInt(player.position.z / tileSize);
        int cs = Mathf.Max(4, chunkCells);
        int pcx = Mathf.FloorToInt(cx / (float)cs);
        int pcz = Mathf.FloorToInt(cz / (float)cs);

        if (pcx != _pcx || pcz != _pcz)
        {
            _pcx = pcx;
            _pcz = pcz;
            EnqueueRing(pcx, pcz);
            DropFar(pcx, pcz);
        }

        int budget = Mathf.Max(1, chunksPerFrame);
        while (budget-- > 0 && _queue.Count > 0)
        {
            var key = _queue.Dequeue();
            _queued.Remove(key);
            if (_chunks.ContainsKey(key)) continue;
            BuildChunk(key.x, key.y);
        }
    }

    // ---------- высота и зоны от гексов ----------

    public float Height(int x, int z)
    {
        // высота клетки — все четыре угла одинаковые
        float wx = (x + 0.5f) * tileSize;
        float wz = (z + 0.5f) * tileSize;
        return TerraceAt(wx, wz);
    }

    public float HeightAtWorld(float wx, float wz)
    {
        int x = Mathf.FloorToInt(wx / tileSize);
        int z = Mathf.FloorToInt(wz / tileSize);
        return Height(x, z);
    }

    float TerraceAt(float wx, float wz)
    {
        // база — бленд высот своего гекса и соседей, переход плавный
        float baseH = BlendHexHeight(wx, wz);
        // внутри гекса рельеф гуляет крупно, не мелко
        float n = Noise(wx, wz, 0.0045f, 3f);
        float h = baseH;
        if (n > 0.68f)
            h += (n - 0.68f) * terraceAmp * 3f;
        else if (n < 0.32f)
            h -= (0.32f - n) * terraceAmp * 2.4f;
        if (IsRoadWorld(wx, wz))
            h = RoadHeightAt(wx, wz);
        if (heightStep > 0.001f)
            h = Mathf.Round(h / heightStep) * heightStep;
        return Mathf.Max(0.1f, h);
    }

    struct RoadSample
    {
        public float x, z, h, dist;
    }

    List<RoadSample> _roadSamples;
    bool _roadSamplesReady;

    float NaturalHeight(float wx, float wz)
    {
        float baseH = BlendHexHeight(wx, wz);
        float n = Noise(wx, wz, 0.0045f, 3f);
        float h = baseH;
        if (n > 0.68f)
            h += (n - 0.68f) * terraceAmp * 3f;
        else if (n < 0.32f)
            h -= (0.32f - n) * terraceAmp * 2.4f;
        return h;
    }

    void EnsureRoadSamples()
    {
        if (_roadSamplesReady) return;
        _roadSamplesReady = true;
        _roadSamples = new List<RoadSample>();

        var line = RegionLayout.RoadLine(hexSize);
        if (line == null || line.Count < 2) return;

        float step = Mathf.Max(1f, roadStepDist);
        float distAlong = 0f;
        float nextAt = 0f;

        void Add(float x, float z, float dist)
        {
            _roadSamples.Add(new RoadSample { x = x, z = z, dist = dist });
        }

        Add(line[0].x, line[0].z, 0f);

        for (int i = 0; i < line.Count - 1; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            float segLen = Mathf.Sqrt((b.x - a.x) * (b.x - a.x) + (b.z - a.z) * (b.z - a.z));
            if (segLen < 0.001f) continue;
            float segStart = distAlong;
            distAlong += segLen;
            while (nextAt <= distAlong + 0.001f)
            {
                if (nextAt > segStart + 0.001f)
                {
                    float t = (nextAt - segStart) / segLen;
                    Add(Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.z, b.z, t), nextAt);
                }
                nextAt += step;
            }
        }

        var last = line[line.Count - 1];
        if (_roadSamples.Count == 0 ||
            (last.x - _roadSamples[_roadSamples.Count - 1].x) * (last.x - _roadSamples[_roadSamples.Count - 1].x) +
            (last.z - _roadSamples[_roadSamples.Count - 1].z) * (last.z - _roadSamples[_roadSamples.Count - 1].z) > 1f)
            Add(last.x, last.z, distAlong);

        int n = _roadSamples.Count;
        if (n == 0) return;

        float[] targets = new float[n];
        for (int i = 0; i < n; i++)
            targets[i] = NaturalHeight(_roadSamples[i].x, _roadSamples[i].z);

        float[] h = new float[n];
        h[0] = targets[0];
        for (int i = 1; i < n; i++)
        {
            float dd = _roadSamples[i].dist - _roadSamples[i - 1].dist;
            float md = roadMaxStep * (dd / step);
            h[i] = Mathf.Clamp(targets[i], h[i - 1] - md, h[i - 1] + md);
        }
        for (int i = n - 2; i >= 0; i--)
        {
            float dd = _roadSamples[i + 1].dist - _roadSamples[i].dist;
            float md = roadMaxStep * (dd / step);
            h[i] = Mathf.Clamp(h[i], h[i + 1] - md, h[i + 1] + md);
        }
        for (int i = 0; i < n; i++)
        {
            var s = _roadSamples[i];
            s.h = h[i];
            _roadSamples[i] = s;
        }
    }

    float RoadHeightAt(float wx, float wz)
    {
        EnsureRoadSamples();
        if (_roadSamples == null || _roadSamples.Count == 0)
            return NaturalHeight(wx, wz);
        if (_roadSamples.Count == 1)
            return _roadSamples[0].h;

        float bestD2 = float.MaxValue;
        float bestH = _roadSamples[0].h;
        for (int i = 0; i < _roadSamples.Count - 1; i++)
        {
            var a = _roadSamples[i];
            var b = _roadSamples[i + 1];
            float abx = b.x - a.x, abz = b.z - a.z;
            float apx = wx - a.x, apz = wz - a.z;
            float ab2 = abx * abx + abz * abz;
            float t = ab2 > 0.0001f ? Mathf.Clamp01((apx * abx + apz * abz) / ab2) : 0f;
            float cx = a.x + abx * t;
            float cz = a.z + abz * t;
            float d2 = (wx - cx) * (wx - cx) + (wz - cz) * (wz - cz);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestH = Mathf.Lerp(a.h, b.h, t);
            }
        }
        return bestH;
    }

    float BlendHexHeight(float wx, float wz)
    {
        var center = WorldToHex(new Vector3(wx, 0f, wz));
        float sum = 0f;
        float wsum = 0f;
        AccumulateHex(center.x, center.y, wx, wz, ref sum, ref wsum);
        for (int i = 0; i < 6; i++)
        {
            var n = HexNeighbor(center.x, center.y, i);
            AccumulateHex(n.x, n.y, wx, wz, ref sum, ref wsum);
        }
        return wsum > 0.001f ? sum / wsum : HexHeight(center.x, center.y);
    }

    void AccumulateHex(int q, int r, float wx, float wz, ref float sum, ref float wsum)
    {
        var c = HexToWorld(q, r);
        float dx = wx - c.x;
        float dz = wz - c.z;
        float d2 = dx * dx + dz * dz;
        float span = hexSize * hexSize * Mathf.Max(0.15f, hexBlend);
        float w = 1f / (1f + d2 / span);
        sum += HexHeight(q, r) * w;
        wsum += w;
    }

    bool IsRoadWorld(float wx, float wz)
    {
        int x = Mathf.FloorToInt(wx / tileSize);
        int z = Mathf.FloorToInt(wz / tileSize);
        return IsRoad(x, z);
    }

    public float HexHeight(int q, int r)
    {
        long key = HexKey(q, r);
        if (_hexHeights.TryGetValue(key, out float h)) return h;

        // та же формула, что в RegionHexMap
        var rnd = new System.Random(SeedFor(q, r));
        float raw = forestBase + ((float)rnd.NextDouble() - 0.5f) * forestVariation;
        h = Mathf.Max(heightStep, Mathf.Round(raw / heightStep) * heightStep);
        _hexHeights[key] = h;
        return h;
    }

    public NaturePlacement.Zone ZoneAt(int x, int z)
    {
        float wx = x * tileSize;
        float wz = z * tileSize;
        var hex = WorldToHex(new Vector3(wx, 0f, wz));
        var zone = RegionLayout.ZoneForHex(hex.x, hex.y);
        // лес: вкрапления чащи
        if (zone == NaturePlacement.Zone.Forest)
        {
            float n = Noise(wx, wz, 0.004f, 9f);
            if (n > 0.72f) return NaturePlacement.Zone.Thicket;
        }
        return zone;
    }

    public float DensityAt(int x, int z)
    {
        float wx = x * tileSize;
        float wz = z * tileSize;
        var hex = WorldToHex(new Vector3(wx, 0f, wz));
        return RegionLayout.DensityScale(hex.x, hex.y);
    }

    public bool IsRoad(int x, int z)
    {
        float wx = (x + 0.5f) * tileSize;
        float wz = (z + 0.5f) * tileSize;
        var line = RegionLayout.RoadLine(hexSize);
        if (line == null || line.Count < 2) return false;
        return DistToPolyline(wx, wz, line) < roadHalfWidth;
    }

    public float roadHalfWidth = 18f;

    static float DistToPolyline(float wx, float wz, List<Vector3> line)
    {
        float best = float.MaxValue;
        for (int i = 0; i < line.Count - 1; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            float d = DistToSegment(wx, wz, a.x, a.z, b.x, b.z);
            if (d < best) best = d;
        }
        return best;
    }

    static float DistToSegment(float px, float pz, float ax, float az, float bx, float bz)
    {
        float abx = bx - ax, abz = bz - az;
        float apx = px - ax, apz = pz - az;
        float ab2 = abx * abx + abz * abz;
        if (ab2 < 0.0001f) return Mathf.Sqrt(apx * apx + apz * apz);
        float t = Mathf.Clamp01((apx * abx + apz * abz) / ab2);
        float dx = px - (ax + abx * t);
        float dz = pz - (az + abz * t);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static readonly int[] _dq = { 1, 1, 0, -1, -1, 0 };
    static readonly int[] _dr = { 0, -1, -1, 0, 1, 1 };

    // ---------- чанки ----------

    void EnqueueRing(int pcx, int pcz)
    {
        int r = Mathf.Max(1, loadRadiusChunks);
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dz * dz > r * r + r) continue;
                var key = new Vector2Int(pcx + dx, pcz + dz);
                if (_chunks.ContainsKey(key) || _queued.Contains(key)) continue;
                _queue.Enqueue(key);
                _queued.Add(key);
            }
    }

    void DropFar(int pcx, int pcz)
    {
        int r = Mathf.Max(loadRadiusChunks + 1, unloadRadiusChunks);
        int r2 = r * r;
        _drop.Clear();
        foreach (var kv in _chunks)
        {
            int dx = kv.Key.x - pcx;
            int dz = kv.Key.y - pcz;
            if (dx * dx + dz * dz <= r2) continue;
            _drop.Add(kv.Key);
        }
        for (int i = 0; i < _drop.Count; i++)
            DestroyChunk(_drop[i]);
    }

    void BuildChunk(int sx, int sz)
    {
        if (chunkBuilder == null)
        {
            Debug.LogError("LocationStream: нет ChunkedTerrainBuilder");
            return;
        }
        float t0 = Time.realtimeSinceStartup;
        chunkBuilder.tileSize = tileSize;
        chunkBuilder.minTerraceCells = minTerraceCells;
        int cs = Mathf.Max(4, chunkCells);
        var go = chunkBuilder.BuildStreamChunk(sx, sz, (x, z) => Height(x, z), cs);
        if (go == null)
        {
            Debug.LogWarning($"[AF.Load] LocationStream.BuildChunk {sx},{sz} вернул null");
            return;
        }
        _chunks[new Vector2Int(sx, sz)] = new Chunk { go = go, sx = sx, sz = sz };
        StampRoad(sx, sz, go.transform);
        if (nature != null && nature.IsReady)
            nature.PlaceStreamSector(sx, sz);
        float ms = (Time.realtimeSinceStartup - t0) * 1000f;
        Debug.Log($"[AF.Load] LocationStream.BuildChunk {sx},{sz} {ms:0.0}ms chunks={_chunks.Count}");
    }

    void StampRoad(int sx, int sz, Transform parent)
    {
        int cs = Mathf.Max(4, chunkCells);
        int x0 = sx * cs;
        int z0 = sz * cs;
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int z = z0; z < z0 + cs; z++)
            for (int x = x0; x < x0 + cs; x++)
            {
                if (!IsRoad(x, z)) continue;
                float y = Height(x, z) + 0.08f;
                float px = x * tileSize;
                float pz = z * tileSize;
                int b = verts.Count;
                verts.Add(new Vector3(px, y, pz));
                verts.Add(new Vector3(px + tileSize, y, pz));
                verts.Add(new Vector3(px + tileSize, y, pz + tileSize));
                verts.Add(new Vector3(px, y, pz + tileSize));
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
        if (verts.Count == 0) return;
        var road = new GameObject("Road");
        road.transform.SetParent(parent, false);
        var mesh = new Mesh { name = "Road" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        var mf = road.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = road.AddComponent<MeshRenderer>();
        mr.sharedMaterial = RoadMat();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    Material _roadMat;
    Material RoadMat()
    {
        if (_roadMat != null) return _roadMat;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        _roadMat = new Material(shader) { name = "HexRoad", color = new Color(0.42f, 0.32f, 0.18f) };
        if (_roadMat.HasProperty("_BaseColor"))
            _roadMat.SetColor("_BaseColor", _roadMat.color);
        return _roadMat;
    }

    void DestroyChunk(Vector2Int key)
    {
        if (!_chunks.TryGetValue(key, out var chunk)) return;
        if (nature != null) nature.ReleaseStreamSector(chunk.sx, chunk.sz);
        if (chunkBuilder != null) chunkBuilder.ReleaseStreamChunk(chunk.go);
        else if (chunk.go != null) Destroy(chunk.go);
        _chunks.Remove(key);
    }

    void ClearChunks()
    {
        _drop.Clear();
        foreach (var kv in _chunks) _drop.Add(kv.Key);
        for (int i = 0; i < _drop.Count; i++) DestroyChunk(_drop[i]);
        _queue.Clear();
        _queued.Clear();
        _chunks.Clear();
    }

    // ---------- спавн и утилиты ----------

    void SnapToHex(int q, int r)
    {
        if (player == null) return;
        var center = HexToWorld(q, r);
        float h = HeightAtWorld(center.x, center.z);
        var pos = new Vector3(center.x, h + 1.2f, center.z);
        var cc = player.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        player.position = pos;
        Physics.SyncTransforms();
        if (cc != null) cc.enabled = true;
    }

    void UpdatePoi()
    {
        CurrentPoi = RegionLayout.KindAt(CurrentHexQ, CurrentHexR);
    }

    Vector2Int WorldToHex(Vector3 world)
    {
        // обратное преобразование axial (pointy-top)
        float q = (Mathf.Sqrt(3f) / 3f * world.x - 1f / 3f * world.z) / hexSize;
        float r = (2f / 3f * world.z) / hexSize;
        return HexRound(q, r);
    }

    public Vector3 HexToWorld(int q, int r)
    {
        float x = hexSize * (Mathf.Sqrt(3f) * q + Mathf.Sqrt(3f) * 0.5f * r);
        float z = hexSize * (1.5f * r);
        return new Vector3(x, 0f, z);
    }

    static Vector2Int HexRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);
        float qDiff = Mathf.Abs(rq - q);
        float rDiff = Mathf.Abs(rr - r);
        float sDiff = Mathf.Abs(rs - s);
        if (qDiff > rDiff && qDiff > sDiff) rq = -rr - rs;
        else if (rDiff > sDiff) rr = -rq - rs;
        return new Vector2Int(rq, rr);
    }

    static Vector2Int HexNeighbor(int q, int r, int dir)
    {
        // 0..5
        int[] dq = { 1, 1, 0, -1, -1, 0 };
        int[] dr = { 0, -1, -1, 0, 1, 1 };
        return new Vector2Int(q + dq[dir], r + dr[dir]);
    }

    int SeedFor(int q, int r)
    {
        unchecked { return seed ^ (q * 73856093) ^ (r * 19349663); }
    }

    static long HexKey(int q, int r)
    {
        return ((long)q << 32) | (uint)r;
    }

    float Noise(float x, float z, float scale, float salt)
    {
        float ox = seed * 0.001f + salt;
        float oz = seed * 0.0017f + salt * 0.5f;
        return Mathf.PerlinNoise(ox + x * scale, oz + z * scale);
    }

    Color TintFor(float wx, float wz)
    {
        var hex = WorldToHex(new Vector3(wx, 0f, wz));
        if (IsRoadWorld(wx, wz))
            return new Color(0.42f, 0.34f, 0.2f);
        if (hex.x == RegionHexMap.HowlingQ && hex.y == RegionHexMap.HowlingR)
            return new Color(0.16f, 0.22f, 0.14f);
        if (HexRegionEntry.IsNeighbor(hex.x, hex.y, RegionHexMap.HowlingQ, RegionHexMap.HowlingR))
            return new Color(0.18f, 0.26f, 0.15f);
        float n = Noise(wx, wz, 0.01f, 5f);
        if (n > 0.65f) return new Color(0.12f, 0.20f, 0.11f);
        if (n < 0.35f) return new Color(0.28f, 0.38f, 0.18f);
        return new Color(0.20f, 0.32f, 0.14f);
    }

    Material _ground;
    Material GroundMat()
    {
        if (_ground != null) return _ground;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        _ground = new Material(shader) { name = "HexGround", color = new Color(0.22f, 0.34f, 0.16f) };
        if (_ground.HasProperty("_BaseColor"))
            _ground.SetColor("_BaseColor", _ground.color);
        return _ground;
    }

    void EnsureRoot()
    {
        if (_root != null) return;
        var existing = transform.Find("LocationChunks");
        if (existing != null) _root = existing;
        else
        {
            var go = new GameObject("LocationChunks");
            go.transform.SetParent(transform, false);
            _root = go.transform;
        }
    }

    void OnDrawGizmos()
    {
        if (!Application.isPlaying && !driveWorld) return;
        DrawHexBounds(CurrentHexQ, CurrentHexR, new Color(1f, 0.85f, 0.2f, 0.9f));
        for (int d = 0; d < 6; d++)
        {
            int nq = CurrentHexQ + _dq[d];
            int nr = CurrentHexR + _dr[d];
            DrawHexBounds(nq, nr, new Color(0.6f, 0.75f, 0.4f, 0.35f));
        }
#if UNITY_EDITOR
        DrawHexLabels();
#endif
    }

#if UNITY_EDITOR
    void DrawHexLabels()
    {
        var nameStyle = new GUIStyle(UnityEditor.EditorStyles.boldLabel);
        nameStyle.normal.textColor = new Color(1f, 0.95f, 0.7f);
        nameStyle.fontSize = 14;
        nameStyle.alignment = TextAnchor.MiddleCenter;

        int r = 2;
        for (int dq = -r; dq <= r; dq++)
        for (int dr = -r; dr <= r; dr++)
        {
            int q = CurrentHexQ + dq;
            int rr = CurrentHexR + dr;
            if ((Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2 > r) continue;
            var c = HexToWorld(q, rr);
            c.y = HeightAtWorld(c.x, c.z) + 8f;
            UnityEditor.Handles.Label(c, $"{q},{rr}");
        }

        DrawSiteLabel(RegionLayout.Village, "деревня", nameStyle);
        DrawSiteLabel(RegionLayout.Temple, "храм", nameStyle);
        DrawSiteLabel(RegionLayout.Howling, "Воющая пуща", nameStyle);
    }

    void DrawSiteLabel(Vector2Int hex, string title, GUIStyle style)
    {
        if (RegionLayout.Seed == 0) return;
        var c = HexToWorld(hex.x, hex.y);
        c.y = HeightAtWorld(c.x, c.z) + 14f;
        UnityEditor.Handles.Label(c, title, style);
    }
#endif

    void DrawHexBounds(int q, int r, Color color)
    {
        var c = HexToWorld(q, r);
        c.y = HeightAtWorld(c.x, c.z) + 0.35f;
        Gizmos.color = color;
        Vector3 prev = c;
        for (int i = 0; i <= 6; i++)
        {
            float a = i * Mathf.PI * 2f / 6f + Mathf.PI / 6f;
            var p = c + new Vector3(Mathf.Cos(a) * hexSize, 0f, Mathf.Sin(a) * hexSize);
            if (i > 0) Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }

    void OnDestroy() => ClearChunks();

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        // зоны соседних гексов — как старая разметка, но по гексам
        int r = 2;
        for (int dq = -r; dq <= r; dq++)
            for (int dr = -r; dr <= r; dr++)
            {
                int q = CurrentHexQ + dq;
                int rr = CurrentHexR + dr;
                if ((Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2 > r) continue;
                var zone = RegionLayout.ZoneForHex(q, rr);
                Gizmos.color = ZoneColor(zone, RegionLayout.IsRoadHex(q, rr));
                var c = HexToWorld(q, rr);
                c.y = HeightAtWorld(c.x, c.z) + 0.4f;
                Gizmos.DrawCube(c, new Vector3(hexSize * 0.85f, 0.3f, hexSize * 0.85f));
            }
    }

    static Color ZoneColor(NaturePlacement.Zone z, bool road)
    {
        if (road) return new Color(0.45f, 0.36f, 0.22f, 0.45f);
        switch (z)
        {
            case NaturePlacement.Zone.Grove: return new Color(0.55f, 0.75f, 0.35f, 0.35f);
            case NaturePlacement.Zone.Thicket: return new Color(0.12f, 0.28f, 0.12f, 0.4f);
            case NaturePlacement.Zone.Clearing: return new Color(0.7f, 0.72f, 0.4f, 0.35f);
            default: return new Color(0.25f, 0.45f, 0.22f, 0.3f);
        }
    }
}
