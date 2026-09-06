using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Отрисовка + hybrid live + стриминг.
/// Fade: если ветка/крона заслоняет видимую клетку — прозрачно от этой высоты и выше по тому же дереву.
/// Низ keepBottomFraction плотный. Live-префаб — только коллайдер; картинка и fade всегда инстансом.
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
    [Tooltip("Прозрачность кроны на маске. 0 = плотная, 1 = не видна.")]
    [Range(0f, 1f)] public float fadeTransparency = 0.95f;
    [Tooltip("Насколько обесцветить крону в fade.")]
    [Range(0f, 1f)] public float fadePale = 0.45f;
    [Tooltip("Нижняя доля высоты дерева остаётся непрозрачной.")]
    [Range(0f, 0.5f)] public float keepBottomFraction = 0.2f;
    [Tooltip("Минимум непрозрачного низа, метры.")]
    public float keepBottomHeight = 0f;
    [Tooltip("Радиус луча угол контура → камера (м).")]
    public float occlusionRayRadius = 1.8f;
    [Tooltip("Сколько углов золотого контура (4–12).")]
    [Range(4, 12)] public int fadeCornerCount = 8;
    [Tooltip("Под игроком низ не режется (м по земле). Крона рядом — наоборот полностью прозрачная.")]
    public float playerKeepRadius = 2.2f;
    [Tooltip("Внутри этого радиуса крона на маске полностью прозрачна.")]
    public float fadeNearRadius = 8f;
    [Tooltip("За fadeNearRadius ещё столько метров до значения со шкалы.")]
    public float fadeNearFalloff = 6f;
    [Tooltip("Разгон / спад прозрачности, сек.")]
    public float fadeSeconds = 0.18f;
    [Tooltip("После того как дерево больше не заслоняет зрение — столько секунд ещё прозрачное, потом отрастает.")]
    public float fadeReturnDelay = 2f;
    public Material fadeMaterial;

    [Header("Live")]
    public int liveCheckEveryNFrames = 8;
    public Transform liveRoot;

    [Header("Стриминг")]
    public int streamCheckEveryNFrames = 15;

    [Header("Gizmo")]
    public bool drawFadeGizmos = true;
    public Color fadeGizmoColor = new Color(0.2f, 0.95f, 0.35f, 0.7f);
    [Tooltip("Буква T на клетках с деревом. Жёлтая — крона сейчас режется.")]
    public bool drawTreeLetters = true;
    public Color treeLetterColor = new Color(0.2f, 0.85f, 0.25f, 1f);
    public Color treeLetterFadedColor = new Color(1f, 0.82f, 0.12f, 1f);

    public static NatureRenderer Active { get; private set; }

    private readonly Dictionary<long, LiveEntry> _live = new Dictionary<long, LiveEntry>();
    private readonly HashSet<long> _liveKeys = new HashSet<long>();
    private readonly List<long> _toRemove = new List<long>();

    private readonly List<Matrix4x4> _opaque = new List<Matrix4x4>(256);
    private readonly List<Matrix4x4> _faded = new List<Matrix4x4>(256);
    private readonly List<float> _fadedW = new List<float>(256);
    private readonly List<float> _fadedFrom = new List<float>(256);
    private readonly List<Matrix4x4> _partBatch = new List<Matrix4x4>(256);
    private readonly Dictionary<long, float> _fadeAmt = new Dictionary<long, float>(256);
    private readonly Dictionary<long, float> _fadeFromFrac = new Dictionary<long, float>(256);
    private readonly Dictionary<long, float> _fadeReturnAt = new Dictionary<long, float>(256);
    private readonly HashSet<long> _fadeSeen = new HashSet<long>();
    private readonly List<long> _fadeDead = new List<long>();
    private readonly List<Vector3> _contourPts = new List<Vector3>(256);
    private readonly List<Vector3> _hullPts = new List<Vector3>(64);
    private readonly List<Vector3> _fieldPts = new List<Vector3>(16);
    private readonly Vector4[] _volumePlanes = new Vector4[12];
    private Vector4 _fieldPlane;
    private int _planeCount;
    private readonly List<Bounds> _variantBounds = new List<Bounds>(16);
    private bool _fieldOk;
    private readonly List<Vector2Int> _visibleCells = new List<Vector2Int>(512);
    private readonly HashSet<Vector2Int> _visibleSet = new HashSet<Vector2Int>();
    private Texture2D _cellMask;
    private Color32[] _cellMaskPixels;
    private int _maskOx, _maskOz;
    private bool _maskOk;
    private const int MaskDim = 64;

    private MaterialPropertyBlock _fadeBlock;
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private readonly Dictionary<int, Material> _fadeBySrc = new Dictionary<int, Material>();
    private static readonly int VisionFadeAlphaId = Shader.PropertyToID("_VisionFadeAlpha");
    private static readonly int VisionKeepBottomId = Shader.PropertyToID("_VisionKeepBottom");
    private static readonly int VisionGroundYId = Shader.PropertyToID("_VisionGroundY");
    private static readonly int VisionCellMaskTexId = Shader.PropertyToID("_VisionCellMaskTex");
    private static readonly int VisionMapOriginId = Shader.PropertyToID("_VisionMapOrigin");
    private static readonly int VisionTileSizeId = Shader.PropertyToID("_VisionTileSize");
    private static readonly int VisionMaskOriginId = Shader.PropertyToID("_VisionMaskOrigin");
    private static readonly int VisionMaskDimId = Shader.PropertyToID("_VisionMaskDim");
    private static readonly int VisionMaskOnId = Shader.PropertyToID("_VisionMaskOn");
    private static readonly int VisionPlayerXZId = Shader.PropertyToID("_VisionPlayerXZ");
    private static readonly int VisionKeepRadiusId = Shader.PropertyToID("_VisionKeepRadius");
    private static readonly int VisionFadeWeightId = Shader.PropertyToID("_VisionFadeWeight");
    private static readonly int VisionKeepFractionId = Shader.PropertyToID("_VisionKeepFraction");
    private static readonly int VisionTreeLocalHeightId = Shader.PropertyToID("_VisionTreeLocalHeight");
    private static readonly int VisionPartInvId = Shader.PropertyToID("_VisionPartInv");
    private static readonly int VisionFadePaleId = Shader.PropertyToID("_VisionFadePale");
    private static readonly int VisionFadeSoftId = Shader.PropertyToID("_VisionFadeSoft");
    private static readonly int VisionCamFwdId = Shader.PropertyToID("_VisionCamFwd");
    private static readonly int VisionNearFadeId = Shader.PropertyToID("_VisionNearFade");
    private static readonly int VisionNearFalloffId = Shader.PropertyToID("_VisionNearFalloff");
    private static readonly int VisionFadeFromFracId = Shader.PropertyToID("_VisionFadeFromFrac");

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
            BuildVisibleMask();
            PushFadeGlobals();
        }
        else
        {
            _fieldOk = false;
            _planeCount = 0;
            _maskOk = false;
        }

        DrawInstanced();

        if (Time.frameCount % Mathf.Max(1, liveCheckEveryNFrames) == 0)
            UpdateLive();

        if (drawFadeGizmos)
            DrawFadeVolume(debugLines: true);
    }


    private void ResolveRefs()
    {
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

    private void EnsureFadeMaterial()
    {
#if UNITY_EDITOR
        SyncFadeShaderAsset();
#endif
        Shader sh = Shader.Find("Nature/VisionFade");
        if (sh == null) return;
        if (fadeMaterial == null) fadeMaterial = new Material(sh);
        else fadeMaterial.shader = sh;
        fadeMaterial.enableInstancing = true;
        fadeMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

#if UNITY_EDITOR
    private static void SyncFadeShaderAsset()
    {
        string path = System.IO.Path.Combine(Application.dataPath, "NatureVisionFade.shader");
        string code = FadeShaderCode;
        if (!System.IO.File.Exists(path) || System.IO.File.ReadAllText(path) != code)
        {
            System.IO.File.WriteAllText(path, code);
            UnityEditor.AssetDatabase.ImportAsset("Assets/NatureVisionFade.shader");
        }
    }

    const string FadeShaderCode = @"Shader ""Nature/VisionFade""
{
    Properties
    {
        _Color (""Color"", Color) = (1,1,1,1)
        _MainTex (""Texture"", 2D) = ""white"" {}
    }
    SubShader
    {
        Tags
        {
            ""RenderPipeline"" = ""UniversalPipeline""
            ""Queue"" = ""Transparent""
            ""RenderType"" = ""Transparent""
            ""IgnoreProjector"" = ""True""
        }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name ""Fade""
            Tags { ""LightMode"" = ""SRPDefaultUnlit"" }
            HLSLPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma multi_compile_instancing
# include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_VisionCellMaskTex);
            SAMPLER(sampler_PointClamp);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
            CBUFFER_END

            float _VisionFadeAlpha;
            float _VisionFadeSoft;
            float _VisionKeepBottom;
            float _VisionKeepFraction;
            float _VisionTreeLocalHeight;
            float4x4 _VisionPartInv;
            float4 _VisionMapOrigin;
            float _VisionTileSize;
            float4 _VisionMaskOrigin;
            float _VisionMaskDim;
            float _VisionMaskOn;
            float4 _VisionPlayerXZ;
            float4 _VisionCamFwd;
            float _VisionGroundY;
            float _VisionKeepRadius;
            float _VisionNearFade;
            float _VisionNearFalloff;
            float _VisionFadeWeight;
            float _VisionFadePale;
            float _VisionFadeFromFrac;

            float3 ViewGround(float3 wp)
            {
                float3 dir = _VisionCamFwd.xyz;
                float gy = _VisionGroundY;
                if (abs(dir.y) < 1e-4)
                    return float3(wp.x, gy, wp.z);
                float t = (gy - wp.y) / dir.y;
                return wp + dir * t;
            }

            bool OnVisibleCell(float3 wp)
            {
                if (_VisionMaskOn < 0.5 || _VisionTileSize < 0.001) return false;
                float cx = floor((wp.x - _VisionMapOrigin.x) / _VisionTileSize);
                float cz = floor((wp.z - _VisionMapOrigin.y) / _VisionTileSize);
                float dim = max(_VisionMaskDim, 1.0);
                float u = (cx - _VisionMaskOrigin.x + 0.5) / dim;
                float v = (cz - _VisionMaskOrigin.y + 0.5) / dim;
                if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0) return false;
                return SAMPLE_TEXTURE2D(_VisionCellMaskTex, sampler_PointClamp, float2(u, v)).r > 0.5;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(ws);
                o.uv = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                o.worldPos = ws;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * (half4)_Color;
                if (col.a < 0.02)
                    discard;

                float4x4 root = mul(unity_ObjectToWorld, _VisionPartInv);
                float3 basePos = float3(root._m03, root._m13, root._m23);
                float scaleY = length(float3(root._m01, root._m11, root._m21));
                float h = max(_VisionTreeLocalHeight * scaleY, 0.001);
                float keepH = max(h * saturate(_VisionKeepFraction), max(_VisionKeepBottom, 0.0));
                float cutH = max(keepH, h * saturate(_VisionFadeFromFrac));
                if (i.worldPos.y < basePos.y + cutH)
                    return col;

                float w = saturate(_VisionFadeWeight);
                float targetA = lerp(1.0, saturate(_VisionFadeAlpha), saturate(_VisionFadeSoft));
                float2 dp = i.worldPos.xz - _VisionPlayerXZ.xy;
                float dist = length(dp);
                float inner = max(_VisionNearFade, 0.0);
                float outer = inner + max(_VisionNearFalloff, 0.01);
                float nearT = saturate((dist - inner) / (outer - inner));
                targetA *= nearT;
                float aMul = lerp(1.0, targetA, w);
                col.a *= aMul;
                float seeThrough = saturate((1.0 - aMul) / max(1.0 - targetA, 0.001));
                col.rgb = lerp(col.rgb, col.rgb * (1.0 - _VisionFadePale) + _VisionFadePale, seeThrough);
                return col;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
";
#endif

    private void PushFadeGlobals()
    {
        Shader.SetGlobalFloat(VisionFadeAlphaId, 1f - Mathf.Clamp01(fadeTransparency));
        Shader.SetGlobalFloat(VisionKeepBottomId, Mathf.Max(0f, keepBottomHeight));
        Shader.SetGlobalFloat(VisionKeepFractionId, Mathf.Clamp01(keepBottomFraction));
        Shader.SetGlobalFloat(VisionFadePaleId, Mathf.Clamp01(fadePale));
        Shader.SetGlobalFloat(VisionFadeSoftId, 1f);
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
        if (player != null)
            Shader.SetGlobalVector(VisionPlayerXZId, new Vector4(player.position.x, player.position.z, 0f, 0f));
        if (cam != null)
        {
            Vector3 cf = cam.transform.forward;
            Shader.SetGlobalVector(VisionCamFwdId, new Vector4(cf.x, cf.y, cf.z, 0f));
        }
        Shader.SetGlobalFloat(VisionKeepRadiusId, Mathf.Max(0f, playerKeepRadius));
        Shader.SetGlobalFloat(VisionNearFadeId, Mathf.Max(0f, fadeNearRadius));
        Shader.SetGlobalFloat(VisionNearFalloffId, Mathf.Max(0.01f, fadeNearFalloff));
        Shader.SetGlobalFloat(VisionFadeFromFracId, 0f);
    }

    private void EnsureCellMask()
    {
        if (_cellMask != null) return;
        _cellMask = new Texture2D(MaskDim, MaskDim, TextureFormat.RGBA32, false, true);
        _cellMask.filterMode = FilterMode.Point;
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

        var on = new Color32(255, 255, 255, 255);
        for (int i = 0; i < _visibleCells.Count; i++)
        {
            int lx = _visibleCells[i].x - _maskOx;
            int lz = _visibleCells[i].y - _maskOz;
            if ((uint)lx >= MaskDim || (uint)lz >= MaskDim) continue;
            _cellMaskPixels[lz * MaskDim + lx] = on;
        }
        _cellMask.SetPixels32(_cellMaskPixels);
        _cellMask.Apply(false, false);
        _maskOk = true;
    }

    private void BuildFieldFromGoldContour()
    {
        _fieldPts.Clear();
        _contourPts.Clear();
        _hullPts.Clear();
        _fieldOk = false;

        if (playerVision != null)
            playerVision.CollectOuterContourPoints(_contourPts);

        if (_contourPts.Count >= 3)
        {
            ConvexHullXZ(_contourPts, _hullPts);
            PickPrincipalCorners(_hullPts, _fieldPts, Mathf.Clamp(fadeCornerCount, 4, 12));
        }

        _fieldOk = _fieldPts.Count >= 3;
    }

    private void BuildVolume()
    {
        _planeCount = 0;
        if (!_fieldOk || cam == null) return;

        int n = Mathf.Min(_fieldPts.Count, 12);
        Vector3 center = Vector3.zero;
        for (int i = 0; i < n; i++)
            center += _fieldPts[i];
        center /= n;

        for (int i = 0; i < 12; i++)
            _volumePlanes[i] = Vector4.zero;

        for (int i = 0; i < n; i++)
        {
            Vector3 a = _fieldPts[i];
            Vector3 b = _fieldPts[(i + 1) % n];
            Vector3 na = PointOnNearPlane(a);
            _volumePlanes[i] = PackPlane(a, b, na, center);
        }

        _fieldPlane = PackPlane(
            _fieldPts[0], _fieldPts[1], _fieldPts[Mathf.Min(2, n - 1)], cam.transform.position);
        _planeCount = n;
    }

    private static Vector4 PackPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 insidePoint)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        if (n.sqrMagnitude < 1e-8f) return Vector4.zero;
        n.Normalize();
        if (Vector3.Dot(n, insidePoint - a) < 0f) n = -n;
        return new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, a));
    }

    private Vector3 PointOnNearPlane(Vector3 fieldPt)
    {
        if (cam == null) return fieldPt + Vector3.up * 20f;
        return CameraFollow.ProjectToFrame(cam, fieldPt);
    }

    private bool TryFadeFrom(Bounds b, Matrix4x4 root, float localH, int footprint, out float fromFrac)
    {
        fromFrac = 0f;
        if (!_maskOk || _visibleSet.Count == 0) return false;
        MapGrid grid = playerVision != null ? playerVision.mapGrid : null;
        if (grid == null || !grid.IsReady) return false;

        Vector3 dir = cam != null ? cam.transform.forward : Vector3.down;
        float gy = player != null ? player.position.y : b.min.y;
        float pivotY = root.m13;
        float scaleY = ((Vector3)root.GetColumn(1)).magnitude;
        float hWorld = Mathf.Max(localH * scaleY, 0.01f);

        Vector3 pos = root.GetColumn(3);
        grid.WorldToCell(pos, out int ox, out int oz);
        int fp = Mathf.Max(1, footprint);
        int x0 = ox - fp / 2;
        int z0 = oz - fp / 2;
        int x1 = x0 + fp - 1;
        int z1 = z0 + fp - 1;

        Vector3 c = b.center;
        Vector3 e = b.extents;
        const int ny = 8;
        for (int iy = 0; iy <= ny; iy++)
        {
            float y = b.min.y + (b.size.y * iy) / ny;
            bool bandHit = false;
            for (int i = 0; i < 5; i++)
            {
                float sx = 0f, sz = 0f;
                if (i == 1) sx = 1f;
                else if (i == 2) sx = -1f;
                else if (i == 3) sz = 1f;
                else if (i == 4) sz = -1f;
                Vector3 p = new Vector3(c.x + e.x * 0.65f * sx, y, c.z + e.z * 0.65f * sz);
                Vector3 g = ProjectAlongView(p, dir, gy);
                grid.WorldToCell(g, out int cx, out int cz);
                if (cx >= x0 && cx <= x1 && cz >= z0 && cz <= z1)
                    continue;
                if (!_visibleSet.Contains(new Vector2Int(cx, cz)))
                    continue;
                bandHit = true;
                break;
            }
            if (!bandHit) continue;
            fromFrac = Mathf.Clamp01((y - pivotY) / hWorld);
            return true;
        }
        return false;
    }

    private static Vector3 ProjectAlongView(Vector3 wp, Vector3 dir, float groundY)
    {
        if (Mathf.Abs(dir.y) < 1e-4f) return new Vector3(wp.x, groundY, wp.z);
        float t = (groundY - wp.y) / dir.y;
        return wp + dir * t;
    }

    private float StepFade(long key, bool want)
    {
        _fadeSeen.Add(key);
        _fadeAmt.TryGetValue(key, out float w);
        if (want)
        {
            _fadeReturnAt.Remove(key);
        }
        else if (w > 0.001f)
        {
            if (!_fadeReturnAt.TryGetValue(key, out float at))
            {
                at = Time.time + Mathf.Max(0f, fadeReturnDelay);
                _fadeReturnAt[key] = at;
            }
            if (Time.time < at)
            {
                _fadeAmt[key] = w;
                return w;
            }
        }
        float step = Time.deltaTime / Mathf.Max(0.05f, fadeSeconds);
        w = Mathf.MoveTowards(w, want ? 1f : 0f, step);
        if (w <= 0.001f)
        {
            _fadeAmt.Remove(key);
            _fadeFromFrac.Remove(key);
            _fadeReturnAt.Remove(key);
            return 0f;
        }
        _fadeAmt[key] = w;
        return w;
    }

    public bool IsCellFading(int cx, int cz)
    {
        if (!useVisionFade) return false;
        MapGrid grid = playerVision != null ? playerVision.mapGrid : null;
        if (grid != null && grid.IsReady && player != null)
        {
            Vector3 c = grid.CellCenterWorld(cx, cz);
            float dx = c.x - player.position.x;
            float dz = c.z - player.position.z;
            float kr = playerKeepRadius;
            if (dx * dx + dz * dz <= kr * kr) return false;
        }
        return _visibleSet.Contains(new Vector2Int(cx, cz));
    }

    private void PruneFadeWeights()
    {
        _fadeDead.Clear();
        foreach (var kv in _fadeAmt)
        {
            if (!_fadeSeen.Contains(kv.Key))
                _fadeDead.Add(kv.Key);
        }
        for (int i = 0; i < _fadeDead.Count; i++)
        {
            _fadeAmt.Remove(_fadeDead[i]);
            _fadeFromFrac.Remove(_fadeDead[i]);
            _fadeReturnAt.Remove(_fadeDead[i]);
        }
    }


    private static Bounds TransformBounds(Bounds local, Matrix4x4 m)
    {
        Vector3 c = m.MultiplyPoint3x4(local.center);
        Vector3 e = local.extents;
        Vector3 ax = (Vector3)m.GetColumn(0) * e.x;
        Vector3 ay = (Vector3)m.GetColumn(1) * e.y;
        Vector3 az = (Vector3)m.GetColumn(2) * e.z;
        Vector3 we = new Vector3(
            Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
            Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
            Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
        return new Bounds(c, we * 2f);
    }

    private static Bounds EncapsulateParts(NaturePlacement.NatureVariant v)
    {
        var acc = new Bounds(Vector3.zero, Vector3.one * 2f);
        bool any = false;
        if (v.parts == null) return acc;
        for (int i = 0; i < v.parts.Count; i++)
        {
            var part = v.parts[i];
            if (part.mesh == null) continue;
            Bounds pb = TransformBounds(part.mesh.bounds, part.localToRoot);
            if (!any) { acc = pb; any = true; }
            else acc.Encapsulate(pb);
        }
        return acc;
    }

    private void EnsureVariantBounds()
    {
        _variantBounds.Clear();
        if (placement == null || placement.allVariants == null) return;
        for (int i = 0; i < placement.allVariants.Count; i++)
            _variantBounds.Add(EncapsulateParts(placement.allVariants[i]));
    }

    private static void ConvexHullXZ(List<Vector3> src, List<Vector3> dst)
    {
        dst.Clear();
        int n = src.Count;
        if (n == 0) return;

        var pts = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
            pts.Add(src[i]);
        pts.Sort((a, b) =>
        {
            int cx = a.x.CompareTo(b.x);
            return cx != 0 ? cx : a.z.CompareTo(b.z);
        });

        int w = 1;
        for (int i = 1; i < pts.Count; i++)
        {
            if (Mathf.Abs(pts[i].x - pts[w - 1].x) < 0.001f
                && Mathf.Abs(pts[i].z - pts[w - 1].z) < 0.001f)
                continue;
            pts[w++] = pts[i];
        }
        if (w < 3)
        {
            for (int i = 0; i < w; i++)
                dst.Add(pts[i]);
            return;
        }
        pts.RemoveRange(w, pts.Count - w);

        var lower = new List<Vector3>();
        for (int i = 0; i < pts.Count; i++)
        {
            while (lower.Count >= 2 && CrossXZ(lower[lower.Count - 2], lower[lower.Count - 1], pts[i]) <= 0f)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(pts[i]);
        }
        var upper = new List<Vector3>();
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && CrossXZ(upper[upper.Count - 2], upper[upper.Count - 1], pts[i]) <= 0f)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(pts[i]);
        }
        if (lower.Count > 0) lower.RemoveAt(lower.Count - 1);
        if (upper.Count > 0) upper.RemoveAt(upper.Count - 1);
        dst.AddRange(lower);
        dst.AddRange(upper);
    }

    private static float CrossXZ(Vector3 a, Vector3 b, Vector3 c)
    {
        return (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
    }

    private static void PickPrincipalCorners(List<Vector3> hull, List<Vector3> dst, int maxCount)
    {
        dst.Clear();
        int n = hull.Count;
        if (n == 0) return;
        if (n <= maxCount)
        {
            dst.AddRange(hull);
            return;
        }

        Vector3 c = Vector3.zero;
        for (int i = 0; i < n; i++)
            c += hull[i];
        c /= n;

        int sectors = Mathf.Max(4, maxCount);
        var bestIdx = new int[sectors];
        var bestScore = new float[sectors];
        for (int s = 0; s < sectors; s++)
        {
            bestIdx[s] = -1;
            bestScore[s] = float.NegativeInfinity;
        }

        for (int i = 0; i < n; i++)
        {
            float dx = hull[i].x - c.x;
            float dz = hull[i].z - c.z;
            float ang = Mathf.Atan2(dz, dx);
            if (ang < 0f) ang += Mathf.PI * 2f;
            int s = Mathf.FloorToInt(ang / (Mathf.PI * 2f) * sectors);
            if (s < 0) s = 0;
            if (s >= sectors) s = sectors - 1;
            float score = dx * dx + dz * dz;
            if (score > bestScore[s])
            {
                bestScore[s] = score;
                bestIdx[s] = i;
            }
        }

        var picked = new List<int>(sectors);
        for (int s = 0; s < sectors; s++)
        {
            int idx = bestIdx[s];
            if (idx < 0) continue;
            bool dup = false;
            for (int k = 0; k < picked.Count; k++)
            {
                if (picked[k] == idx) { dup = true; break; }
            }
            if (!dup) picked.Add(idx);
        }
        picked.Sort();
        for (int i = 0; i < picked.Count; i++)
            dst.Add(hull[picked[i]]);
    }

    private void DrawInstanced()
    {
        if (placement.terrainBuilder == null || placement.heightSource == null) return;
        if (placement.allVariants == null) return;
        EnsureFadeMaterial();
        if (_variantBounds.Count != placement.allVariants.Count)
            EnsureVariantBounds();

        float ts = placement.terrainBuilder.TileSize;
        float sectorWorld = placement.sectorSize * ts;
        int w = placement.heightSource.width;
        int d = placement.heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);

        int pcx = Mathf.FloorToInt((player.position.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((player.position.z - origin.z) / sectorWorld);
        int r = Mathf.CeilToInt(drawRadius / sectorWorld);
        _fadeSeen.Clear();

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
            _fadedW.Clear();
            _fadedFrom.Clear();

            bool treeFade = useVisionFade && layer.IsTree && FadeShaderOk();
            int layerFp = layer.IsTree ? Mathf.Max(1, layer.footprint) : 1;
            Bounds localBounds = (vi < _variantBounds.Count)
                ? _variantBounds[vi]
                : EncapsulateParts(v);
            float localH = localBounds.size.y;

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
                            Bounds wb = TransformBounds(localBounds, batch[i]);
                            float fromFrac = 0f;
                            bool want = treeFade && TryFadeFrom(wb, batch[i], localH, layerFp, out fromFrac);
                            long key = PosHash(batch[i].GetColumn(3));
                            if (want)
                                _fadeFromFrac[key] = fromFrac;
                            float fadeW = treeFade ? StepFade(key, want) : 0f;
                            if (fadeW > 0.01f)
                            {
                                _faded.Add(batch[i]);
                                _fadedW.Add(fadeW);
                                float stored = fromFrac;
                                _fadeFromFrac.TryGetValue(key, out stored);
                                _fadedFrom.Add(stored);
                            }
                            else _opaque.Add(batch[i]);
                        }
                    }
                }
            }

            DrawParts(v, _opaque, v.propertyBlock, shadows, fade: false);
            if (_faded.Count > 0)
                DrawParts(v, _faded, null, shadows, fade: true);
        }
        PruneFadeWeights();
    }

    private bool FadeShaderOk()
    {
        Shader s = Shader.Find("Nature/VisionFade");
        if (s == null) return false;
        if (s.name.IndexOf("Error", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        return s.isSupported;
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
            if (fade)
            {
                mat = GetFadeMaterial(part.material);
                if (mat == null) mat = fadeMaterial != null ? fadeMaterial : part.material;
                if (_fadeBlock == null) _fadeBlock = new MaterialPropertyBlock();
                DrawFadedBuckets(v, part, mat, roots, shadows);
                continue;
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
            DrawList(part.mesh, mat, pb, list, shadows, part.submesh);
        }
    }

    private void DrawFadedBuckets(NaturePlacement.NatureVariant v, NaturePlacement.MeshPart part, Material mat,
        List<Matrix4x4> roots, UnityEngine.Rendering.ShadowCastingMode shadows)
    {
        const int wBuckets = 8;
        const int hBuckets = 8;
        bool ident = part.localToRoot.isIdentity;
        Matrix4x4 partInv = ident ? Matrix4x4.identity : part.localToRoot.inverse;
        float localH = EncapsulateParts(v).size.y;
        if (localH < 0.01f && part.mesh != null) localH = part.mesh.bounds.size.y;
        for (int b = 1; b <= wBuckets; b++)
        {
            float lo = (b - 1) / (float)wBuckets;
            float hi = b / (float)wBuckets;
            for (int hb = 0; hb < hBuckets; hb++)
            {
                float flo = hb / (float)hBuckets;
                float fhi = (hb + 1) / (float)hBuckets;
                _partBatch.Clear();
                int n = roots.Count;
                int wn = _fadedW.Count;
                int fn = _fadedFrom.Count;
                for (int i = 0; i < n; i++)
                {
                    float w = i < wn ? _fadedW[i] : 1f;
                    if (w <= lo || w > hi) continue;
                    float f = i < fn ? _fadedFrom[i] : 0f;
                    if (hb < hBuckets - 1)
                    {
                        if (f < flo || f >= fhi) continue;
                    }
                    else if (f < flo) continue;
                    _partBatch.Add(ident ? roots[i] : roots[i] * part.localToRoot);
                }
                if (_partBatch.Count == 0) continue;
                _fadeBlock.Clear();
                CopyAlbedo(part.material, _fadeBlock);
                _fadeBlock.SetFloat(VisionFadeWeightId, hi);
                _fadeBlock.SetFloat(VisionKeepFractionId, keepBottomFraction);
                _fadeBlock.SetFloat(VisionKeepBottomId, keepBottomHeight);
                _fadeBlock.SetFloat(VisionTreeLocalHeightId, localH);
                _fadeBlock.SetMatrix(VisionPartInvId, partInv);
                _fadeBlock.SetFloat(VisionFadePaleId, fadePale);
                _fadeBlock.SetFloat(VisionFadeFromFracId, flo);
                DrawList(part.mesh, mat, _fadeBlock, _partBatch, shadows, part.submesh);
            }
        }
    }

    private void DrawList(Mesh mesh, Material mat, MaterialPropertyBlock block,
        List<Matrix4x4> list, UnityEngine.Rendering.ShadowCastingMode shadows, int submesh = 0)
    {
        if (list.Count == 0 || mesh == null || mat == null) return;
        if (submesh < 0 || submesh >= mesh.subMeshCount) submesh = 0;
        mat.enableInstancing = true;
        const int BS = 1023;
        for (int start = 0; start < list.Count; start += BS)
        {
            int len = Mathf.Min(BS, list.Count - start);
            var slice = list.GetRange(start, len).ToArray();
            Graphics.DrawMeshInstanced(mesh, submesh, mat, slice, len, block, shadows, false);
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

    private bool _fadeDebugLines;

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

    void OnDrawGizmos()
    {
        if (!drawFadeGizmos) return;
        if (Application.isPlaying) return;
        DrawFadeVolume(debugLines: false);
    }

    private void DrawFadeVolume(bool debugLines)
    {
        _fadeDebugLines = debugLines;
        if (playerVision == null)
            playerVision = FindObjectOfType<PlayerVision>();
        if (cam == null) cam = Camera.main;

        BuildFieldFromGoldContour();
        if (!_fieldOk || _fieldPts.Count < 3)
            BuildFieldFallbackFromVisionShape();

        Vector3 probe = player != null ? player.position : transform.position;
        FadeDrawLine(probe, probe + Vector3.up * 6f, fadeGizmoColor);

        if (cam != null)
        {
            for (int i = 0; i < _fieldPts.Count; i++)
            {
                Vector3 a = _fieldPts[i];
                Vector3 b = _fieldPts[(i + 1) % _fieldPts.Count];
                Vector3 na = PointOnNearPlane(a);
                Vector3 nb = PointOnNearPlane(b);
                FadeDrawLine(a, b, fadeGizmoColor);
                FadeDrawLine(a, na, fadeGizmoColor);
                FadeDrawLine(na, nb, fadeGizmoColor);
            }
        }
    }

    private void BuildFieldFallbackFromVisionShape()
    {
        _fieldPts.Clear();
        _fieldOk = false;
        if (playerVision == null || player == null) return;

        Vector3 eye = player.position;
        Vector3 fwd = playerVision.ForwardFlat;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        float range = Mathf.Max(4f, playerVision.RadiusMeters);
        Vector3 left = Vector3.Cross(Vector3.up, fwd).normalized;
        _fieldPts.Add(eye);
        _fieldPts.Add(eye + (-left) * range);
        _fieldPts.Add(eye + fwd * range);
        _fieldPts.Add(eye + left * range);
        _fieldOk = _fieldPts.Count >= 3;
    }

    private void FadeDrawLine(Vector3 a, Vector3 b, Color c)
    {
        if (_fadeDebugLines)
            Debug.DrawLine(a, b, c);
        else
        {
            Gizmos.color = c;
            Gizmos.DrawLine(a, b);
        }
    }
}
