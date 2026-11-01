using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Регион-карта: большой гекс из объёмных гексов без просветов.
/// Орбитальная камера (мягкое вращение, Q/E, без лимита отдаления).
/// Плавный и слабый фокус на выбранном гексе. Лесные высоты с шагом.
/// Один гекс — «Воющая пуща» (маркер для зоны волков).
/// Кнопка «Переместиться» → основная сцена, спавн в выбранном гексе.
/// Типы — один файл HexTypes. На гекс не пишется.
/// Сначала линия дороги, потом префаб типа (HexContent).
/// </summary>
public class RegionHexMap : MonoBehaviour
{
    [Header("Сетка")]
    public int radius = 9;
    public float hexSize = 2f;
    public float heightStep = 0.25f;
    public float forestBase = 2.2f;
    public float forestVariation = 0.35f;
    public int seed = 42;

    [Header("Вид")]
    public Material hexMaterial;
    public Color selectedColor = new Color(1f, 0.55f, 0.15f, 1f);
    public Color nearFaceTint = new Color(1.25f, 1.2f, 1.05f, 1f);
    public Color howlingColor = new Color(0.35f, 0.25f, 0.45f, 1f);

    [Header("Камера")]
    public float orbitDistance = 32f;
    public float orbitHeight = 16f;
    public float focusDistance = 14f;
    public float rotateSpeed = 0.08f;
    public float zoomSpeed = 5f;
    public float minDist = 5f;
    public float focusLerp = 1.4f;
    public float keyRotateSpeed = 28f;

    [Header("Переход")]
    public string mainSceneName = "DEMKA";
    public float hexWorldSize = 200f;
    [Tooltip("Строить сетку при запуске сцены RegionMap.")]
    public bool generateOnStart = true;
    [Tooltip("При старте брать новый случайный сид.")]
    public bool randomMapOnStart = false;

    [Header("Типы гексов")]
    [Tooltip("Один файл со всеми типами. Alan Fury → Create Hex Types.")]
    public HexTypes hexTypesAsset;

    private readonly List<HexCell> _cells = new List<HexCell>(256);
    private HexCell _selected;
    private HexCell _howling;
    private Camera _cam;
    private Light _sun;

    private float _yaw = 20f;
    private float _pitch = 35f;
    private float _dist;
    private Vector3 _lookAt;
    private Vector3 _orbitLookAt;
    private bool _focused;
    private Vector3 _focusPoint;

    public class HexCell
    {
        public int q;
        public int r;
        public float height;
        public GameObject go;
        public Material mat;
        public Color baseColor;
        public Vector3[] sideNormals;
        public string kind = "лес";
        public bool road;
        public Vector3 roadDir = Vector3.forward;
        public List<GameObject> subs = new List<GameObject>();
        public Material centerMat;
        public int centerSmallQ;
        public int centerSmallR;
    }

    HexCell _village;
    HexCell _temple;
    HexCell _cemetery;
    HexCell _swamp;
    readonly List<HexCell> _road = new List<HexCell>();

    void Start()
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        bool isRegionScene = scene.IndexOf("Region", System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (!isRegionScene)
        {
            enabled = false;
            return;
        }
        if (!generateOnStart) return;

        EnsureCamera();
        EnsureLight();
        _dist = orbitDistance;
        _lookAt = Vector3.zero;
        _orbitLookAt = Vector3.zero;

        if (randomMapOnStart)
            seed = UnityEngine.Random.Range(1, 999999);

        Generate();
        UpdateCameraImmediate();
    }

    [ContextMenu("Построить сетку")]
    public void BuildNowMenu() => BuildNow(false);

    [ContextMenu("Случайная карта")]
    public void BuildRandomMenu() => BuildNow(true);

    /// <summary>Явный вызов: построить (или перестроить) сетку.</summary>
    public void BuildNow(bool newRandomSeed = false)
    {
        if (newRandomSeed)
            seed = UnityEngine.Random.Range(1, 999999);
        EnsureCamera();
        EnsureLight();
        Generate();
        UpdateCameraImmediate();
    }

    void Update()
    {
        HandleInput();
        UpdateCamera();
        UpdateNearFaces();
    }

