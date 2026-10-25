using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Отрисовка + hybrid live + стриминг.
/// Обычное дерево — родной материал префаба (ProPixelizer), без подмены.
/// Если дерево между камерой и клеткой видимости: сначала темнеет на родном,
/// потом становится тёмно-прозрачным (Nature/TreeSeeThrough).
/// Live — коллайдер; картинка всегда инстанс.
/// </summary>
public class NatureRenderer : MonoBehaviour
{
    [Header("Источник данных")]
    public NaturePlacement placement;

    [Header("Игрок / камера")]
    public Transform player;
    public Camera cam;
    public PlayerVision playerVision;

    [Header("Радиусы")]
    public float drawRadius = 55f;
    [Tooltip("Начать live при входе в этот радиус")]
    public float liveRadius = 16f;
    [Tooltip("Снять live только когда дальше (гистерезис)")]
    public float liveExitExtra = 10f;

    [Header("Время суток")]
    [Tooltip("Множитель альбедо инстансов. Ставит TimeOfDayController.")]
    public Color todTint = Color.white;

    [Header("Прозрачность")]
    [Tooltip("Дерево между камерой и клеткой видимости: темнеет, потом тёмно-прозрачное.")]
    public bool fadeTreesInVision = true;
    public float darkenSeconds = 0.3f;
    public float fadeSeconds = 0.35f;
    [Range(0.15f, 0.9f)]
    public float seeThroughAlpha = 0.72f;
    public Color silhouetteColor = new Color(0.06f, 0.06f, 0.07f, 1f);
    [Tooltip("Насколько близко дерево к лучу камера→клетка, чтобы считаться заслоном.")]
    public float coverRadius = 3.2f;

    [Header("Live")]
    public int liveCheckEveryNFrames = 12;
    public Transform liveRoot;

    [Header("Стриминг")]
    public int streamCheckEveryNFrames = 20;

    [Header("Gizmo")]
    [Tooltip("Буква T на клетках с деревом. Жёлтая — клетка в поле зрения.")]
    public bool drawTreeLetters = false;
    public Color treeLetterColor = new Color(0.2f, 0.85f, 0.25f, 1f);

    public static NatureRenderer Active { get; private set; }

    private readonly Dictionary<long, LiveEntry> _live = new Dictionary<long, LiveEntry>();
    private readonly HashSet<long> _liveKeys = new HashSet<long>();
    private readonly List<long> _toRemove = new List<long>();

    private readonly List<Matrix4x4> _partBatch = new List<Matrix4x4>(256);
    private readonly List<Matrix4x4> _solidRoots = new List<Matrix4x4>(256);
    private readonly List<Matrix4x4> _fadeRoots = new List<Matrix4x4>(64);
    private readonly List<float> _fadeLevel = new List<float>(64);
    private readonly List<Vector3> _visCenters = new List<Vector3>(256);
    private readonly List<Vector2Int> _visCells = new List<Vector2Int>(256);
    private readonly Dictionary<long, float> _fade = new Dictionary<long, float>();
    private readonly List<long> _fadeDrop = new List<long>();
    private readonly HashSet<long> _fadeSeen = new HashSet<long>();
    private readonly Dictionary<Material, Material> _seeThroughMats = new Dictionary<Material, Material>();
    private Shader _seeThroughShader;
    private MaterialPropertyBlock _oneBlock;
    private Matrix4x4[] _drawSlice = new Matrix4x4[1023];
    private Matrix4x4[] _oneSlice = new Matrix4x4[1];
    private readonly List<List<Matrix4x4>> _drawCache = new List<List<Matrix4x4>>();
    private int _cacheStamp = int.MinValue;
    private int _cachePcx = int.MinValue, _cachePcz = int.MinValue, _cacheR = int.MinValue;
    private float _lastRebuildMs;
    private int _lastRebuildMatrices;
    private float _nextSummary;
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int TodTintId = Shader.PropertyToID("_TodTint");

    private class LiveEntry
    {
        public GameObject go;
        public int variantIndex;
        public Vector2Int sector;
        public Vector3 pos;
        public Renderer[] renderers;
    }

