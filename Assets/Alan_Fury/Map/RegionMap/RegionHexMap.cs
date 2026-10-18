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

    private class HexCell
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

        if (Input.GetMouseButtonDown(0) && _cam != null)
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

    void OnGUI()
    {
        // статус всегда виден
        GUI.Box(new Rect(16, 16, 420, 28), _debug);

        if (_selected == null) return;
        var r = new Rect(16, 52, 280, 180);
        GUI.Box(r, "");
        GUILayout.BeginArea(r);
        string name = _selected.kind;
        GUILayout.Label($"Гекс   q = {_selected.q}    r = {_selected.r}");
        GUILayout.Label($"Тип    {name}");
        GUILayout.Label($"Высота {_selected.height:0.0}");
        GUILayout.Label(_focused ? "фокус" : "орбита");
        GUILayout.Label($"Сцена  {mainSceneName}");
        GUILayout.Space(8);
        if (GUILayout.Button("Переместиться", GUILayout.Height(28)))
        {
            _debug = "кнопка нажата";
            Debug.Log("RegionHexMap: клик Переместиться");
            MoveToSelected();
        }
        GUILayout.EndArea();
    }

    void MoveToSelected()
    {
        if (_selected == null)
        {
            Debug.LogWarning("RegionHexMap: гекс не выбран");
            return;
        }
        HexSpawnRequest.Set(_selected.q, _selected.r, _selected.height, seed, hexWorldSize);
        string scene = mainSceneName;
        if (string.IsNullOrEmpty(scene) || !Application.CanStreamedLevelBeLoaded(scene))
        {
            if (Application.CanStreamedLevelBeLoaded("DEMKA"))
                scene = "DEMKA";
            else
            {
                Debug.LogError($"RegionHexMap: не могу загрузить «{mainSceneName}». " +
                               "В инспекторе Main Scene Name поставь DEMKA. Сцена уже есть в Build Settings.");
                return;
            }
        }
        Debug.Log($"RegionHexMap: переход в {scene}, гекс {_selected.q},{_selected.r} ({_selected.kind})");
        SceneManager.LoadScene(scene);
    }

    public void Generate()
    {
        Clear();
        var rnd = new System.Random(seed);
        for (int q = -radius; q <= radius; q++)
        {
            int r1 = Mathf.Max(-radius, -q - radius);
            int r2 = Mathf.Min(radius, -q + radius);
            for (int r = r1; r <= r2; r++)
                CreateHex(q, r, rnd);
        }
        if (_cells.Count > 0)
        {
            var c = _cells[_cells.Count / 2];
            _orbitLookAt = c.go.transform.position;
            _orbitLookAt.y = 0f;
            _lookAt = _orbitLookAt;
        }
        PlaceSitesAndRoad();
    }

    void CreateHex(int q, int r, System.Random rnd)
    {
        float raw = forestBase + ((float)rnd.NextDouble() - 0.5f) * forestVariation;
        float h = Mathf.Max(heightStep, Mathf.Round(raw / heightStep) * heightStep);

        var pos = AxialToWorld(q, r);
        pos.y = h * 0.5f;

        var go = new GameObject($"Hex_{q}_{r}");
        go.transform.SetParent(transform, false);
        go.transform.position = pos;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        var mc = go.AddComponent<MeshCollider>();

        var mesh = HexMesh.Prism(hexSize * 1.005f, h);
        mf.sharedMesh = mesh;
        mc.sharedMesh = mesh;

        var mat = hexMaterial != null ? new Material(hexMaterial) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (mat.shader == null || mat.shader.name.Contains("Error"))
            mat = new Material(Shader.Find("Standard"));

        var col = Color.HSVToRGB(Mathf.Repeat(q * 0.04f + r * 0.06f + 0.35f, 1f), 0.28f, 0.55f);

        mat.color = col;
        mr.sharedMaterial = mat;

        var cell = new HexCell
        {
            q = q,
            r = r,
            height = h,
            go = go,
            mat = mat,
            baseColor = col,
            sideNormals = BuildSideNormals(go.transform)
        };
        _cells.Add(cell);
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
        }
    }

    void PaintSite(HexCell cell, string kind, Color color, PrimitiveType marker)
    {
        if (cell == null) return;
        cell.kind = kind;
        cell.baseColor = color;
        if (cell.mat != null) cell.mat.color = color;
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
        var path = HexLine(_village.q, _village.r, _temple.q, _temple.r);
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < path.Count; i++)
        {
            var cell = FindCell(path[i].x, path[i].y);
            if (cell == null) continue;
            sum += cell.height;
            n++;
        }
        if (n == 0) return;
        float roadH = Mathf.Max(heightStep, Mathf.Round((sum / n) / heightStep) * heightStep);
        for (int i = 0; i < path.Count; i++)
        {
            var cell = FindCell(path[i].x, path[i].y);
            if (cell == null) continue;
            SetHexHeight(cell, roadH);
            cell.road = true;
            cell.kind = "дорога";
            cell.baseColor = new Color(0.45f, 0.36f, 0.22f);
            if (cell.mat != null) cell.mat.color = cell.baseColor;
            _road.Add(cell);
        }
    }

    void SetHexHeight(HexCell cell, float h)
    {
        cell.height = h;
        var p = cell.go.transform.position;
        p.y = h * 0.5f;
        cell.go.transform.position = p;
        var mesh = HexMesh.Prism(hexSize * 1.005f, h);
        var mf = cell.go.GetComponent<MeshFilter>();
        var mc = cell.go.GetComponent<MeshCollider>();
        if (mf != null) mf.sharedMesh = mesh;
        if (mc != null) mc.sharedMesh = mesh;
        cell.sideNormals = BuildSideNormals(cell.go.transform);
    }

    void DrawRoadRibbon()
    {
        if (_road.Count < 2) return;
        float top = _road[0].go.transform.position.y + _road[0].height * 0.5f + 0.05f;
        var line = RegionLayout.RoadLine(hexSize);
        if (line == null || line.Count < 2) return;
        var local = new List<Vector3>(line.Count);
        for (int i = 0; i < line.Count; i++)
        {
            var w = new Vector3(line[i].x, top, line[i].z);
            local.Add(transform.InverseTransformPoint(w));
        }

        var go = new GameObject("Road");
        go.transform.SetParent(transform, false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mf.sharedMesh = RibbonMesh(local, hexSize * 0.28f);
        var mat = hexMaterial != null ? new Material(hexMaterial) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (mat.shader == null || mat.shader.name.Contains("Error"))
            mat = new Material(Shader.Find("Standard"));
        mat.color = new Color(0.45f, 0.36f, 0.22f);
        mr.sharedMaterial = mat;

        for (int i = 0; i < _road.Count; i++)
        {
            var cell = _road[i];
            var wp = cell.go.transform.position;
            int best = 0;
            float bestD = float.MaxValue;
            for (int k = 0; k < line.Count; k++)
            {
                float d = (line[k].x - wp.x) * (line[k].x - wp.x) + (line[k].z - wp.z) * (line[k].z - wp.z);
                if (d < bestD) { bestD = d; best = k; }
            }
            int a = Mathf.Max(0, best - 1);
            int b = Mathf.Min(line.Count - 1, best + 1);
            var tan = line[b] - line[a];
            tan.y = 0f;
            cell.roadDir = tan.sqrMagnitude > 0.0001f ? tan.normalized : Vector3.forward;
        }
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

    HexCell FindCellFromHit(GameObject hitGo)
    {
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
        _selected = cell;
        if (cell.mat != null)
            cell.mat.color = selectedColor;
        _focused = true;
        _focusPoint = cell.go.transform.position;
        _focusPoint.y += 1.2f;
        _dist = Mathf.Lerp(_dist, focusDistance, 0.35f);
    }

    void Deselect()
    {
        if (_selected != null && _selected.mat != null)
            _selected.mat.color = _selected.baseColor;
        _selected = null;
        _focused = false;
        _dist = orbitDistance;
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
            var baseCol = (cell == _selected) ? selectedColor : cell.baseColor;
            cell.mat.color = baseCol * tint;
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
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            if (c.go == null) continue;
            var p = c.go.transform.position;
            p.y += c.height * 0.5f + 0.4f;
            UnityEditor.Handles.Label(p, $"{c.q},{c.r}");
        }
    }
#endif

    void OnDestroy() => Clear();
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
    public static List<Vector3> RoadLine(float hexSize)
    {
        if (Seed == 0 || hexSize < 1f) return _roadLine ?? new List<Vector3>();
        if (_roadLine != null && Mathf.Approximately(_roadLineSize, hexSize))
            return _roadLine;
        _roadLine = BuildRoadLine(hexSize);
        _roadLineSize = hexSize;
        return _roadLine;
    }

    static List<Vector3> BuildRoadLine(float hexSize)
    {
        var path = HexLine(Village.x, Village.y, Temple.x, Temple.y);
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

    public static void Set(int q, int r, float height, int seed, float hexWorldSize)
    {
        Q = q;
        R = r;
        Height = height;
        Seed = seed;
        HexWorldSize = hexWorldSize;
        HasRequest = true;
        // переживает смену сцены даже если домен перезагрузится
        PlayerPrefs.SetInt("HexSpawn_Q", q);
        PlayerPrefs.SetInt("HexSpawn_R", r);
        PlayerPrefs.SetFloat("HexSpawn_H", height);
        PlayerPrefs.SetInt("HexSpawn_Seed", seed);
        PlayerPrefs.SetFloat("HexSpawn_Size", hexWorldSize);
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
        HasRequest = true;
        return true;
    }

    public static void Clear()
    {
        HasRequest = false;
        PlayerPrefs.DeleteKey("HexSpawn_Has");
        PlayerPrefs.Save();
    }
}