    void HandleInput()
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (_selected != null) MoveToSelected();
        }

        if (Input.GetKeyDown(KeyCode.Escape))
            Deselect();

        if (Input.GetKey(KeyCode.Q)) _yaw -= keyRotateSpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.E)) _yaw += keyRotateSpeed * Time.deltaTime;

        if (Input.GetMouseButton(1) || Input.GetMouseButton(2))
        {
            _yaw += Input.GetAxis("Mouse X") * rotateSpeed * 80f;
            _pitch -= Input.GetAxis("Mouse Y") * rotateSpeed * 80f;
            _pitch = Mathf.Clamp(_pitch, 6f, 78f);
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.001f)
        {
            _dist -= scroll * zoomSpeed * (_focused ? 10f : 14f);
            if (_dist < minDist) _dist = minDist;
        }

        if (_selected != null && Input.GetMouseButtonUp(0) && MouseOver(ButtonRect()))
        {
            _debug = "кнопка нажата";
            Debug.Log("RegionHexMap: клик Переместиться");
            MoveToSelected();
            return;
        }

        if (Input.GetMouseButtonDown(0) && _cam != null && !MouseOverInfo())
        {
            var ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 400f))
            {
                var cell = FindCellFromHit(hit.collider.gameObject);
                if (cell != null)
                {
                    if (_selected == cell)
                        Deselect();
                    else
                        Select(cell);
                    return;
                }
            }
            if (_focused) Deselect();
        }
    }

    string _debug = "ожидание";

    static Rect StatusRect() => new Rect(16, 16, 420, 28);
    static Rect PanelRect() => new Rect(16, 52, 300, 220);
    static Rect ButtonRect() => new Rect(28, 228, 276, 32);

    bool MouseOver(Rect r)
    {
        Vector2 m = Input.mousePosition;
        m.y = Screen.height - m.y;
        return r.Contains(m);
    }

    bool MouseOverInfo()
    {
        return MouseOver(StatusRect()) || MouseOver(PanelRect()) || MouseOver(ButtonRect());
    }

    void OnGUI()
    {
        GUI.Box(StatusRect(), _debug);

        if (_selected == null) return;
        var r = PanelRect();
        GUI.Box(r, "");

        float y = r.y + 8;
        string name = _selected.kind;
        GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), $"Гекс   q = {_selected.q}    r = {_selected.r}"); y += 20;
        GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), $"Тип    {name}"); y += 20;
        GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), $"Высота {_selected.height:0.0}"); y += 20;
        GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), _focused ? "фокус" : "орбита"); y += 20;
        GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), $"Сцена  {mainSceneName}"); y += 20;
        string point = SpecificPointName(_selected);
        GUI.Label(new Rect(r.x + 10, y, r.width - 20, 36), string.IsNullOrEmpty(point)
            ? "Спавн  центр района"
            : $"Спавн  {point}\n(или центр, если точки нет)");

        var btn = ButtonRect();
        bool over = MouseOver(btn);
        var prev = GUI.color;
        if (over) GUI.color = new Color(1f, 0.85f, 0.4f);
        if (GUI.Button(btn, "Переместиться"))
        {
            _debug = "кнопка нажата";
            Debug.Log("RegionHexMap: клик Переместиться (GUI)");
            MoveToSelected();
        }
        GUI.color = prev;
    }

    static string SpecificPointName(HexCell cell)
    {
        if (cell == null) return null;
        string k = cell.kind;
        if (k == "лес" || k == "дорога" || k == "роща" || k == "чаща" || k == "поляна") return null;
        return k;
    }

    bool _moving;

    void MoveToSelected()
    {
        if (_moving || _selected == null)
        {
            if (_selected == null) Debug.LogWarning("RegionHexMap: гекс не выбран");
            return;
        }
        _moving = true;
        string point = SpecificPointName(_selected);
        HexSpawnRequest.Set(_selected.q * 3, _selected.r * 3, _selected.height, seed, hexWorldSize, point);
        string scene = mainSceneName;
        if (string.IsNullOrEmpty(scene) || !Application.CanStreamedLevelBeLoaded(scene))
        {
            if (Application.CanStreamedLevelBeLoaded("DEMKA"))
                scene = "DEMKA";
            else
            {
                _moving = false;
                Debug.LogError($"RegionHexMap: не могу загрузить «{mainSceneName}». " +
                               "В инспекторе Main Scene Name поставь DEMKA. Сцена уже есть в Build Settings.");
                return;
            }
        }
        Debug.Log($"RegionHexMap: переход в {scene}, гекс {_selected.q},{_selected.r} ({_selected.kind}), точка={(point ?? "центр")}");
        SceneManager.LoadScene(scene);
    }

    public void Generate()
    {
        Clear();
        var rnd = new System.Random(seed);

        CreateSmallFieldAndGroup(rnd);

        if (_cells.Count > 0)
        {
            var c = _cells[_cells.Count / 2];
            _orbitLookAt = c.go.transform.position;
            _orbitLookAt.y = 0f;
            _lookAt = _orbitLookAt;
        }
        PlaceSitesAndRoad();
    }

    void CreateSmallFieldAndGroup(System.Random rnd)
    {
        float small = hexSize / 3f;
        int span = radius * 2 + 2;
        var fieldObj = new GameObject("HexField");
        fieldObj.transform.SetParent(transform, false);

        var groups = new Dictionary<Vector2Int, HexCell>();

        for (int sq = -span; sq <= span; sq++)
        {
            int r1 = Mathf.Max(-span, -sq - span);
            int r2 = Mathf.Min(span, -sq + span);
            for (int sr = r1; sr <= r2; sr++)
            {
                // Родитель по решётке плотности 1/7 (N=7, i=2, j=1)
                float fQ = (3f * sq + sr) / 7f;
                float fR = (-sq + 2f * sr) / 7f;
                int Q = Mathf.RoundToInt(fQ);
                int R = Mathf.RoundToInt(fR);

                var key = new Vector2Int(Q, R);
                if (!groups.TryGetValue(key, out var cell))
                {
                    cell = CreateHex(Q, R, rnd, small);
                    groups[key] = cell;
                    _cells.Add(cell);
                }

                float x = small * (Mathf.Sqrt(3f) * sq + Mathf.Sqrt(3f) * 0.5f * sr);
                float z = small * (1.5f * sr);
                float yJitter = ((sq * 13 + sr * 7) & 3) * 0.002f;

                var child = new GameObject($"sub_{sq}_{sr}");
                child.transform.SetParent(fieldObj.transform, false);
                child.transform.position = new Vector3(x, cell.height * 0.5f + yJitter, z);

                var mf = child.AddComponent<MeshFilter>();
                var mr = child.AddComponent<MeshRenderer>();
                var mc = child.AddComponent<MeshCollider>();
                var mesh = HexMesh.Prism(small * 1.0f, cell.height);
                mf.sharedMesh = mesh;
                mc.sharedMesh = mesh;
                mc.enabled = true;

                // Центр группы — тот, чьи координаты совпадают с центром клетки
                bool isCenter = (sq == cell.centerSmallQ && sr == cell.centerSmallR);
                mr.sharedMaterial = cell.mat;

                var tag = child.AddComponent<HexSubTag>();
                tag.parentCell = cell;
                tag.smallQ = sq;
                tag.smallR = sr;
                tag.isCenter = isCenter;
                cell.subs.Add(child);
            }
        }

        SmoothEdgeHeights(groups, small);
    }

    void SmoothEdgeHeights(Dictionary<Vector2Int, HexCell> groups, float small)
    {
        const float maxDelta = 0.35f;
        foreach (var cell in _cells)
        {
            if (cell.subs == null) continue;
            for (int i = 0; i < cell.subs.Count; i++)
            {
                var ch = cell.subs[i];
                if (ch == null) continue;
                var tag = ch.GetComponent<HexSubTag>();
                if (tag == null || tag.isCenter) continue;

                int dq = tag.smallQ - cell.centerSmallQ;
                int dr = tag.smallR - cell.centerSmallR;
                if (dq == 0 && dr == 0) continue;

                // Ближайшая соседняя клетка в этом направлении
                var neighKey = new Vector2Int(cell.q + dq, cell.r + dr);
                if (!groups.TryGetValue(neighKey, out var neigh) || neigh == null) continue;

                float ownH = cell.height;
                float neighH = neigh.height;
                float blended = Mathf.Lerp(ownH, neighH, 0.45f);
                float limited = Mathf.Clamp(blended, ownH - maxDelta, ownH + maxDelta);

                var pos = ch.transform.position;
                pos.y = limited * 0.5f;
                ch.transform.position = pos;

                var mf = ch.GetComponent<MeshFilter>();
                var mc = ch.GetComponent<MeshCollider>();
                if (mf != null)
                {
                    var mesh = HexMesh.Prism(small * 1.0f, limited);
                    mf.sharedMesh = mesh;
                    if (mc != null) mc.sharedMesh = mesh;
                }
            }
        }
    }

    HexCell CreateHex(int q, int r, System.Random rnd, float small)
    {
        float raw = forestBase + ((float)rnd.NextDouble() - 0.5f) * forestVariation;
        float h = Mathf.Max(heightStep, Mathf.Round(raw / heightStep) * heightStep);

        // Позиция клетки — в центре её семёрки
        int cq = 2 * q - r;
        int cr = q + 3 * r;
        var pos = AxialToWorldScaled(cq, cr, small);
        pos.y = h * 0.5f;

        var go = new GameObject($"Hex_{q}_{r}");
        go.transform.SetParent(transform, false);
        go.transform.position = pos;

        var mat = hexMaterial != null ? new Material(hexMaterial) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (mat.shader == null || mat.shader.name.Contains("Error"))
            mat = new Material(Shader.Find("Standard"));

        var col = Color.HSVToRGB(Mathf.Repeat(q * 0.04f + r * 0.06f + 0.35f, 1f), 0.28f, 0.55f);
        mat.color = col;

        var mc = go.AddComponent<MeshCollider>();
        var mesh = HexMesh.Prism(small * 2.5f, h);
        mc.sharedMesh = mesh;

        var cell = new HexCell
        {
            q = q,
            r = r,
            height = h,
            go = go,
            mat = mat,
            baseColor = col,
            sideNormals = BuildSideNormals(go.transform),
            centerSmallQ = cq,
            centerSmallR = cr
        };
        return cell;
    }

    static Vector3 AxialToWorldScaled(int q, int r, float size)
    {
        float x = size * (Mathf.Sqrt(3f) * q + Mathf.Sqrt(3f) * 0.5f * r);
        float z = size * (1.5f * r);
        return new Vector3(x, 0f, z);
    }

    void BlendBorders()
    {
        float ideal = hexSize / 3f;
        float smallSize = ideal * 1.06f;
        float maxShift = ideal * 0.06f;
        int[] dq = { 0, 1, 1, 0, -1, -1, 0 };
        int[] dr = { 0, 0, -1, -1, 0, 1, 1 };

        for (int ci = 0; ci < _cells.Count; ci++)
        {
            var cell = _cells[ci];
            if (cell.go == null) continue;
            for (int dir = 1; dir < 7; dir++)
            {
                int nq = cell.q + dq[dir];
                int nr = cell.r + dr[dir];
                var neigh = FindCell(nq, nr);
                if (neigh == null) continue;
                float dh = neigh.height - cell.height;
                if (Mathf.Abs(dh) < 0.02f) continue;

                string name = $"sub_{dq[dir]}_{dr[dir]}";
                var child = cell.go.transform.Find(name);
                if (child == null) continue;

                var dirOff = AxialToWorld(dq[dir], dr[dir]);
                dirOff *= (ideal / hexSize);
                if (dirOff.sqrMagnitude < 0.0001f) continue;
                var dirN = dirOff.normalized;

                float shift = Mathf.Clamp(Mathf.Abs(dh) * 0.15f, 0f, maxShift);
                var lp = child.localPosition;
                lp.x += dirN.x * shift;
                lp.z += dirN.z * shift;

                float blendH = Mathf.Lerp(cell.height, neigh.height, 0.5f);
                var mesh = HexMesh.Prism(smallSize, blendH);
                var mf = child.GetComponent<MeshFilter>();
                var mc = child.GetComponent<MeshCollider>();
                if (mf != null) mf.sharedMesh = mesh;
                if (mc != null) mc.sharedMesh = mesh;

                lp.y = (blendH - cell.height) * 0.5f;
                child.localPosition = lp;
            }
        }
    }

    void PlaceSitesAndRoad()
    {
        RegionLayout.Build(seed);

        _village = FindCell(RegionLayout.Village.x, RegionLayout.Village.y);
        _temple = FindCell(RegionLayout.Temple.x, RegionLayout.Temple.y);
        _cemetery = FindCell(RegionLayout.Cemetery.x, RegionLayout.Cemetery.y);
        _swamp = FindCell(RegionLayout.Swamp.x, RegionLayout.Swamp.y);

        LevelRoadHexes();
        PaintSite(_village, "населенный пункт", new Color(0.75f, 0.62f, 0.35f), PrimitiveType.Cube);
        PaintSite(_temple, "старый храм", new Color(0.85f, 0.82f, 0.7f), PrimitiveType.Cylinder);
        PaintSite(_cemetery, "погост", new Color(0.55f, 0.55f, 0.6f), PrimitiveType.Cube);
        PaintSite(_swamp, "болото", new Color(0.25f, 0.38f, 0.28f), PrimitiveType.Sphere);

        PlaceHowlingNearRoad();
        PaintZones();
        DrawRoadRibbon();
        AddSiteLabels();
        ApplyHexTypes();
    }

    void PaintZones()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            if (c.kind != "лес") continue;
            var z = RegionLayout.ZoneForHex(c.q, c.r);
            Color col;
            switch (z)
            {
                case NaturePlacement.Zone.Grove: col = new Color(0.55f, 0.72f, 0.38f); c.kind = "роща"; break;
                case NaturePlacement.Zone.Thicket: col = new Color(0.18f, 0.32f, 0.16f); c.kind = "чаща"; break;
                case NaturePlacement.Zone.Clearing: col = new Color(0.62f, 0.64f, 0.38f); c.kind = "поляна"; break;
                default: col = new Color(0.28f, 0.48f, 0.26f); break;
            }
            c.baseColor = col;
            if (c.mat != null) c.mat.color = col;
            if (c.centerMat != null) c.centerMat.color = Color.Lerp(col, Color.white, 0.35f);
        }
    }

    void PaintSite(HexCell cell, string kind, Color color, PrimitiveType marker)
    {
        if (cell == null) return;
        cell.kind = kind;
        cell.baseColor = color;
        if (cell.mat != null) cell.mat.color = color;
        if (cell.centerMat != null) cell.centerMat.color = Color.Lerp(color, Color.white, 0.35f);
        var m = GameObject.CreatePrimitive(marker);
        m.name = kind;
        m.transform.SetParent(cell.go.transform, false);
        m.transform.localPosition = new Vector3(0f, cell.height * 0.5f + 0.55f, 0f);
        m.transform.localScale = marker == PrimitiveType.Cube
            ? new Vector3(0.45f, 0.7f, 0.45f)
            : new Vector3(0.4f, 0.5f, 0.4f);
        var mr = m.GetComponent<MeshRenderer>();
        if (mr != null) mr.material.color = color * 1.15f;
        var col = m.GetComponent<Collider>();
        if (col != null) Destroy(col);
    }

    void LevelRoadHexes()
    {
        _road.Clear();
        if (_village == null || _temple == null) return;

        int vq = _village.centerSmallQ;
        int vr = _village.centerSmallR;
        int tq = _temple.centerSmallQ;
        int tr = _temple.centerSmallR;
        var path = HexLine(vq, vr, tq, tr);

        float sum = 0f;
        int n = 0;
        for (int i = 0; i < path.Count; i++)
        {
            var cell = FindCellBySmall(path[i].x, path[i].y);
            if (cell == null) continue;
            sum += cell.height;
            n++;
        }
        if (n == 0) return;
        float roadH = Mathf.Max(heightStep, Mathf.Round((sum / n) / heightStep) * heightStep);

        for (int i = 0; i < path.Count; i++)
        {
            var cell = FindCellBySmall(path[i].x, path[i].y);
            if (cell == null) continue;
            if (!_road.Contains(cell))
            {
                SetHexHeight(cell, roadH);
                cell.road = true;
                cell.kind = "дорога";
                cell.baseColor = new Color(0.45f, 0.36f, 0.22f);
                if (cell.mat != null) cell.mat.color = cell.baseColor;
                _road.Add(cell);
            }
        }
    }

    HexCell FindCellBySmall(int sq, int sr)
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            if (c.subs == null) continue;
            for (int k = 0; k < c.subs.Count; k++)
            {
                var tag = c.subs[k] != null ? c.subs[k].GetComponent<HexSubTag>() : null;
                if (tag != null && tag.smallQ == sq && tag.smallR == sr) return c;
            }
        }
        return null;
    }

    void SetHexHeight(HexCell cell, float h)
    {
        cell.height = h;
        var p = cell.go.transform.position;
        p.y = h * 0.5f;
        cell.go.transform.position = p;
        float small = hexSize / 3f;
        foreach (Transform child in cell.go.transform)
        {
            var mf = child.GetComponent<MeshFilter>();
            var mc = child.GetComponent<MeshCollider>();
            if (mf == null) continue;
            var mesh = HexMesh.Prism(small * 1.008f, h);
            mf.sharedMesh = mesh;
            if (mc != null) mc.sharedMesh = mesh;
            var lp = child.localPosition;
            lp.y = 0f;
            child.localPosition = lp;
        }
        cell.sideNormals = BuildSideNormals(cell.go.transform);
    }

    void DrawRoadRibbon()
    {
        if (_village == null || _temple == null) return;
        int vq = _village.centerSmallQ;
        int vr = _village.centerSmallR;
        int tq = _temple.centerSmallQ;
        int tr = _temple.centerSmallR;
        var path = HexLine(vq, vr, tq, tr);
        if (path.Count < 2) return;

        float small = hexSize / 3f;
        var points = new List<Vector3>();
        for (int i = 0; i < path.Count; i++)
        {
            int sq = path[i].x;
            int sr = path[i].y;
            var cell = FindCellBySmall(sq, sr);
            if (cell == null) continue;
            float x = small * (Mathf.Sqrt(3f) * sq + Mathf.Sqrt(3f) * 0.5f * sr);
            float z = small * (1.5f * sr);
            float y = cell.height * 0.5f + 0.06f;
            points.Add(transform.InverseTransformPoint(new Vector3(x, y, z)));
        }
        if (points.Count < 2) return;

        var go = new GameObject("Road");
        go.transform.SetParent(transform, false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mf.sharedMesh = RibbonMesh(points, small * 0.85f);
        var mat = hexMaterial != null ? new Material(hexMaterial) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (mat.shader == null || mat.shader.name.Contains("Error"))
            mat = new Material(Shader.Find("Standard"));
        mat.color = new Color(0.45f, 0.36f, 0.22f);
        mr.sharedMaterial = mat;
    }

    static Mesh RibbonMesh(List<Vector3> line, float width)
    {
        int n = line.Count;
        if (n < 2) return new Mesh();
        var mesh = new Mesh();
        mesh.name = "RoadRibbon";
        var verts = new Vector3[n * 2];
        var tris = new int[(n - 1) * 6];
        float half = width * 0.5f;
        Vector3 prevSide = Vector3.right;
        for (int i = 0; i < n; i++)
        {
            var prev = line[Mathf.Max(0, i - 1)];
            var next = line[Mathf.Min(n - 1, i + 1)];
            var tan = next - prev;
            tan.y = 0f;
            if (tan.sqrMagnitude < 0.0001f) tan = Vector3.forward;
            tan.Normalize();
            var side = new Vector3(-tan.z, 0f, tan.x);
            if (Vector3.Dot(side, prevSide) < 0f) side = -side;
            prevSide = side;
            var p = line[i];
            verts[i * 2] = p - side * half;
            verts[i * 2 + 1] = p + side * half;
        }
        int ti = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
            tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
            tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
        }
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void AddSiteLabels()
    {
        AddLabel(_village, "населенный пункт");
        AddLabel(_temple, "храм");
        AddLabel(_howling, "Воющая пуща");
    }

    void AddLabel(HexCell cell, string text)
    {
        if (cell == null || cell.go == null) return;
        var go = new GameObject("Label_" + text);
        go.transform.SetParent(cell.go.transform, false);
        go.transform.localPosition = new Vector3(0f, cell.height * 0.5f + 1.6f, 0f);
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.fontSize = 48;
        tm.characterSize = 0.12f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(1f, 0.95f, 0.75f);
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (tm.font == null) tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    void PlaceHowlingNearRoad()
    {
        var pick = FindCell(RegionLayout.Howling.x, RegionLayout.Howling.y);
        if (pick == null) pick = FindCell(2, -1);
        if (pick == null) return;

        pick.kind = "Воющая пуща";
        pick.baseColor = howlingColor;
        if (pick.mat != null) pick.mat.color = howlingColor;
        if (pick.centerMat != null) pick.centerMat.color = Color.Lerp(howlingColor, Color.white, 0.35f);
        _howling = pick;
        CreateHowlingMarker(pick.go.transform, pick.height);
        HowlingQ = pick.q;
        HowlingR = pick.r;
    }

    public static int HowlingQ = 2;
    public static int HowlingR = -1;

    HexCell FindCell(int q, int r)
    {
        for (int i = 0; i < _cells.Count; i++)
            if (_cells[i].q == q && _cells[i].r == r) return _cells[i];
        return null;
    }

    void AddCenterLabel(Transform parent, int q, int r, float height)
    {
        var go = new GameObject("label");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, height * 0.5f + 0.15f, 0f);
        var tm = go.AddComponent<TextMesh>();
        tm.text = $"({q},{r})";
        tm.fontSize = 24;
        tm.characterSize = 0.08f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.15f, 0.15f, 0.2f, 1f);
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (tm.font == null) tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    HexCell FindCellFromHit(GameObject hitGo)
    {
        if (hitGo == null) return null;
        var tag = hitGo.GetComponent<HexSubTag>();
        if (tag != null && tag.parentCell != null) return tag.parentCell;

        var go = hitGo;
        while (go != null)
        {
            for (int i = 0; i < _cells.Count; i++)
                if (_cells[i].go == go) return _cells[i];
            if (go.transform.parent == null) break;
            go = go.transform.parent.gameObject;
        }
        return null;
    }

    void ApplyHexTypes()
    {
        if (hexTypesAsset == null) return;
        for (int i = 0; i < _cells.Count; i++)
            ApplyType(_cells[i]);
    }

    void ApplyType(HexCell cell)
    {
        if (cell == null || cell.go == null) return;
        var entry = hexTypesAsset.Find(cell.kind);
        if (entry == null || entry.hexPrefab == null) return;

        var inst = Instantiate(entry.hexPrefab, cell.go.transform);
        inst.name = cell.kind;
        inst.transform.localPosition = Vector3.zero;
        inst.transform.localRotation = Quaternion.identity;
        inst.transform.localScale = Vector3.one;
        if (entry.hideBase)
        {
            var mr = cell.go.GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false;
        }
        var content = inst.GetComponent<HexContent>();
        if (content != null)
            content.Generate(SeedFor(cell.q, cell.r), hexSize, cell.height * 0.5f, cell.road, cell.roadDir);
    }

    static List<Vector2Int> HexLine(int q0, int r0, int q1, int r1)
    {
        var list = new List<Vector2Int>();
        int n = HexDist(q0, r0, q1, r1);
        if (n == 0) { list.Add(new Vector2Int(q0, r0)); return list; }
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float q = Mathf.Lerp(q0, q1, t);
            float r = Mathf.Lerp(r0, r1, t);
            var c = HexRound(q, r);
            if (list.Count == 0 || list[list.Count - 1] != c)
                list.Add(c);
        }
        return list;
    }

    static int HexDist(int q0, int r0, int q1, int r1)
    {
        int dq = q1 - q0;
        int dr = r1 - r0;
        return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2;
    }

    static Vector2Int HexRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);
        float qd = Mathf.Abs(rq - q);
        float rd = Mathf.Abs(rr - r);
        float sd = Mathf.Abs(rs - s);
        if (qd > rd && qd > sd) rq = -rr - rs;
        else if (rd > sd) rr = -rq - rs;
        return new Vector2Int(rq, rr);
    }

    static Vector2Int HexNeighbor(int q, int r, int dir)
    {
        int[] dq = { 1, 1, 0, -1, -1, 0 };
        int[] dr = { 0, -1, -1, 0, 1, 1 };
        return new Vector2Int(q + dq[dir], r + dr[dir]);
    }

    void CreateHowlingMarker(Transform parent, float hexHeight)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = "Воющая пуща";
        marker.transform.SetParent(parent, false);
        marker.transform.localPosition = new Vector3(0f, hexHeight * 0.5f + 0.6f, 0f);
        marker.transform.localScale = new Vector3(0.35f, 0.8f, 0.35f);
        var m = marker.GetComponent<MeshRenderer>().material;
        m.color = new Color(0.7f, 0.55f, 0.9f);
        Destroy(marker.GetComponent<Collider>());
    }

    void Select(HexCell cell)
    {
        if (_selected != null && _selected.mat != null)
            _selected.mat.color = _selected.baseColor;
        if (_selected != null && _selected.centerMat != null)
            _selected.centerMat.color = Color.Lerp(_selected.baseColor, Color.white, 0.35f);

        _selected = cell;
        if (cell.mat != null)
            cell.mat.color = selectedColor;
        if (cell.centerMat != null)
            cell.centerMat.color = selectedColor;
        _focused = true;
        _focusPoint = cell.go.transform.position;
        _focusPoint.y += 1.2f;
        _dist = Mathf.Lerp(_dist, focusDistance, 0.35f);
        RefreshSubHexes(cell);
    }

    void Deselect()
    {
        if (_selected != null && _selected.mat != null)
            _selected.mat.color = _selected.baseColor;
        if (_selected != null && _selected.centerMat != null)
            _selected.centerMat.color = Color.Lerp(_selected.baseColor, Color.white, 0.35f);
        var prev = _selected;
        _selected = null;
        _focused = false;
        _dist = orbitDistance;
        if (prev != null) RefreshSubHexes(prev);
    }

    void RefreshSubHexes(HexCell cell)
    {
        if (cell == null) return;
        bool sel = (cell == _selected);
        Color ring = sel ? selectedColor : cell.baseColor;
        Color center = sel ? selectedColor : Color.Lerp(cell.baseColor, Color.white, 0.35f);
        if (cell.mat != null) cell.mat.color = ring;
        if (cell.centerMat != null) cell.centerMat.color = center;

        for (int i = 0; i < cell.subs.Count; i++)
        {
            var ch = cell.subs[i];
            if (ch == null) continue;
            var mr = ch.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            bool isCenter = ch.name.Contains($"_{cell.q * 3}_{cell.r * 3}");
            mr.sharedMaterial = isCenter && cell.centerMat != null ? cell.centerMat : cell.mat;
        }
    }

    void UpdateCamera()
    {
        Vector3 targetLook = _focused ? _focusPoint : _orbitLookAt;
        _lookAt = Vector3.Lerp(_lookAt, targetLook, Time.deltaTime * focusLerp);

        float targetDist = _focused ? Mathf.Lerp(_dist, focusDistance, 0.25f) : _dist;
        _dist = Mathf.Lerp(_dist, targetDist, Time.deltaTime * focusLerp * 0.45f);

        UpdateCameraImmediate();
    }

    void UpdateCameraImmediate()
    {
        if (_cam == null) return;
        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        var offset = rot * new Vector3(0f, 0f, -_dist);
        _cam.transform.position = _lookAt + offset + Vector3.up * (_focused ? 1.5f : orbitHeight * 0.15f);
        _cam.transform.LookAt(_lookAt);
    }

    void UpdateNearFaces()
    {
        if (_cam == null) return;
        var camPos = _cam.transform.position;
        foreach (var cell in _cells)
        {
            if (cell.mat == null || cell.sideNormals == null) continue;
            float best = -1f;
            for (int i = 0; i < 6; i++)
            {
                float d = Vector3.Dot(cell.sideNormals[i], (camPos - cell.go.transform.position).normalized);
                if (d > best) best = d;
            }
            var tint = Color.Lerp(Color.white, nearFaceTint, Mathf.Clamp01(best * 1.4f));
            bool sel = (cell == _selected);
            var ringBase = sel ? selectedColor : cell.baseColor;
            var centerBase = sel ? selectedColor : Color.Lerp(cell.baseColor, Color.white, 0.35f);
            cell.mat.color = ringBase * tint;
            if (cell.centerMat != null) cell.centerMat.color = centerBase * tint;
        }
    }

    void EnsureCamera()
    {
        _cam = Camera.main;
        if (_cam == null)
        {
            var go = new GameObject("RegionCamera");
            _cam = go.AddComponent<Camera>();
            _cam.tag = "MainCamera";
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.12f, 0.14f, 0.16f);
        }
    }

    void EnsureLight()
    {
        _sun = FindObjectOfType<Light>();
        if (_sun == null)
        {
            var go = new GameObject("RegionSun");
            _sun = go.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.intensity = 1.1f;
            _sun.color = new Color(1f, 0.96f, 0.9f);
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
    }

    void Clear()
    {
        var old = transform.Find("Road");
        if (old != null)
        {
            if (Application.isPlaying) Destroy(old.gameObject);
            else DestroyImmediate(old.gameObject);
        }
        var oldField = transform.Find("HexField");
        if (oldField != null)
        {
            if (Application.isPlaying) Destroy(oldField.gameObject);
            else DestroyImmediate(oldField.gameObject);
        }
        foreach (var c in _cells)
            if (c.go != null) Destroy(c.go);
        _cells.Clear();
        _road.Clear();
        _selected = null;
        _howling = null;
        _village = null;
        _temple = null;
        _cemetery = null;
        _swamp = null;
        _focused = false;
    }

    Vector3 AxialToWorld(int q, int r)
    {
        float x = hexSize * (Mathf.Sqrt(3f) * q + Mathf.Sqrt(3f) * 0.5f * r);
        float z = hexSize * (1.5f * r);
        return new Vector3(x, 0f, z);
    }

    int SeedFor(int q, int r)
    {
        unchecked { return seed ^ (q * 73856093) ^ (r * 19349663); }
    }

    static Vector3[] BuildSideNormals(Transform t)
    {
        var n = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.PI * 2f / 6f + Mathf.PI / 6f;
            n[i] = t.TransformDirection(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
        }
        return n;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (_cells == null || _cells.Count == 0) return;
        float smallR = hexSize / 3f;
        var wire = new Color(1f, 1f, 1f, 0.55f);
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            if (c.go == null) continue;
            var p = c.go.transform.position;
            p.y += c.height * 0.5f + 0.4f;
            UnityEditor.Handles.Label(p, $"{c.q},{c.r}");

            // обводка всех маленьких гексов, из которых собран регион
            var t = c.go.transform;
            for (int k = 0; k < t.childCount; k++)
            {
                var ch = t.GetChild(k);
                if (ch == null || !ch.name.StartsWith("sub_")) continue;
                DrawHexWire(ch.position, smallR, wire);
            }
        }
    }

    void DrawHexWire(Vector3 center, float radius, Color color)
    {
        UnityEditor.Handles.color = color;
        Vector3 prev = center;
        for (int i = 0; i <= 6; i++)
        {
            float a = i * Mathf.PI * 2f / 6f + Mathf.PI / 6f;
            var pt = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            if (i > 0) UnityEditor.Handles.DrawLine(prev, pt);
            prev = pt;
        }
    }
