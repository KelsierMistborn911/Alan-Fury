using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Отрисовка + hybrid live + стриминг.
/// Fade: открытые видимые клетки. Клетка-укрытие (дерево) не бледнеет.
/// Макс внутри поля, к краю — в непрозрачное.
/// Ствол (низ + радиус корня) не выцветает. Без среза-плоскости.
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

    [Header("Fade (заслон камеры)")]
    public bool useVisionFade = true;
    [Tooltip("Ширина мягкого края у границы зрения (м).")]
    public float fadeEdgeMeters = 2.2f;
    [Tooltip("Высота ствола, который не выцветает (м от корня).")]
    public float fadeKeepHeight = 2.3f;
    [Tooltip("Радиус ствола, который не выцветает (м от корня).")]
    public float fadeKeepRadius = 1.8f;
    [Tooltip("Мягкость края ствола (м).")]
    public float fadeKeepFalloff = 1.0f;
    [Tooltip("Насколько обесцветить крону в fade.")]
    [Range(0f, 1f)] public float fadePale = 0f;
    [Tooltip("Секунды удержания прозрачности после выхода клетки из зрения.")]
    public float fadeRestoreDelay = 0.4f;
    [Tooltip("Секунды плавного возврата в непрозрачное.")]
    public float fadeRestoreTime = 0.7f;
    public Material fadeMaterial;

    [Header("Live")]
    public int liveCheckEveryNFrames = 12;
    public Transform liveRoot;

    [Header("Стриминг")]
    public int streamCheckEveryNFrames = 20;

    [Header("Темп проверок")]
    [Tooltip("Раз в сколько кадров пересчитывать маску зрения.")]
    public int fadeCheckEveryNFrames = 5;

    [Header("Gizmo")]
    [Tooltip("Буква T на клетках с деревом. Жёлтая — клетка в поле зрения.")]
    public bool drawTreeLetters = false;
    public Color treeLetterColor = new Color(0.2f, 0.85f, 0.25f, 1f);
    public Color treeLetterFadedColor = new Color(1f, 0.82f, 0.12f, 1f);

    public static NatureRenderer Active { get; private set; }

    private readonly Dictionary<long, LiveEntry> _live = new Dictionary<long, LiveEntry>();
    private readonly HashSet<long> _liveKeys = new HashSet<long>();
    private readonly List<long> _toRemove = new List<long>();

    private readonly List<Matrix4x4> _opaque = new List<Matrix4x4>(256);
    private readonly List<Matrix4x4> _faded = new List<Matrix4x4>(256);
    private readonly List<Matrix4x4> _partBatch = new List<Matrix4x4>(256);
    private readonly Color32[] _maskBlur = new Color32[MaskDim * MaskDim];
    private readonly int[] _visDist = new int[MaskDim * MaskDim];
    private readonly int[] _bfsQ = new int[MaskDim * MaskDim];
    private readonly float[] _fadePersist = new float[MaskDim * MaskDim];
    private readonly float[] _fadeHold = new float[MaskDim * MaskDim];
    private readonly float[] _fadeScratch = new float[MaskDim * MaskDim];
    private int _persistOx = int.MinValue;
    private int _persistOz = int.MinValue;
    private float _lastFadeTime;
    private bool _fadeRestoring;
    private readonly List<Vector2Int> _visibleCells = new List<Vector2Int>(512);
    private readonly HashSet<Vector2Int> _visibleSet = new HashSet<Vector2Int>();
    private Texture2D _cellMask;
    private Color32[] _cellMaskPixels;
    private int _maskOx, _maskOz;
    private bool _maskOk;
    private const int MaskDim = 64;
    private Vector3 _fadePlayerXZ;
    private int _lastFadeEval = -999;
    private bool _fadeShaderChecked;
    private bool _fadeShaderOkCache;

    private MaterialPropertyBlock _fadeBlock;
    private Matrix4x4[] _drawSlice = new Matrix4x4[1023];
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private readonly Dictionary<int, Material> _fadeBySrc = new Dictionary<int, Material>();
    private static readonly int VisionGroundYId = Shader.PropertyToID("_VisionGroundY");
    private static readonly int VisionCellMaskTexId = Shader.PropertyToID("_VisionCellMaskTex");
    private static readonly int VisionMapOriginId = Shader.PropertyToID("_VisionMapOrigin");
    private static readonly int VisionTileSizeId = Shader.PropertyToID("_VisionTileSize");
    private static readonly int VisionMaskOriginId = Shader.PropertyToID("_VisionMaskOrigin");
    private static readonly int VisionMaskDimId = Shader.PropertyToID("_VisionMaskDim");
    private static readonly int VisionMaskOnId = Shader.PropertyToID("_VisionMaskOn");
    private static readonly int VisionKeepRadiusId = Shader.PropertyToID("_VisionKeepRadius");
    private static readonly int VisionPartInvId = Shader.PropertyToID("_VisionPartInv");
    private static readonly int VisionFadePaleId = Shader.PropertyToID("_VisionFadePale");
    private static readonly int VisionCamFwdId = Shader.PropertyToID("_VisionCamFwd");
    private static readonly int VisionNearFalloffId = Shader.PropertyToID("_VisionNearFalloff");
    private static readonly int VisionKeepHeightId = Shader.PropertyToID("_VisionKeepHeight");

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
        _fadeBlock = new MaterialPropertyBlock();
        EnsureFadeMaterial();
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

        if (useVisionFade)
        {
            if (ShouldEvalFade())
            {
                BuildVisibleMask();
                StampFadeMask();
                PushFadeGlobals();
                _lastFadeEval = Time.frameCount;
                _fadePlayerXZ = player.position;
            }
        }
        else
        {
            _maskOk = false;
        }

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

    private bool ShouldEvalFade()
    {
        if (_fadeRestoring) return true;
        int n = Mathf.Max(1, fadeCheckEveryNFrames);
        if (Time.frameCount - _lastFadeEval >= n) return true;
        if (player == null) return false;
        float dx = player.position.x - _fadePlayerXZ.x;
        float dz = player.position.z - _fadePlayerXZ.z;
        return dx * dx + dz * dz > 1.21f;
    }

    private void EnsureFadeMaterial()
    {
        if (fadeMaterial != null && fadeMaterial.shader != null) return;
        Shader sh = Shader.Find("Nature/VisionFade");
        if (sh == null) return;
        if (fadeMaterial == null) fadeMaterial = new Material(sh);
        else fadeMaterial.shader = sh;
        fadeMaterial.enableInstancing = true;
        fadeMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private void PushFadeGlobals()
    {
        Shader.SetGlobalFloat(VisionFadePaleId, Mathf.Clamp01(fadePale));
        Shader.SetGlobalFloat(VisionGroundYId, player != null ? player.position.y : 0f);
        Shader.SetGlobalTexture(VisionCellMaskTexId, _cellMask);
        MapGrid grid = playerVision != null ? playerVision.mapGrid : null;
        if (grid != null && grid.IsReady)
        {
            Vector3 o = grid.Origin;
            Shader.SetGlobalVector(VisionMapOriginId, new Vector4(o.x, o.z, 0f, 0f));
            Shader.SetGlobalFloat(VisionTileSizeId, grid.TileSize);
        }
        Shader.SetGlobalVector(VisionMaskOriginId, new Vector4(_maskOx, _maskOz, 0f, 0f));
        Shader.SetGlobalFloat(VisionMaskDimId, MaskDim);
        Shader.SetGlobalFloat(VisionMaskOnId, _maskOk ? 1f : 0f);
        if (cam != null)
        {
            Vector3 cf = cam.transform.forward;
            Shader.SetGlobalVector(VisionCamFwdId, new Vector4(cf.x, cf.y, cf.z, 0f));
        }
        Shader.SetGlobalFloat(VisionKeepRadiusId, Mathf.Max(0f, fadeKeepRadius));
        Shader.SetGlobalFloat(VisionNearFalloffId, Mathf.Max(0.5f, fadeKeepFalloff));
        Shader.SetGlobalFloat(VisionKeepHeightId, Mathf.Max(0.5f, fadeKeepHeight));
    }

    private void EnsureCellMask()
    {
        if (_cellMask != null) return;
        _cellMask = new Texture2D(MaskDim, MaskDim, TextureFormat.RGBA32, false, true);
        _cellMask.filterMode = FilterMode.Bilinear;
        _cellMask.wrapMode = TextureWrapMode.Clamp;
        _cellMask.name = "VisionFadeCellMask";
        _cellMaskPixels = new Color32[MaskDim * MaskDim];
    }

    private void BuildVisibleMask()
    {
        _maskOk = false;
        _visibleCells.Clear();
        _visibleSet.Clear();
        if (playerVision == null) return;

        playerVision.EnsureMapGrid();
        MapGrid grid = playerVision.mapGrid;
        if (grid == null || !grid.IsReady) return;

        playerVision.CollectVisibleCells(_visibleCells);
        if (_visibleCells.Count == 0) return;

        for (int i = 0; i < _visibleCells.Count; i++)
            _visibleSet.Add(_visibleCells[i]);

        float ts = grid.TileSize;
        Vector3 o = grid.Origin;
        int pcx = Mathf.FloorToInt((player.position.x - o.x) / ts);
        int pcz = Mathf.FloorToInt((player.position.z - o.z) / ts);
        _maskOx = pcx - MaskDim / 2;
        _maskOz = pcz - MaskDim / 2;

        EnsureCellMask();
        var clear = new Color32(0, 0, 0, 0);
        for (int i = 0; i < _cellMaskPixels.Length; i++)
            _cellMaskPixels[i] = clear;

        _maskOk = true;
    }

    private void StampFadeMask()
    {
        if (_cellMask == null || _cellMaskPixels == null) return;

        int n = MaskDim * MaskDim;
        for (int i = 0; i < n; i++)
            _visDist[i] = 0;

        foreach (var cell in _visibleSet)
        {
            if (CellIsCover(cell.x, cell.y)) continue;
            int lx = cell.x - _maskOx;
            int lz = cell.y - _maskOz;
            if ((uint)lx >= MaskDim || (uint)lz >= MaskDim) continue;
            _visDist[lz * MaskDim + lx] = 999;
        }

        int qh = 0, qt = 0;
        for (int i = 0; i < n; i++)
        {
            if (_visDist[i] != 0) continue;
            _bfsQ[qt++] = i;
        }

        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };
        while (qh < qt)
        {
            int i = _bfsQ[qh++];
            int x = i % MaskDim;
            int z = i / MaskDim;
            int nd = _visDist[i] + 1;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + dx[k];
                int nz = z + dz[k];
                if ((uint)nx >= MaskDim || (uint)nz >= MaskDim) continue;
                int j = nz * MaskDim + nx;
                if (_visDist[j] <= nd) continue;
                _visDist[j] = nd;
                _bfsQ[qt++] = j;
            }
        }

        float edge = Mathf.Max(0.75f, fadeEdgeMeters);
        float ts = 1f;
        if (playerVision != null && playerVision.mapGrid != null && playerVision.mapGrid.IsReady)
            ts = Mathf.Max(0.25f, playerVision.mapGrid.TileSize);

        for (int i = 0; i < n; i++)
        {
            float meters = _visDist[i] * ts;
            float w = Mathf.Clamp01(meters / edge);
            byte v = (byte)Mathf.RoundToInt(w * 255f);
            _cellMaskPixels[i] = new Color32(v, v, v, v);
        }

        ShiftFadePersist(_maskOx, _maskOz);
        ApplyFadePersist();

        for (int z = 0; z < MaskDim; z++)
        {
            for (int x = 0; x < MaskDim; x++)
            {
                int i = z * MaskDim + x;
                int acc = _cellMaskPixels[i].r * 4;
                int ww = 4;
                if (x > 0) { acc += _cellMaskPixels[i - 1].r; ww++; }
                if (x + 1 < MaskDim) { acc += _cellMaskPixels[i + 1].r; ww++; }
                if (z > 0) { acc += _cellMaskPixels[i - MaskDim].r; ww++; }
                if (z + 1 < MaskDim) { acc += _cellMaskPixels[i + MaskDim].r; ww++; }
                byte v = (byte)Mathf.Clamp(acc / ww, 0, 255);
                _maskBlur[i] = new Color32(v, v, v, v);
            }
        }

        PunchCoverOpaque();

        _cellMask.SetPixels32(_maskBlur);
        _cellMask.Apply(false, false);
        _maskOk = _visibleSet.Count > 0 || _fadeRestoring;
    }

    private void ShiftFadePersist(int newOx, int newOz)
    {
        if (_persistOx == int.MinValue)
        {
            System.Array.Clear(_fadePersist, 0, _fadePersist.Length);
            System.Array.Clear(_fadeHold, 0, _fadeHold.Length);
            _persistOx = newOx;
            _persistOz = newOz;
            return;
        }

        int dx = newOx - _persistOx;
        int dz = newOz - _persistOz;
        if (dx == 0 && dz == 0) return;

        ShiftFloatGrid(_fadePersist, dx, dz);
        ShiftFloatGrid(_fadeHold, dx, dz);
        _persistOx = newOx;
        _persistOz = newOz;
    }

    private void ShiftFloatGrid(float[] src, int dx, int dz)
    {
        System.Array.Clear(_fadeScratch, 0, _fadeScratch.Length);
        for (int z = 0; z < MaskDim; z++)
        {
            int oldz = z + dz;
            if ((uint)oldz >= MaskDim) continue;
            for (int x = 0; x < MaskDim; x++)
            {
                int oldx = x + dx;
                if ((uint)oldx >= MaskDim) continue;
                _fadeScratch[z * MaskDim + x] = src[oldz * MaskDim + oldx];
            }
        }
        System.Array.Copy(_fadeScratch, src, src.Length);
    }

    private void ApplyFadePersist()
    {
        float now = Time.time;
        float dt = _lastFadeTime > 0f ? Mathf.Max(0f, now - _lastFadeTime) : 0f;
        _lastFadeTime = now;

        float delay = Mathf.Max(0f, fadeRestoreDelay);
        float restore = Mathf.Max(0.01f, fadeRestoreTime);
        bool restoring = false;
        int n = MaskDim * MaskDim;

        for (int i = 0; i < n; i++)
        {
            float target = _cellMaskPixels[i].r / 255f;
            float cur = _fadePersist[i];
            if (target >= cur - 0.001f)
            {
                cur = target;
                _fadeHold[i] = delay;
            }
            else
            {
                float hold = _fadeHold[i];
                if (hold > 0f)
                {
                    hold -= dt;
                    _fadeHold[i] = hold;
                    restoring = true;
                }
                if (hold <= 0f)
                {
                    cur = Mathf.MoveTowards(cur, target, dt / restore);
                    if (cur > target + 0.001f) restoring = true;
                }
            }
            _fadePersist[i] = cur;
            byte v = (byte)Mathf.RoundToInt(cur * 255f);
            _cellMaskPixels[i] = new Color32(v, v, v, v);
        }

        _fadeRestoring = restoring;
    }

    public bool IsCellFading(int cx, int cz)
    {
        if (!useVisionFade || !_visibleSet.Contains(new Vector2Int(cx, cz))) return false;
        return !CellIsCover(cx, cz);
    }

    private bool CellIsCover(int cx, int cz)
    {
        MapGrid grid = playerVision != null ? playerVision.mapGrid : null;
        if (grid == null || !grid.IsReady) return false;
        grid.GetEffectiveSightCover(cx, cz, out var mode, out _);
        return mode != MapGrid.SightCoverMode.None;
    }

    private void PunchCoverOpaque()
    {
        MapGrid grid = playerVision != null ? playerVision.mapGrid : null;
        if (grid == null || !grid.IsReady) return;

        for (int z = 0; z < MaskDim; z++)
        {
            for (int x = 0; x < MaskDim; x++)
            {
                if (!CellIsCover(_maskOx + x, _maskOz + z)) continue;
                int i = z * MaskDim + x;
                _maskBlur[i] = new Color32(0, 0, 0, 0);
                _fadePersist[i] = 0f;
                _fadeHold[i] = 0f;
            }
        }
    }

    private void DrawInstanced()
    {
        if (placement.terrainBuilder == null || placement.heightSource == null) return;
        if (placement.allVariants == null) return;
        EnsureFadeMaterial();

        float ts = placement.terrainBuilder.TileSize;
        float sectorWorld = placement.sectorSize * ts;
        int w = placement.heightSource.width;
        int d = placement.heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);

        int pcx = Mathf.FloorToInt((player.position.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((player.position.z - origin.z) / sectorWorld);
        int r = Mathf.CeilToInt(drawRadius / sectorWorld);

        for (int vi = 0; vi < placement.allVariants.Count; vi++)
        {
            var v = placement.allVariants[vi];
            if (v.sectors == null || v.parts == null || v.parts.Count == 0) continue;

            var layer = placement.variantLayer[vi];
            var shadows = v.castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;

            _opaque.Clear();
            _faded.Clear();

            bool treeFade = useVisionFade && layer.IsTree && FadeShaderOk();

            for (int sx = pcx - r; sx <= pcx + r; sx++)
            {
                for (int sz = pcz - r; sz <= pcz + r; sz++)
                {
                    if (!v.sectors.TryGetValue(new Vector2Int(sx, sz), out var batches)) continue;
                    foreach (var batch in batches)
                    {
                        if (batch == null) continue;
                        for (int i = 0; i < batch.Length; i++)
                        {
                            if (treeFade) _faded.Add(batch[i]);
                            else _opaque.Add(batch[i]);
                        }
                    }
                }
            }

            if (treeFade)
                DrawParts(v, _faded, null, shadows, fade: true);
            else
                DrawParts(v, _opaque, v.propertyBlock, shadows, fade: false);
        }
    }

    private bool FadeShaderOk()
    {
        if (_fadeShaderChecked) return _fadeShaderOkCache;
        Shader s = Shader.Find("Nature/VisionFade");
        _fadeShaderChecked = true;
        if (s == null || s.name.IndexOf("Error", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _fadeShaderOkCache = false;
            return false;
        }
        _fadeShaderOkCache = s.isSupported;
        return _fadeShaderOkCache;
    }

    private Material GetFadeMaterial(Material src)
    {
        Shader fadeSh = Shader.Find("Nature/VisionFade");
        if (fadeSh == null) return src;
        if (src == null) return fadeMaterial;

        int id = src.GetInstanceID();
        if (_fadeBySrc.TryGetValue(id, out var cached) && cached != null)
        {
            if (cached.shader != fadeSh) cached.shader = fadeSh;
            cached.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return cached;
        }

        var m = new Material(src);
        m.shader = fadeSh;
        m.enableInstancing = true;
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.name = src.name + "_VisionFade";
        CopyAlbedoToMaterial(src, m);
        _fadeBySrc[id] = m;
        return m;
    }

    private static void CopyAlbedo(Material src, MaterialPropertyBlock dst)
    {
        if (src == null || dst == null) return;
        Texture tex = src.mainTexture;
        if (tex == null && src.HasProperty(MainTexId)) tex = src.GetTexture(MainTexId);
        if (tex == null && src.HasProperty(BaseMapId)) tex = src.GetTexture(BaseMapId);
        if (tex != null) dst.SetTexture(MainTexId, tex);

        if (src.HasProperty(ColorId)) dst.SetColor(ColorId, src.GetColor(ColorId));
        else if (src.HasProperty(BaseColorId)) dst.SetColor(ColorId, src.GetColor(BaseColorId));
    }

    private static void CopyAlbedoToMaterial(Material src, Material dst)
    {
        if (src == null || dst == null) return;
        Texture tex = src.mainTexture;
        if (tex == null && src.HasProperty(MainTexId)) tex = src.GetTexture(MainTexId);
        if (tex == null && src.HasProperty(BaseMapId)) tex = src.GetTexture(BaseMapId);
        if (tex != null && dst.HasProperty(MainTexId)) dst.SetTexture(MainTexId, tex);

        if (dst.HasProperty(ColorId))
        {
            if (src.HasProperty(ColorId)) dst.SetColor(ColorId, src.GetColor(ColorId));
            else if (src.HasProperty(BaseColorId)) dst.SetColor(ColorId, src.GetColor(BaseColorId));
        }
    }

    private void DrawParts(NaturePlacement.NatureVariant v, List<Matrix4x4> roots,
        MaterialPropertyBlock block, UnityEngine.Rendering.ShadowCastingMode shadows, bool fade)
    {
        if (roots.Count == 0 || v.parts == null) return;
        for (int p = 0; p < v.parts.Count; p++)
        {
            var part = v.parts[p];
            if (part.mesh == null || part.material == null) continue;

            Material mat = part.material;
            MaterialPropertyBlock pb = block;
            bool ident = part.localToRoot.isIdentity;
            List<Matrix4x4> list = roots;
            if (!ident)
            {
                _partBatch.Clear();
                for (int i = 0; i < roots.Count; i++)
                    _partBatch.Add(roots[i] * part.localToRoot);
                list = _partBatch;
            }

            if (fade)
            {
                mat = GetFadeMaterial(part.material);
                if (mat == null) mat = fadeMaterial != null ? fadeMaterial : part.material;
                if (_fadeBlock == null) _fadeBlock = new MaterialPropertyBlock();
                _fadeBlock.Clear();
                CopyAlbedo(part.material, _fadeBlock);
                Matrix4x4 partInv = ident ? Matrix4x4.identity : part.localToRoot.inverse;
                _fadeBlock.SetMatrix(VisionPartInvId, partInv);
                _fadeBlock.SetFloat(VisionFadePaleId, fadePale);
                pb = _fadeBlock;
            }

            DrawList(part.mesh, mat, pb, list, shadows, part.submesh);
        }
    }

    private void DrawList(Mesh mesh, Material mat, MaterialPropertyBlock block,
        List<Matrix4x4> list, UnityEngine.Rendering.ShadowCastingMode shadows, int submesh = 0)
    {
        if (list.Count == 0 || mesh == null || mat == null) return;
        if (submesh < 0 || submesh >= mesh.subMeshCount) submesh = 0;
        mat.enableInstancing = true;
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

        float ts = placement.terrainBuilder.TileSize;
        float sectorWorld = placement.sectorSize * ts;
        int w = placement.heightSource.width;
        int d = placement.heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);

        int pcx = Mathf.FloorToInt((pp.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((pp.z - origin.z) / sectorWorld);
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

    void OnDestroy()
    {
        if (_cellMask != null)
        {
            if (Application.isPlaying) Destroy(_cellMask);
            else DestroyImmediate(_cellMask);
            _cellMask = null;
        }
        foreach (var kv in _fadeBySrc)
        {
            if (kv.Value == null) continue;
            if (Application.isPlaying) Destroy(kv.Value);
            else DestroyImmediate(kv.Value);
        }
        _fadeBySrc.Clear();
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

                bool faded = IsCellFading(x, z);
                GUI.color = faded ? treeLetterFadedColor : treeLetterColor;
                GUI.Label(new Rect(sp.x - 6f, Screen.height - sp.y - 8f, 20f, 18f), "T");
            }
        }
        GUI.color = Color.white;
    }
}
