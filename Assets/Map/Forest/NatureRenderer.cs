using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Отрисовка + hybrid live + стриминг + fade.
/// Гистерезис live и fade — без мерцания при подходе/отходе.
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

    [Header("Fade (прозрачность)")]
    [Range(0.05f, 1f)] public float fadeAlpha = 0.28f;
    [Tooltip("Ширина коридора камера→цель (м). Для камеры 45° лучше 6–10.")]
    public float cameraCorridorHalfWidth = 8f;
    [Tooltip("Макс. клеток зрения для коридоров камера→клетка")]
    public int maxVisionCellsForFade = 64;
    [Tooltip("Запас, чтобы не мигало на границе коридора")]
    public float fadeHysteresis = 1.2f;
    [Tooltip("Скорость смены альфы (1/сек)")]
    public float fadeLerpSpeed = 6f;
    public Material fadeMaterial;

    [Header("Live")]
    public int liveCheckEveryNFrames = 8;
    public Transform liveRoot;

    [Header("Стриминг")]
    public int streamCheckEveryNFrames = 15;

    private readonly Dictionary<long, LiveEntry> _live = new Dictionary<long, LiveEntry>();
    private readonly HashSet<long> _liveKeys = new HashSet<long>();
    private readonly List<long> _toRemove = new List<long>();

    // fade state: target 0..1 (1 = full fade), current smoothed
    private readonly Dictionary<long, float> _fadeCurrent = new Dictionary<long, float>();
    private readonly Dictionary<long, bool> _fadeLatched = new Dictionary<long, bool>();

    private readonly List<Matrix4x4> _opaque = new List<Matrix4x4>(256);
    private readonly List<Matrix4x4> _faded = new List<Matrix4x4>(128);
    private readonly List<Vector3> _fadeTargets = new List<Vector3>(64);
    private readonly List<Vector2> _fadePolygon = new List<Vector2>(8);
    private readonly List<Vector2Int> _visionCellBuf = new List<Vector2Int>(256);
    private readonly List<Vector3> _fadeGizmoPositions = new List<Vector3>(128);
    private readonly HashSet<long> _rayFadeHashes = new HashSet<long>();
    private readonly RaycastHit[] _rayHits = new RaycastHit[32];
    private MaterialPropertyBlock _fadeBlock;
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    [Header("Gizmo")]
    public bool drawFadeGizmos = true;
    public Color fadeGizmoColor = new Color(1f, 0.15f, 0.1f, 1f);

    private struct LiveEntry
    {
        public GameObject go;
        public int variantIndex;
        public Vector2Int sector;
        public Vector3 pos;
        public Renderer[] renderers;
        public Color[] originalColors;
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

        float dt = Time.deltaTime;
        UpdateFadeStates(dt);
        DrawInstanced();

        if (Time.frameCount % Mathf.Max(1, liveCheckEveryNFrames) == 0)
            UpdateLive();
        else
            ApplyLiveFade();
    }

    /// <summary>
    /// Камера и игрок появляются после Network spawn — подхватываем динамически.
    /// Приоритет: CameraFollow.target → PlayerRegistry → уже назначенный player.
    /// </summary>
    private void ResolveRefs()
    {
        // Камера
        if (cam == null)
        {
            var cf = FindObjectOfType<CameraFollow>();
            if (cf != null) cam = cf.GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
        }

        // Игрок: если пропал / ещё не был — взять с CameraFollow или реестра
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
            // CameraFollow мог сменить target на Player(Clone)
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

    // =================== Fade state ===================

    private void UpdateFadeStates(float dt)
    {
        if (placement.allVariants == null) return;

        Vector3 camPos = cam != null ? cam.transform.position : player.position + Vector3.up * 20f;
        Vector3 playerPos = player.position;

        // цели коридоров: игрок + клетки поля зрения
        RebuildFadeTargets(camPos, playerPos);
        CollectRaycastObstacles(cam != null ? cam.transform.position : camPos);

        float ts = placement.terrainBuilder.TileSize;
        float sectorWorld = placement.sectorSize * ts;
        int w = placement.heightSource.width;
        int d = placement.heightSource.depth;
        Vector3 origin = new Vector3(-w * ts / 2f, 0f, -d * ts / 2f);
        int pcx = Mathf.FloorToInt((playerPos.x - origin.x) / sectorWorld);
        int pcz = Mathf.FloorToInt((playerPos.z - origin.z) / sectorWorld);
        int r = Mathf.CeilToInt(drawRadius / sectorWorld);

        var seen = new HashSet<long>();
        _fadeGizmoPositions.Clear();

        for (int vi = 0; vi < placement.allVariants.Count; vi++)
        {
            var v = placement.allVariants[vi];
            if (v.sectors == null) continue;
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
                            Vector3 pos = batch[i].GetColumn(3);
                            long h = PosHash(pos);
                            seen.Add(h);
                            ApplyFadeLatch(h, pos, camPos, strictEnter: false);
                            float target = (_fadeLatched.TryGetValue(h, out var f) && f) ? 1f : 0f;
                            float cur = _fadeCurrent.TryGetValue(h, out var c) ? c : 0f;
                            _fadeCurrent[h] = Mathf.MoveTowards(cur, target, fadeLerpSpeed * dt);
                            if (target > 0.5f || _fadeCurrent[h] > 0.3f)
                                _fadeGizmoPositions.Add(pos);
                        }
                    }
                }
            }
        }

        foreach (var kv in _live)
        {
            long h = kv.Key;
            seen.Add(h);
            ApplyFadeLatch(h, kv.Value.pos, camPos, strictEnter: false);
            float target = (_fadeLatched.TryGetValue(h, out var f) && f) ? 1f : 0f;
            float cur = _fadeCurrent.TryGetValue(h, out var c) ? c : 0f;
            _fadeCurrent[h] = Mathf.MoveTowards(cur, target, fadeLerpSpeed * dt);
            if (target > 0.5f || _fadeCurrent[h] > 0.3f)
                _fadeGizmoPositions.Add(kv.Value.pos);
        }

        if (Time.frameCount % 60 == 0)
        {
            var dead = new List<long>();
            foreach (var k in _fadeCurrent.Keys)
                if (!seen.Contains(k)) dead.Add(k);
            for (int i = 0; i < dead.Count; i++)
            {
                _fadeCurrent.Remove(dead[i]);
                _fadeLatched.Remove(dead[i]);
            }
        }
    }

    private void RebuildFadeTargets(Vector3 camPos, Vector3 playerPos)
    {
        _fadeTargets.Clear();
        _fadePolygon.Clear();

        // реальная камера
        if (cam == null) cam = Camera.main;
        Vector3 realCam = cam != null ? cam.transform.position : camPos;
        Vector2 camXZ = new Vector2(realCam.x, realCam.z);
        Vector2 playerXZ = new Vector2(playerPos.x, playerPos.z);

        _fadeTargets.Add(playerPos);

        // дальние углы поля зрения
        var farPoints = new List<Vector2>(6);

        if (playerVision != null)
        {
            MapGrid grid = playerVision.mapGrid;
            if ((grid == null || !grid.IsReady) && placement != null && placement.mapGrid != null)
                grid = placement.mapGrid;
            if (playerVision.mapGrid == null && grid != null)
                playerVision.mapGrid = grid;

            if (grid != null && grid.IsReady)
            {
                playerVision.CollectVisibleCells(_visionCellBuf);
                if (_visionCellBuf.Count > 0)
                {
                    int minX = int.MaxValue, maxX = int.MinValue;
                    int minZ = int.MaxValue, maxZ = int.MinValue;
                    for (int i = 0; i < _visionCellBuf.Count; i++)
                    {
                        var c = _visionCellBuf[i];
                        if (c.x < minX) minX = c.x;
                        if (c.x > maxX) maxX = c.x;
                        if (c.y < minZ) minZ = c.y;
                        if (c.y > maxZ) maxZ = c.y;
                    }
                    Vector3 c0 = grid.CellCenterWorld(minX, minZ);
                    Vector3 c1 = grid.CellCenterWorld(minX, maxZ);
                    Vector3 c2 = grid.CellCenterWorld(maxX, maxZ);
                    Vector3 c3 = grid.CellCenterWorld(maxX, minZ);
                    farPoints.Add(new Vector2(c0.x, c0.z));
                    farPoints.Add(new Vector2(c1.x, c1.z));
                    farPoints.Add(new Vector2(c2.x, c2.z));
                    farPoints.Add(new Vector2(c3.x, c3.z));
                    _fadeTargets.Add(c0);
                    _fadeTargets.Add(c1);
                    _fadeTargets.Add(c2);
                    _fadeTargets.Add(c3);
                }
            }

            if (farPoints.Count == 0)
            {
                Vector3 eye = playerVision.EyePosition;
                Vector3 fwd = playerVision.ForwardFlat;
                float range = playerVision.visionRange;
                float half = playerVision.coneHalfAngle;
                Vector3 left = Quaternion.AngleAxis(-half, Vector3.up) * fwd;
                Vector3 right = Quaternion.AngleAxis(half, Vector3.up) * fwd;
                Vector3 pL = eye + left * range;
                Vector3 pR = eye + right * range;
                Vector3 pF = eye + fwd * range;
                farPoints.Add(new Vector2(pL.x, pL.z));
                farPoints.Add(new Vector2(pF.x, pF.z));
                farPoints.Add(new Vector2(pR.x, pR.z));
                _fadeTargets.Add(pL);
                _fadeTargets.Add(pF);
                _fadeTargets.Add(pR);
            }
        }

        // полигон только для гизмо; fade решает raycast
        var sorted = new List<Vector2>(farPoints);
        sorted.Add(playerXZ);
        sorted.Sort((a, b) =>
        {
            float aa = Mathf.Atan2(a.y - camXZ.y, a.x - camXZ.x);
            float bb = Mathf.Atan2(b.y - camXZ.y, b.x - camXZ.x);
            return aa.CompareTo(bb);
        });

        _fadePolygon.Add(camXZ);
        for (int i = 0; i < sorted.Count; i++)
            _fadePolygon.Add(sorted[i]);
    }

    private void ApplyFadeLatch(long h, Vector3 pos, Vector3 camPos, bool strictEnter)
    {
        bool hit = _rayFadeHashes.Contains(h);
        bool latched = _fadeLatched.TryGetValue(h, out var L) && L;
        if (latched)
        {
            if (!hit)
                _fadeLatched[h] = false;
        }
        else if (hit)
        {
            _fadeLatched[h] = true;
        }
    }

    /// <summary>
    /// Рейкасты из камеры в игрока и углы зрения.
    /// Всё, во что попал луч — преграда (fade + красная F).
    /// </summary>
    private void CollectRaycastObstacles(Vector3 camPos)
    {
        _rayFadeHashes.Clear();
        if (_fadeTargets == null || _fadeTargets.Count == 0) return;

        // слой по умолчанию — всё; при желании сузить через layer mask в инспекторе позже
        int mask = ~0;

        for (int t = 0; t < _fadeTargets.Count; t++)
        {
            Vector3 target = _fadeTargets[t] + Vector3.up * 1.2f;
            Vector3 dir = target - camPos;
            float dist = dir.magnitude;
            if (dist < 0.5f) continue;
            dir /= dist;

            int count = Physics.RaycastNonAlloc(camPos, dir, _rayHits, dist, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var hit = _rayHits[i];
                if (hit.collider == null) continue;

                // live-дерево: ищем корень NatureLive
                Transform tr = hit.collider.transform;
                Vector3 treePos = hit.point;

                // если это наш live-prefab — берём позицию GO
                var liveRoot = this.liveRoot;
                if (liveRoot != null && tr.IsChildOf(liveRoot))
                {
                    // подняться к корню live-инстанса
                    Transform root = tr;
                    while (root.parent != null && root.parent != liveRoot)
                        root = root.parent;
                    treePos = root.position;
                }

                long h = PosHash(treePos);
                _rayFadeHashes.Add(h);

                // также ближайшие инстансы к точке удара (без коллайдера)
                MarkNearbyInstances(hit.point, 3.5f);
            }
        }
    }

    private void MarkNearbyInstances(Vector3 hitPoint, float radius)
    {
        if (placement == null || placement.allVariants == null) return;
        float r2 = radius * radius;
        for (int vi = 0; vi < placement.allVariants.Count; vi++)
        {
            var v = placement.allVariants[vi];
            if (v.sectorLists == null) continue;
            foreach (var kv in v.sectorLists)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    Vector3 p = list[i].GetColumn(3);
                    if ((p - hitPoint).sqrMagnitude <= r2)
                        _rayFadeHashes.Add(PosHash(p));
                }
            }
        }
    }

    // =================== Draw ===================

    private void DrawInstanced()
    {
        if (placement.terrainBuilder == null || placement.heightSource == null) return;
        if (placement.allVariants == null) return;

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
            if (v.sectors == null || v.resolvedMesh == null || v.material == null) continue;

            var layer = placement.variantLayer[vi];
            var shadows = v.castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;

            _opaque.Clear();
            _faded.Clear();

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
                            Vector3 pos = batch[i].GetColumn(3);
                            long h = PosHash(pos);

                            // live: не рисуем инстанс (GO уже есть)
                            if (layer.IsTree && _liveKeys.Contains(h)) continue;

                            float fadeT = _fadeCurrent.TryGetValue(h, out var ft) ? ft : 0f;
                            if (fadeT > 0.15f)
                                _faded.Add(batch[i]);
                            else
                                _opaque.Add(batch[i]);
                        }
                    }
                }
            }

            DrawList(v.resolvedMesh, v.material, v.propertyBlock, _opaque, shadows);
            if (_faded.Count > 0)
            {
                // average alpha for batch (good enough; per-instance needs GPU instanced props)
                float avgA = fadeAlpha;
                DrawList(v.resolvedMesh,
                    fadeMaterial != null ? fadeMaterial : v.material,
                    fadeMaterial != null ? null : GetFadeBlock(avgA),
                    _faded, shadows);
            }
        }
    }

    private MaterialPropertyBlock GetFadeBlock(float alpha)
    {
        if (_fadeBlock == null) _fadeBlock = new MaterialPropertyBlock();
        Color c = new Color(1f, 1f, 1f, alpha);
        _fadeBlock.SetColor(ColorId, c);
        _fadeBlock.SetColor(BaseColorId, c);
        return _fadeBlock;
    }

    private void DrawList(Mesh mesh, Material mat, MaterialPropertyBlock block,
        List<Matrix4x4> list, UnityEngine.Rendering.ShadowCastingMode shadows)
    {
        if (list.Count == 0 || mesh == null || mat == null) return;
        const int BS = 1023;
        for (int start = 0; start < list.Count; start += BS)
        {
            int len = Mathf.Min(BS, list.Count - start);
            var slice = list.GetRange(start, len).ToArray();
            Graphics.DrawMeshInstanced(mesh, 0, mat, slice, len, block, shadows, false);
        }
    }

    // =================== Live ===================

    private void UpdateLive()
    {
        if (placement.allVariants == null) return;

        float enterR2 = liveRadius * liveRadius;
        float exitR = liveRadius + liveExitExtra;
        float exitR2 = exitR * exitR;
        Vector3 pp = player.position;

        // despawn only beyond exit radius
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

                        var renderers = go.GetComponentsInChildren<Renderer>();
                        var colors = new Color[renderers.Length];
                        for (int ri = 0; ri < renderers.Length; ri++)
                        {
                            var m = renderers[ri].material;
                            if (m.HasProperty("_Color")) colors[ri] = m.color;
                            else if (m.HasProperty("_BaseColor")) colors[ri] = m.GetColor("_BaseColor");
                            else colors[ri] = Color.white;
                        }

                        _live[h] = new LiveEntry
                        {
                            go = go,
                            variantIndex = vi,
                            sector = skey,
                            pos = pos,
                            renderers = renderers,
                            originalColors = colors
                        };
                        _liveKeys.Add(h);
                    }
                }
            }
        }

        ApplyLiveFade();
    }

    private void ApplyLiveFade()
    {
        foreach (var kv in _live)
        {
            var e = kv.Value;
            if (e.go == null || e.renderers == null) continue;

            float fadeT = _fadeCurrent.TryGetValue(kv.Key, out var ft) ? ft : 0f;
            float a = Mathf.Lerp(1f, fadeAlpha, fadeT);

            for (int i = 0; i < e.renderers.Length; i++)
            {
                var r = e.renderers[i];
                if (r == null) continue;
                var mat = r.material;
                Color baseC = e.originalColors != null && i < e.originalColors.Length
                    ? e.originalColors[i] : Color.white;
                Color c = baseC;
                c.a = baseC.a * a;
                if (mat.HasProperty("_Color")) mat.color = c;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
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
        _fadeCurrent.Clear();
        _fadeLatched.Clear();
    }

    void OnDrawGizmos()
    {
        if (!drawFadeGizmos) return;
        if (!Application.isPlaying) return;

        // лучи ИЗ камеры (3D) к игроку и углам зрения
        if (cam != null && player != null)
        {
            Vector3 camPos = cam.transform.position;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(camPos, 1.2f);

            Gizmos.color = new Color(1f, 0.85f, 0.1f, 1f);
            // камера → игрок
            Gizmos.DrawLine(camPos, player.position + Vector3.up * 1f);
            Gizmos.DrawWireSphere(player.position + Vector3.up * 1f, 0.5f);

            // камера → цели (углы зрения)
            if (_fadeTargets != null)
            {
                for (int i = 0; i < _fadeTargets.Count; i++)
                {
                    Vector3 t = _fadeTargets[i] + Vector3.up * 0.3f;
                    Gizmos.DrawLine(camPos, t);
                    Gizmos.DrawWireSphere(t, 0.35f);
                }
            }
        }

        // красные F — деревья в fade
        if (_fadeGizmoPositions != null && _fadeGizmoPositions.Count > 0)
        {
#if UNITY_EDITOR
            UnityEditor.Handles.color = fadeGizmoColor;
            for (int i = 0; i < _fadeGizmoPositions.Count; i++)
            {
                Vector3 p = _fadeGizmoPositions[i] + Vector3.up * 1.2f;
                UnityEditor.Handles.Label(p, "F");
            }
#endif
            Gizmos.color = fadeGizmoColor;
            for (int i = 0; i < _fadeGizmoPositions.Count; i++)
                Gizmos.DrawWireSphere(_fadeGizmoPositions[i] + Vector3.up * 0.5f, 0.6f);
        }
    }

    void OnDisable()
    {
        ClearLive();
        if (placement != null)
            placement.UnloadAll();
    }
}