#endif

    void OnDestroy() => Clear();
}

public class HexSubTag : MonoBehaviour
{
    public RegionHexMap.HexCell parentCell;
    public int smallQ;
    public int smallR;
    public bool isCenter;
}

/// <summary>
/// Раскладка региона от сида. Одна и та же в RegionMap и в основной сцене.
/// </summary>
public static class RegionLayout
{
    public static int Seed;
    public static Vector2Int Village;
    public static Vector2Int Temple;
    public static Vector2Int Cemetery;
    public static Vector2Int Swamp;
    public static Vector2Int Howling;
    public static readonly List<Vector2Int> Road = new List<Vector2Int>();

    static List<Vector3> _roadLine;
    static float _roadLineSize = -1f;

    public static void Build(int seed)
    {
        Seed = seed;
        var rnd = new System.Random(seed);
        int shift = rnd.Next(-2, 3);
        Village = new Vector2Int(-4 + shift, 2);
        Temple = new Vector2Int(5 + shift, -3);
        Cemetery = new Vector2Int(-1 + shift, -2);
        Swamp = new Vector2Int(3 + shift, 3);

        Road.Clear();
        var path = HexLine(Village.x, Village.y, Temple.x, Temple.y);
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if (p == Village || p == Temple || p == Cemetery || p == Swamp) continue;
            Road.Add(p);
        }