    void OnEnable()
    {
        Active = this;
    }

    void Start()
    {
        if (placement == null)
            placement = GetComponent<NaturePlacement>();
        if (cam == null) cam = Camera.main;
        if (playerVision == null && player != null)
            playerVision = player.GetComponentInChildren<PlayerVision>();
        if (playerVision == null)
            playerVision = FindObjectOfType<PlayerVision>();
        if (playerVision != null)
            playerVision.EnsureMapGrid();

        if (placement != null && !placement.IsReady
            && placement.heightSource != null && placement.heightSource.isGenerated)
            placement.Init();
    }

    void LateUpdate()
    {
        ResolveRefs();
        if (placement == null || player == null) return;
        if (!placement.IsReady)
        {
            placement.Init();
            if (!placement.IsReady) return;
        }

        if (Time.frameCount % Mathf.Max(1, streamCheckEveryNFrames) == 0)
            placement.UpdateStreaming(player.position);

        DrawInstanced();

        if (Time.frameCount % Mathf.Max(1, liveCheckEveryNFrames) == 0)
            UpdateLive();
    }

    private void ResolveRefs()
    {
        if (placement == null)
            placement = GetComponent<NaturePlacement>();

        if (cam == null)
        {
            var cf = FindObjectOfType<CameraFollow>();
            if (cf != null) cam = cf.GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
        }

        if (player == null)
        {
            var cf = cam != null ? cam.GetComponent<CameraFollow>() : FindObjectOfType<CameraFollow>();
            if (cf != null && cf.target != null)
                player = cf.target;
            if (player == null)
                player = PlayerRegistry.ResolvePrimary();
            if (player != null)
            {
                playerVision = player.GetComponentInChildren<PlayerVision>();
                if (playerVision != null) playerVision.EnsureMapGrid();
            }
        }
        else
        {
            var cf = cam != null ? cam.GetComponent<CameraFollow>() : null;
            if (cf != null && cf.target != null && cf.target != player)
            {
                player = cf.target;
                playerVision = player.GetComponentInChildren<PlayerVision>();
                if (playerVision != null) playerVision.EnsureMapGrid();
            }
        }

        if (playerVision == null && player != null)
        {
            playerVision = player.GetComponentInChildren<PlayerVision>();
            if (playerVision != null) playerVision.EnsureMapGrid();
        }
    }

    private void DrawInstanced()
    {
        if (placement.streamGround == null && placement.terrainBuilder == null) return;
        if (placement.heightSource == null && placement.streamGround == null) return;
        if (placement.allVariants == null) return;

        float ts = placement.terrainBuilder != null ? placement.terrainBuilder.TileSize : 4f;
        float sectorWorld = placement.sectorSize * ts;
        Vector3 origin;
        if (!placement.TryCellOrigin(out origin))
        {
            int w = placement.heightSource != null ? placement.heightSource.width : 0;
            int d = placement.heightSource != null ? placement.heightSource.depth : 0;
            origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);
        }
        Vector3 playerLocal = (placement.streamGround != null)
            ? player.position
            : placement.terrainBuilder.WorldToMapLocal(player.position);

        int pcx = Mathf.FloorToInt((playerLocal.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((playerLocal.z - origin.z) / sectorWorld);
        int r = Mathf.CeilToInt(drawRadius / sectorWorld);
        int stamp = placement.StreamStamp;

        bool dirty = stamp != _cacheStamp || pcx != _cachePcx || pcz != _cachePcz || r != _cacheR
            || _drawCache.Count != placement.allVariants.Count;
        if (dirty)
            RebuildDrawCache(pcx, pcz, r, stamp);

        if (fadeTreesInVision && playerVision != null)
            RefreshVisibleCenters();

        for (int vi = 0; vi < placement.allVariants.Count; vi++)
        {
            var v = placement.allVariants[vi];
            if (v.parts == null || v.parts.Count == 0) continue;
            if (vi >= _drawCache.Count) continue;
            var roots = _drawCache[vi];
            if (roots == null || roots.Count == 0) continue;
            var layer = placement.variantLayer[vi];
            var shadows = v.castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;

            bool canFade = fadeTreesInVision && layer != null && layer.IsTree && playerVision != null;
            if (!canFade)
            {
                DrawNative(v, roots, v.propertyBlock, shadows);
                continue;
            }

            _solidRoots.Clear();
            _fadeRoots.Clear();
            _fadeLevel.Clear();

            float dt = Time.deltaTime;
            float darkStep = dt / Mathf.Max(0.05f, darkenSeconds);
            float fadeStep = dt / Mathf.Max(0.05f, fadeSeconds);

            for (int i = 0; i < roots.Count; i++)
            {
                Vector3 pos = roots[i].GetColumn(3);
                long h = PosHash(pos);
                _fadeSeen.Add(h);
                bool covers = CoversVisibleCell(pos);
                _fade.TryGetValue(h, out float t);
                if (covers)
                {
                    if (t < 1f) t = Mathf.Min(1f, t + darkStep);
                    else t = Mathf.Min(2f, t + fadeStep);
                }
                else
                {
                    if (t > 1f) t = Mathf.Max(1f, t - fadeStep);
                    else t = Mathf.Max(0f, t - darkStep);
                }
                if (t <= 0.001f)
                {
                    _fade.Remove(h);
                    _solidRoots.Add(roots[i]);
                    continue;
                }
                _fade[h] = t;
                _fadeRoots.Add(roots[i]);
                _fadeLevel.Add(t);
            }

            if (_solidRoots.Count > 0)
                DrawNative(v, _solidRoots, v.propertyBlock, shadows);
            for (int i = 0; i < _fadeRoots.Count; i++)
                DrawFadingOne(v, _fadeRoots[i], _fadeLevel[i], shadows);
        }

        if (_fade.Count > 0)
        {
            _fadeDrop.Clear();
            foreach (var kv in _fade)
            {
                if (!_fadeSeen.Contains(kv.Key))
                    _fadeDrop.Add(kv.Key);
            }
            for (int i = 0; i < _fadeDrop.Count; i++)
                _fade.Remove(_fadeDrop[i]);
            _fadeSeen.Clear();
        }

        if (Time.unscaledTime >= _nextSummary)
        {
            _nextSummary = Time.unscaledTime + 8f;
            Debug.Log($"[AF.Load] NatureRenderer cache stamp={_cacheStamp} sector={_cachePcx},{_cachePcz} r={_cacheR} matrices={_lastRebuildMatrices} rebuild={_lastRebuildMs:0.0}ms");
        }
    }

    private void RebuildDrawCache(int pcx, int pcz, int r, int stamp)
    {
        float t0 = Time.realtimeSinceStartup;
        while (_drawCache.Count < placement.allVariants.Count)
            _drawCache.Add(new List<Matrix4x4>(256));
        int total = 0;
        for (int vi = 0; vi < placement.allVariants.Count; vi++)
        {
            var v = placement.allVariants[vi];
            var dst = _drawCache[vi];
            dst.Clear();
            if (v.sectors == null || v.parts == null || v.parts.Count == 0) continue;
            for (int sx = pcx - r; sx <= pcx + r; sx++)
            {
                for (int sz = pcz - r; sz <= pcz + r; sz++)
                {
                    if (!v.sectors.TryGetValue(new Vector2Int(sx, sz), out var batches)) continue;
                    foreach (var batch in batches)
                    {
                        if (batch == null) continue;
                        for (int i = 0; i < batch.Length; i++)
                            dst.Add(batch[i]);
                    }
                }
            }
            total += dst.Count;
        }
        _cacheStamp = stamp;
        _cachePcx = pcx;
        _cachePcz = pcz;
        _cacheR = r;
        _lastRebuildMatrices = total;
        _lastRebuildMs = (Time.realtimeSinceStartup - t0) * 1000f;
        Debug.Log($"[AF.Load] NatureRenderer.RebuildDrawCache {_lastRebuildMs:0.0}ms matrices={total} stamp={stamp} sector={pcx},{pcz} r={r}");
    }


    private void RefreshVisibleCenters()
    {
        _visCenters.Clear();
        if (playerVision == null || playerVision.mapGrid == null || !playerVision.mapGrid.IsReady)
            return;
        playerVision.CollectVisibleCells(_visCells);
        var grid = playerVision.mapGrid;
        for (int i = 0; i < _visCells.Count; i++)
        {
            var c = _visCells[i];
            _visCenters.Add(grid.CellCenterWorld(c.x, c.y));
        }
    }

    private bool CoversVisibleCell(Vector3 treePos)
    {
        if (cam == null || _visCenters.Count == 0) return false;
        Vector3 camPos = cam.transform.position;
        float r2 = coverRadius * coverRadius;
        for (int i = 0; i < _visCenters.Count; i++)
        {
            Vector3 cell = _visCenters[i];
            Vector3 toCell = cell - camPos;
            float len2 = toCell.sqrMagnitude;
            if (len2 < 4f) continue;
            float along = Vector3.Dot(treePos - camPos, toCell) / len2;
            if (along < 0.12f || along > 0.9f) continue;
            Vector3 closest = camPos + toCell * along;
            float sx = treePos.x - closest.x;
            float sz = treePos.z - closest.z;
            if (sx * sx + sz * sz <= r2)
                return true;
        }
        return false;
    }

    private void DrawNative(NaturePlacement.NatureVariant v, List<Matrix4x4> roots,
        MaterialPropertyBlock block, UnityEngine.Rendering.ShadowCastingMode shadows)
    {
        DrawParts(v, roots, block, shadows, false);
    }

    private void DrawFadingOne(NaturePlacement.NatureVariant v, Matrix4x4 root, float t,
        UnityEngine.Rendering.ShadowCastingMode shadows)
    {
        bool ghost = t >= 1f;
        if (!ghost)
        {
            DrawOne(v, root, v.propertyBlock, shadows, false);
            return;
        }

        if (_oneBlock == null) _oneBlock = new MaterialPropertyBlock();
        _oneBlock.Clear();
        float u = t - 1f;
        float alpha = Mathf.Lerp(0.15f, seeThroughAlpha, u);
        Color c = silhouetteColor;
        c.a = alpha;
        _oneBlock.SetColor("_Color", c);
        _oneBlock.SetColor("_BaseColor", c);
        DrawOne(v, root, _oneBlock, UnityEngine.Rendering.ShadowCastingMode.Off, true);
    }

    private void DrawParts(NaturePlacement.NatureVariant v, List<Matrix4x4> roots,
        MaterialPropertyBlock block, UnityEngine.Rendering.ShadowCastingMode shadows, bool ghost)
    {
        if (roots.Count == 0 || v.parts == null) return;
        for (int p = 0; p < v.parts.Count; p++)
        {
            var part = v.parts[p];
            if (part.mesh == null || part.material == null) continue;
            Material mat = part.material;
            if (ghost)
            {
                var swapped = SeeThroughOf(part.material);
                if (swapped != null) mat = swapped;
            }
            bool ident = part.localToRoot.isIdentity;
            List<Matrix4x4> list = roots;
            if (!ident)
            {
                _partBatch.Clear();
                for (int i = 0; i < roots.Count; i++)
                    _partBatch.Add(roots[i] * part.localToRoot);
                list = _partBatch;
            }
            DrawList(part.mesh, mat, block, list, shadows, part.submesh);
        }
    }

    private void DrawOne(NaturePlacement.NatureVariant v, Matrix4x4 root,
        MaterialPropertyBlock block, UnityEngine.Rendering.ShadowCastingMode shadows, bool ghost)
    {
        if (v.parts == null) return;
        for (int p = 0; p < v.parts.Count; p++)
        {
            var part = v.parts[p];
            if (part.mesh == null || part.material == null) continue;
            Material mat = part.material;
            if (ghost)
            {
                var swapped = SeeThroughOf(part.material);
                if (swapped != null) mat = swapped;
            }
            _oneSlice[0] = part.localToRoot.isIdentity ? root : root * part.localToRoot;
            int sub = part.submesh;
            if (sub < 0 || sub >= part.mesh.subMeshCount) sub = 0;
            Graphics.DrawMeshInstanced(part.mesh, sub, mat, _oneSlice, 1, block, shadows, false);
        }
    }

    private Material SeeThroughOf(Material src)
    {
        if (src == null) return null;
        if (_seeThroughMats.TryGetValue(src, out var cached) && cached != null)
            return cached;

        if (_seeThroughShader == null)
            _seeThroughShader = Shader.Find("Nature/TreeSeeThrough");
        if (_seeThroughShader == null)
        {
            if (Time.frameCount % 300 == 0)
                Debug.LogWarning("NatureRenderer: нет шейдера Nature/TreeSeeThrough — прозрачность не включится.");
            return src;
        }

        var m = new Material(_seeThroughShader);
        m.name = "TreeSilhouette";
        Color c = silhouetteColor;
        c.a = seeThroughAlpha;
        m.SetColor(BaseColorId, c);
        m.enableInstancing = true;
        _seeThroughMats[src] = m;
        return m;
    }

    private void DrawList(Mesh mesh, Material mat, MaterialPropertyBlock block,
        List<Matrix4x4> list, UnityEngine.Rendering.ShadowCastingMode shadows, int submesh = 0)
    {
        if (list.Count == 0 || mesh == null || mat == null) return;
        if (submesh < 0 || submesh >= mesh.subMeshCount) submesh = 0;

        const int BS = 1023;
        if (_drawSlice == null || _drawSlice.Length < BS)
            _drawSlice = new Matrix4x4[BS];
        for (int start = 0; start < list.Count; start += BS)
        {
            int len = Mathf.Min(BS, list.Count - start);
            for (int i = 0; i < len; i++)
                _drawSlice[i] = list[start + i];
            Graphics.DrawMeshInstanced(mesh, submesh, mat, _drawSlice, len, block, shadows, false);
        }
    }

    private void UpdateLive()
    {
        if (placement.allVariants == null) return;

        float enterR2 = liveRadius * liveRadius;
        float exitR = liveRadius + liveExitExtra;
        float exitR2 = exitR * exitR;
        Vector3 pp = player.position;

        _toRemove.Clear();
        foreach (var kv in _live)
        {
            if ((kv.Value.pos - pp).sqrMagnitude > exitR2)
                _toRemove.Add(kv.Key);
        }
        for (int i = 0; i < _toRemove.Count; i++)
        {
            long k = _toRemove[i];
            if (_live.TryGetValue(k, out var e) && e.go != null)
            {
                if (Application.isPlaying) Destroy(e.go);
                else DestroyImmediate(e.go);
            }
            _live.Remove(k);
            _liveKeys.Remove(k);
        }

        float ts = placement.terrainBuilder != null ? placement.terrainBuilder.TileSize : 4f;
        float sectorWorld = placement.sectorSize * ts;
        Vector3 origin;
        if (!placement.TryCellOrigin(out origin))
        {
            int w = placement.heightSource != null ? placement.heightSource.width : 0;
            int d = placement.heightSource != null ? placement.heightSource.depth : 0;
            origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);
        }
        Vector3 playerLocal = (placement.streamGround != null)
            ? pp
            : (placement.terrainBuilder != null ? placement.terrainBuilder.WorldToMapLocal(pp) : pp);

        int pcx = Mathf.FloorToInt((playerLocal.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((playerLocal.z - origin.z) / sectorWorld);
        int r = Mathf.CeilToInt(exitR / sectorWorld) + 1;

        EnsureLiveRoot();

        for (int vi = 0; vi < placement.allVariants.Count; vi++)
        {
            var v = placement.allVariants[vi];
            var layer = placement.variantLayer[vi];
            if (!layer.IsTree || v.prefab == null || v.sectorLists == null) continue;

            for (int sx = pcx - r; sx <= pcx + r; sx++)
            {
                for (int sz = pcz - r; sz <= pcz + r; sz++)
                {
                    var skey = new Vector2Int(sx, sz);
                    if (!v.sectorLists.TryGetValue(skey, out var list)) continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        Vector3 pos = list[i].GetColumn(3);
                        if ((pos - pp).sqrMagnitude > enterR2) continue;

                        long h = PosHash(pos);
                        if (_live.ContainsKey(h)) continue;

                        Quaternion rot = list[i].rotation;
                        Vector3 scale = list[i].lossyScale;
                        GameObject go = Instantiate(v.prefab, pos, rot, liveRoot);
                        go.transform.localScale = scale;
                        go.name = $"{v.name}_live";

                        var lods = go.GetComponentsInChildren<LODGroup>(true);
                        for (int li = 0; li < lods.Length; li++)
                            lods[li].enabled = false;

                        var renderers = go.GetComponentsInChildren<Renderer>(true);
                        for (int ri = 0; ri < renderers.Length; ri++)
                        {
                            if (renderers[ri] != null)
                                renderers[ri].enabled = false;
                        }

                        _live[h] = new LiveEntry
                        {
                            go = go,
                            variantIndex = vi,
                            sector = skey,
                            pos = pos,
                            renderers = renderers
                        };
                        _liveKeys.Add(h);
                    }
                }
            }
        }
    }

    private void EnsureLiveRoot()
    {
        if (liveRoot != null) return;
        var go = new GameObject("NatureLive");
        go.transform.SetParent(transform, false);
        liveRoot = go.transform;
    }

    private static long PosHash(Vector3 p)
    {
        int x = Mathf.RoundToInt(p.x * 100f);
        int y = Mathf.RoundToInt(p.y * 100f);
        int z = Mathf.RoundToInt(p.z * 100f);
        unchecked
        {
            long h = x;
            h = (h * 397) ^ y;
            h = (h * 397) ^ z;
            return h;
        }
    }

    public bool IsCellFading(int cx, int cz)
    {
        if (!fadeTreesInVision || playerVision == null) return false;
        if (playerVision.mapGrid == null || !playerVision.mapGrid.IsReady) return false;
        return playerVision.InShape(playerVision.mapGrid.CellCenterWorld(cx, cz));
    }

    [ContextMenu("Clear Live")]
    public void ClearLive()
    {
        foreach (var kv in _live)
        {
            if (kv.Value.go != null)
            {
                if (Application.isPlaying) Destroy(kv.Value.go);
                else DestroyImmediate(kv.Value.go);
            }
        }
        _live.Clear();
        _liveKeys.Clear();
    }

    void OnGUI()
    {
        if (!drawTreeLetters || !Application.isPlaying) return;
        if (cam == null || player == null) return;
        MapGrid grid = playerVision != null ? playerVision.mapGrid : null;
        if (grid == null || !grid.IsReady) return;

        grid.WorldToCell(player.position, out int pcx, out int pcz);
        int span = 18;
        int x0 = Mathf.Max(0, pcx - span);
        int x1 = Mathf.Min(grid.Width - 1, pcx + span);
        int z0 = Mathf.Max(0, pcz - span);
        int z1 = Mathf.Min(grid.Depth - 1, pcz + span);
        float yOff = playerVision != null ? playerVision.yOffset : 0.12f;

        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                if (!grid.HasFlag(x, z, MapGrid.OccupancyFlags.Tree)) continue;
                Vector3 w = grid.CellCenterWorld(x, z);
                w.y += yOff + 0.2f;
                Vector3 sp = cam.WorldToScreenPoint(w);
                if (sp.z <= 0f) continue;
                if (sp.x < 0f || sp.x > Screen.width || sp.y < 0f || sp.y > Screen.height)
                    continue;

                GUI.color = treeLetterColor;
                GUI.Label(new Rect(sp.x - 6f, Screen.height - sp.y - 8f, 20f, 18f), "T");
            }
        }
        GUI.color = Color.white;
    }
}