        Howling = new Vector2Int(2, -1);
        for (int i = 0; i < Road.Count; i++)
        {
            bool found = false;
            for (int d = 0; d < 6; d++)
            {
                var n = HexNeighbor(Road[i].x, Road[i].y, d);
                if (n == Village || n == Temple || n == Cemetery || n == Swamp) continue;
                if (Road.Contains(n)) continue;
                Howling = n;
                found = true;
                break;
            }
            if (found) break;
        }
        RegionHexMap.HowlingQ = Howling.x;
        RegionHexMap.HowlingR = Howling.y;

        _roadLine = null;
        _roadLineSize = -1f;
    }

    /// <summary>Извилистая лента деревня→храм в мировых (или визуальных) координатах. Один и тот же путь на карте региона и на местности.</summary>
    public static List<Vector3> RoadLine(float hexSize, bool scaled = false)
    {
        if (Seed == 0 || hexSize < 1f) return _roadLine ?? new List<Vector3>();
        float keySize = scaled ? -hexSize : hexSize;
        if (_roadLine != null && Mathf.Approximately(_roadLineSize, keySize))
            return _roadLine;
        _roadLine = BuildRoadLine(hexSize, scaled);
        _roadLineSize = keySize;
        return _roadLine;
    }

    static List<Vector3> BuildRoadLine(float hexSize, bool scaled = false)
    {
        int scale = scaled ? SuperScale : 1;
        var path = HexLine(Village.x * scale, Village.y * scale, Temple.x * scale, Temple.y * scale);
        var knots = new List<Vector3>(path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            float x = hexSize * (Mathf.Sqrt(3f) * p.x + Mathf.Sqrt(3f) * 0.5f * p.y);
            float z = hexSize * (1.5f * p.y);
            knots.Add(new Vector3(x, 0f, z));
        }
        if (knots.Count < 2) return knots;
        return Winding(knots, Seed, hexSize);
    }

    static List<Vector3> Winding(List<Vector3> knots, int roadSeed, float hexSize)
    {
        var rnd = new System.Random(roadSeed ^ 0x51A7);
        var line = new List<Vector3>(knots.Count * 6);
        int n = knots.Count;
        const int steps = 6;
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = knots[Mathf.Max(0, i - 1)];
            var p1 = knots[i];
            var p2 = knots[i + 1];
            var p3 = knots[Mathf.Min(n - 1, i + 2)];
            for (int s = 0; s < steps; s++)
            {
                float t = s / (float)steps;
                var p = Catmull(p0, p1, p2, p3, t);
                var tan = Catmull(p0, p1, p2, p3, Mathf.Min(1f, t + 0.05f)) - p;
                tan.y = 0f;
                if (tan.sqrMagnitude < 0.0001f) tan = p2 - p1;
                tan.Normalize();
                var side = new Vector3(-tan.z, 0f, tan.x);
                float wave = Mathf.Sin((i + t) * 1.6f) * hexSize * 0.22f;
                wave += ((float)rnd.NextDouble() - 0.5f) * hexSize * 0.1f;
                line.Add(p + side * wave);
            }
        }
        line.Add(knots[n - 1]);
        return line;
    }

    static Vector3 Catmull(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            2f * p1 +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    public static bool IsRoadHex(int q, int r)
    {
        var p = new Vector2Int(q, r);
        return Road.Contains(p);
    }

    public static string KindAt(int q, int r)
    {
        var p = new Vector2Int(q, r);
        if (p == Village) return "населенный пункт";
        if (p == Temple) return "старый храм";
        if (p == Cemetery) return "погост";
        if (p == Swamp) return "болото";
        if (p == Howling) return "Воющая пуща";
        if (Road.Contains(p)) return "дорога";
        return "лес";
    }

    public const int SuperScale = 3;

    public static Vector2Int SuperOf(int q, int r)
    {
        int q0 = q / SuperScale;
        int r0 = r / SuperScale;
        int bestQ = q0, bestR = r0;
        int bestD = HexDist(q, r, q0 * SuperScale, r0 * SuperScale);
        for (int dq = -1; dq <= 1; dq++)
            for (int dr = -1; dr <= 1; dr++)
            {
                if (dq == 0 && dr == 0) continue;
                int Q = q0 + dq;
                int R = r0 + dr;
                int d = HexDist(q, r, Q * SuperScale, R * SuperScale);
                if (d < bestD)
                {
                    bestD = d;
                    bestQ = Q;
                    bestR = R;
                }
            }
        return new Vector2Int(bestQ, bestR);
    }

    public static NaturePlacement.Zone ZoneForTerrain(int q, int r)
    {
        var s = SuperOf(q, r);
        return ZoneForHex(s.x, s.y);
    }

    public static float DensityScaleTerrain(int q, int r)
    {
        var s = SuperOf(q, r);
        return DensityScale(s.x, s.y);
    }

    public static string KindAtTerrain(int q, int r)
    {
        var s = SuperOf(q, r);
        return KindAt(s.x, s.y);
    }

    public static bool IsHowlingArea(int q, int r)
    {
        return HexDist(q, r, Howling.x * SuperScale, Howling.y * SuperScale) <= 1;
    }

    /// <summary>Зона растительности всего гекса. Один гекс = одна территория.</summary>
    public static NaturePlacement.Zone ZoneForHex(int q, int r)
    {
        var p = new Vector2Int(q, r);
        if (p == Village || HexDist(q, r, Village.x, Village.y) <= 1)
            return NaturePlacement.Zone.Grove;
        if (p == Temple || HexDist(q, r, Temple.x, Temple.y) <= 1)
            return NaturePlacement.Zone.Grove;
        if (p == Cemetery) return NaturePlacement.Zone.Clearing;
        if (p == Swamp || p == Howling) return NaturePlacement.Zone.Thicket;
        if (HexDist(q, r, Howling.x, Howling.y) == 1) return NaturePlacement.Zone.Thicket;
        return NaturePlacement.Zone.Forest;
    }

    /// <summary>1 = норма, меньше = реже. Деревня — минимум, храм — норма.</summary>
    public static float DensityScale(int q, int r)
    {
        if (HexDist(q, r, Village.x, Village.y) <= 1) return 0.55f;
        if (HexDist(q, r, Temple.x, Temple.y) <= 1) return 1f;
        return 1f;
    }

    static List<Vector2Int> HexLine(int q0, int r0, int q1, int r1)
    {
        var list = new List<Vector2Int>();
        int n = HexDist(q0, r0, q1, r1);
        if (n == 0) { list.Add(new Vector2Int(q0, r0)); return list; }
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float q = Mathf.Lerp(q0, q1, t);
            float r = Mathf.Lerp(r0, r1, t);
            var c = HexRound(q, r);
            if (list.Count == 0 || list[list.Count - 1] != c)
                list.Add(c);
        }
        return list;
    }

    static int HexDist(int q0, int r0, int q1, int r1)
    {
        int dq = q1 - q0, dr = r1 - r0;
        return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2;
    }

    static Vector2Int HexRound(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q), rr = Mathf.RoundToInt(r), rs = Mathf.RoundToInt(s);
        float qd = Mathf.Abs(rq - q), rd = Mathf.Abs(rr - r), sd = Mathf.Abs(rs - s);
        if (qd > rd && qd > sd) rq = -rr - rs;
        else if (rd > sd) rr = -rq - rs;
        return new Vector2Int(rq, rr);
    }

    static Vector2Int HexNeighbor(int q, int r, int dir)
    {
        int[] dq = { 1, 1, 0, -1, -1, 0 };
        int[] dr = { 0, -1, -1, 0, 1, 1 };
        return new Vector2Int(q + dq[dir], r + dr[dir]);
    }
}
public static class HexSpawnRequest
{
    public static bool HasRequest { get; private set; }
    public static int Q { get; private set; }
    public static int R { get; private set; }
    public static float Height { get; private set; }
    public static int Seed { get; private set; }
    public static float HexWorldSize { get; private set; }
    public static string PointName { get; private set; }

    public static void Set(int q, int r, float height, int seed, float hexWorldSize, string pointName = null)
    {
        Q = q;
        R = r;
        Height = height;
        Seed = seed;
        HexWorldSize = hexWorldSize;
        PointName = pointName;
        HasRequest = true;
        // переживает смену сцены даже если домен перезагрузится
        PlayerPrefs.SetInt("HexSpawn_Q", q);
        PlayerPrefs.SetInt("HexSpawn_R", r);
        PlayerPrefs.SetFloat("HexSpawn_H", height);
        PlayerPrefs.SetInt("HexSpawn_Seed", seed);
        PlayerPrefs.SetFloat("HexSpawn_Size", hexWorldSize);
        PlayerPrefs.SetString("HexSpawn_Point", pointName ?? "");
        PlayerPrefs.SetInt("HexSpawn_Has", 1);
        PlayerPrefs.Save();
    }

    public static bool TryRestore()
    {
        if (HasRequest) return true;
        if (PlayerPrefs.GetInt("HexSpawn_Has", 0) != 1) return false;
        Q = PlayerPrefs.GetInt("HexSpawn_Q");
        R = PlayerPrefs.GetInt("HexSpawn_R");
        Height = PlayerPrefs.GetFloat("HexSpawn_H");
        Seed = PlayerPrefs.GetInt("HexSpawn_Seed");
        HexWorldSize = PlayerPrefs.GetFloat("HexSpawn_Size", 200f);
        PointName = PlayerPrefs.GetString("HexSpawn_Point", "");
        if (string.IsNullOrEmpty(PointName)) PointName = null;
        HasRequest = true;
        return true;
    }

    /// <summary>Центр выбранного гекса в мире. Та же формула, что LocationStream.HexToWorld.</summary>
    public static Vector3 WorldCenter()
    {
        float size = HexWorldSize > 1f ? HexWorldSize : 100f;
        float x = size * (Mathf.Sqrt(3f) * Q + Mathf.Sqrt(3f) * 0.5f * R);
        float z = size * (1.5f * R);
        return new Vector3(x, Height, z);
    }

    public static void Clear()
    {
        HasRequest = false;
        PointName = null;
        PlayerPrefs.DeleteKey("HexSpawn_Has");
        PlayerPrefs.DeleteKey("HexSpawn_Point");
        PlayerPrefs.Save();
    }
}
