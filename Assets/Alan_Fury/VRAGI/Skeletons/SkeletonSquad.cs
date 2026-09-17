using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Отряд лучников при рыцаре (SkeletonBrain на том же объекте).
/// Маршрут точками, не приклеен к рыцарю. Шеренга на врага. Залп по команде.
/// Смерть рыцаря — Disband, лучники сами и криво.
/// </summary>
public class SkeletonSquad : MonoBehaviour
{
    public enum Form { Column, Rank }

    [Header("Состав")]
    public GameObject archerPrefab;
    public int archerCount = 12;
    public Transform[] existingArchers;

    [Header("Маршрут")]
    public Transform[] orderPoints;
    public int marchGait = 1;
    public float waypointArrive = 2.4f;

    [Header("Строй")]
    public int columnFiles = 2;
    public float fileSpacing = 1.7f;
    public float rankSpacing = 1.85f;
    public float slotArrive = 0.75f;
    [Tooltip("Если шеренга смотрит мимо врага больше этого угла — перестроить.")]
    public float reformAngle = 38f;
    public float escortSide = 2.4f;
    public float escortBack = 0.8f;

    [Header("Залп")]
    public float volleyDraw = 0.75f;
    public float volleyReload = 2.4f;
    public float volleyStagger = 0.04f;
    public float engageRange = 20f;
    public float loseRange = 28f;

    [Header("Спавн без префаба")]
    public float dummyHeight = 1.8f;
    public float dummyRadius = 0.28f;
    public Color dummyColor = new Color(0.82f, 0.8f, 0.72f, 1f);

    public SkeletonBrain Knight { get; private set; }
    public Form CurrentForm { get; private set; } = Form.Column;
    public bool Broken { get; private set; }
    public bool FormationReady { get; private set; }
    public Vector3 Head { get; private set; }
    public Vector3 Forward { get; private set; } = Vector3.forward;
    public Vector3 EscortPoint { get; private set; }
    public Transform Threat { get; private set; }

    readonly List<SkeletonArcherBrain> _archers = new List<SkeletonArcherBrain>(16);
    readonly List<Vector3> _route = new List<Vector3>(16);
    readonly List<Vector3> _slots = new List<Vector3>(16);
    int _routeIndex;
    float _volleyPhaseUntil;
    float _volleyReadyTime;
    enum VolleyPhase { Idle, Drawing, Recover }
    VolleyPhase _volley;
    Pathfinder _pathfinder;
    Transform _hold;

    public int AliveCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _archers.Count; i++)
                if (Live(_archers[i])) n++;
            return n;
        }
    }

    public bool HasArchers => AliveCount > 0 && !Broken;

    public void Bind(SkeletonBrain knight)
    {
        Knight = knight;
        if (orderPoints == null || orderPoints.Length == 0)
        {
            if (knight != null && knight.waypoints != null && knight.waypoints.Length > 0)
                orderPoints = knight.waypoints;
        }
        BuildRouteFromPoints();
        if (_archers.Count == 0)
            SpawnOrCollect();
        Head = transform.position;
        Forward = flatten(transform.forward);
        RefreshSlots();
    }

    void Awake()
    {
        _pathfinder = FindObjectOfType<Pathfinder>();
    }

    void Start()
    {
        if (Knight == null)
        {
            Knight = GetComponent<SkeletonBrain>();
            if (Knight != null) Bind(Knight);
        }
    }

    void Update()
    {
        if (Broken) return;
        Prune();
        if (_archers.Count == 0) return;

        if (Threat != null && !Alive(Threat))
            Threat = null;

        AdvanceRoute();
        RefreshSlots();
        PushSlots();
        FormationReady = AllInSlots();
        TickVolley();
    }

    public void SetThreat(Transform target)
    {
        Threat = Alive(target) ? target : null;
    }

    public void IssueMarch(IList<Vector3> points)
    {
        if (Broken) return;
        _route.Clear();
        if (points != null)
        {
            for (int i = 0; i < points.Count; i++)
                _route.Add(Snap(points[i]));
        }
        _routeIndex = 0;
        CurrentForm = Form.Column;
        CancelVolley();
        if (_route.Count > 0)
        {
            Head = _route[0];
            Forward = flatten(Head - transform.position);
        }
    }

    public void MarchAssignedRoute()
    {
        if (Broken) return;
        if (_route.Count == 0) BuildRouteFromPoints();
        CurrentForm = Form.Column;
        CancelVolley();
    }

    public void FormRankToward(Transform target)
    {
        if (Broken || target == null) return;
        Threat = target;
        Vector3 to = target.position - RankOrigin();
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return;
        Forward = to.normalized;
        CurrentForm = Form.Rank;
        CancelVolley();
    }

    public float RankAngleTo(Transform target)
    {
        if (target == null) return 180f;
        Vector3 to = target.position - RankOrigin();
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return 0f;
        return Vector3.Angle(Forward, to);
    }

    public bool NeedsReform(Transform target)
    {
        if (CurrentForm != Form.Rank) return true;
        return RankAngleTo(target) > reformAngle;
    }

    public bool CanVolley =>
        !Broken
        && CurrentForm == Form.Rank
        && FormationReady
        && _volley == VolleyPhase.Idle
        && Time.time >= _volleyReadyTime;

    public void OrderVolley(Transform target)
    {
        if (!CanVolley || target == null) return;
        Threat = target;
        _volley = VolleyPhase.Drawing;
        _volleyPhaseUntil = Time.time + volleyDraw;
        for (int i = 0; i < _archers.Count; i++)
        {
            if (!Live(_archers[i])) continue;
            _archers[i].OrderDraw();
        }
    }

    public Vector3 GetEscortPoint()
    {
        Vector3 right = Vector3.Cross(Vector3.up, Forward);
        EscortPoint = Head + right * escortSide - Forward * escortBack;
        return Snap(EscortPoint);
    }

    public void Disband()
    {
        Broken = true;
        CancelVolley();
        for (int i = 0; i < _archers.Count; i++)
        {
            if (!Live(_archers[i])) continue;
            _archers[i].BreakRanks();
        }
        _archers.Clear();
        Threat = null;
    }

    public void Forget(SkeletonArcherBrain archer)
    {
        _archers.Remove(archer);
    }

    public static void ApplyEnemyLayer(GameObject go)
    {
        if (go == null) return;
        int layer = LayerMask.NameToLayer("Fury");
        if (layer < 0) layer = LayerMask.NameToLayer("Enemy");
        if (layer < 0) layer = LayerMask.NameToLayer("Werewolf");
        if (layer < 0) return;
        SetLayer(go.transform, layer);
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayer(t.GetChild(i), layer);
    }

    void SpawnOrCollect()
    {
        if (existingArchers != null)
        {
            for (int i = 0; i < existingArchers.Length; i++)
            {
                if (existingArchers[i] == null) continue;
                var a = existingArchers[i].GetComponent<SkeletonArcherBrain>();
                if (a != null) AddArcher(a);
            }
        }

        var found = GetComponentsInChildren<SkeletonArcherBrain>(true);
        for (int i = 0; i < found.Length; i++)
            AddArcher(found[i]);

        int need = Mathf.Max(0, archerCount - _archers.Count);
        if (_hold == null)
        {
            var go = new GameObject(name + "_Archers");
            go.transform.position = transform.position;
            _hold = go.transform;
        }

        for (int i = 0; i < need; i++)
        {
            var a = SpawnOne(i);
            if (a != null) AddArcher(a);
        }
    }

    SkeletonArcherBrain SpawnOne(int index)
    {
        Vector3 pos = transform.position - transform.forward * (1.6f + index * 0.35f);
        pos += transform.right * ((index % 2 == 0) ? -0.8f : 0.8f);
        pos = Snap(pos);

        GameObject go;
        if (archerPrefab != null)
        {
            go = Instantiate(archerPrefab, pos, transform.rotation);
        }
        else
        {
            go = BuildDummy(pos);
        }

        go.name = "SkeletonArcher_" + index;
        go.transform.SetParent(_hold, true);

        var brain = go.GetComponent<SkeletonArcherBrain>();
        if (brain == null) brain = go.AddComponent<SkeletonArcherBrain>();
        if (go.GetComponent<NpcRanged>() == null) go.AddComponent<NpcRanged>();
        ApplyEnemyLayer(go);
        return brain;
    }

    GameObject BuildDummy(Vector3 pos)
    {
        var go = new GameObject("SkeletonArcher");
        go.transform.position = pos;
        go.transform.rotation = transform.rotation;

        var cc = go.AddComponent<CharacterController>();
        cc.height = dummyHeight;
        cc.radius = dummyRadius;
        cc.center = new Vector3(0f, dummyHeight * 0.5f, 0f);

        go.AddComponent<HumanoidLocomotion>();
        go.AddComponent<HumanoidCombat>();
        go.AddComponent<PlayerResources>();
        go.AddComponent<PlayerLoadout>();
        go.AddComponent<NpcPerception>();
        go.AddComponent<NpcRanged>();
        go.AddComponent<SkeletonArcherBrain>();

        var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        vis.name = "Body";
        Object.Destroy(vis.GetComponent<Collider>());
        vis.transform.SetParent(go.transform, false);
        vis.transform.localPosition = new Vector3(0f, dummyHeight * 0.5f, 0f);
        vis.transform.localScale = new Vector3(dummyRadius * 2f, dummyHeight * 0.5f, dummyRadius * 2f);
        var mr = vis.GetComponent<MeshRenderer>();
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh != null)
        {
            var mat = new Material(sh);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", dummyColor);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", dummyColor);
            mr.material = mat;
        }

        var cap = go.GetComponent<CapsuleCollider>();
        if (cap == null) cap = go.AddComponent<CapsuleCollider>();
        cap.height = dummyHeight;
        cap.radius = dummyRadius;
        cap.center = new Vector3(0f, dummyHeight * 0.5f, 0f);
        cap.isTrigger = true;

        ApplyEnemyLayer(go);
        return go;
    }

    void AddArcher(SkeletonArcherBrain a)
    {
        if (a == null || _archers.Contains(a)) return;
        if (a.transform == transform) return;
        _archers.Add(a);
        a.BindSquad(this, _archers.Count - 1);
        a.arriveDistance = slotArrive;
        a.marchGait = marchGait;
    }

    void Prune()
    {
        for (int i = _archers.Count - 1; i >= 0; i--)
        {
            if (!Live(_archers[i]))
                _archers.RemoveAt(i);
        }
        for (int i = 0; i < _archers.Count; i++)
            _archers[i].SlotIndex = i;
    }

    void BuildRouteFromPoints()
    {
        _route.Clear();
        if (orderPoints != null)
        {
            for (int i = 0; i < orderPoints.Length; i++)
            {
                if (orderPoints[i] == null) continue;
                _route.Add(Snap(orderPoints[i].position));
            }
        }
        _routeIndex = 0;
        if (_route.Count == 0)
            _route.Add(Snap(transform.position + transform.forward * 6f));
    }

    void AdvanceRoute()
    {
        if (CurrentForm != Form.Column || _route.Count == 0) return;
        Vector3 dest = _route[Mathf.Clamp(_routeIndex, 0, _route.Count - 1)];
        if (Flat(Head, dest) <= waypointArrive || FrontNear(dest))
        {
            _routeIndex++;
            if (_routeIndex >= _route.Count)
                _routeIndex = 0;
        }
        Head = _route[Mathf.Clamp(_routeIndex, 0, _route.Count - 1)];
        Vector3 prev = _routeIndex > 0 ? _route[_routeIndex - 1] : transform.position;
        Vector3 tang = Head - prev;
        tang.y = 0f;
        if (tang.sqrMagnitude > 0.05f)
            Forward = tang.normalized;
    }

    void RefreshSlots()
    {
        if (CurrentForm == Form.Rank)
            Head = RankOrigin();

        int files = CurrentForm == Form.Rank
            ? Mathf.Max(1, AliveCount)
            : Mathf.Max(1, columnFiles);
        int n = _archers.Count;
        while (_slots.Count < n) _slots.Add(Vector3.zero);
        if (_slots.Count > n) _slots.RemoveRange(n, _slots.Count - n);

        Vector3 right = Vector3.Cross(Vector3.up, Forward);
        for (int i = 0; i < n; i++)
        {
            int file = i % files;
            int row = i / files;
            float x = (file - (files - 1) * 0.5f) * fileSpacing;
            float z = -row * rankSpacing;
            Vector3 p = Head + right * x + Forward * z;
            _slots[i] = Snap(p);
        }

        EscortPoint = Head + right * escortSide - Forward * escortBack;
    }

    Vector3 RankOrigin()
    {
        if (CurrentForm == Form.Rank && Threat != null)
        {
            Vector3 c = Centroid();
            Vector3 to = Threat.position - c;
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f)
            {
                float stand = Mathf.Clamp(to.magnitude - 12f, 4f, 16f);
                return Snap(Threat.position - to.normalized * stand);
            }
            return c;
        }
        if (_route.Count > 0)
            return _route[Mathf.Clamp(_routeIndex, 0, _route.Count - 1)];
        return transform.position + Forward * 2f;
    }

    Vector3 Centroid()
    {
        Vector3 s = Vector3.zero;
        int n = 0;
        for (int i = 0; i < _archers.Count; i++)
        {
            if (!Live(_archers[i])) continue;
            s += _archers[i].transform.position;
            n++;
        }
        if (n == 0) return transform.position;
        s /= n;
        s.y = transform.position.y;
        return s;
    }

    void PushSlots()
    {
        for (int i = 0; i < _archers.Count; i++)
        {
            if (!Live(_archers[i])) continue;
            Vector3 face = Forward;
            if (CurrentForm == Form.Rank && Threat != null)
            {
                Vector3 to = Threat.position - _archers[i].transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) face = to.normalized;
            }
            _archers[i].SetSlot(_slots[i], face, marchGait);
        }
    }

    bool AllInSlots()
    {
        int need = 0, ok = 0;
        for (int i = 0; i < _archers.Count; i++)
        {
            if (!Live(_archers[i])) continue;
            need++;
            if (_archers[i].InSlot) ok++;
        }
        return need > 0 && ok >= need;
    }

    bool FrontNear(Vector3 dest)
    {
        int files = Mathf.Max(1, columnFiles);
        int check = Mathf.Min(files, _archers.Count);
        int near = 0, live = 0;
        for (int i = 0; i < check; i++)
        {
            if (!Live(_archers[i])) continue;
            live++;
            if (Flat(_archers[i].transform.position, dest) <= waypointArrive)
                near++;
        }
        return live > 0 && near >= live;
    }

    void TickVolley()
    {
        if (_volley == VolleyPhase.Drawing)
        {
            if (Time.time >= _volleyPhaseUntil)
            {
                for (int i = 0; i < _archers.Count; i++)
                {
                    if (!Live(_archers[i])) continue;
                    _archers[i].OrderFire(Threat);
                }
                _volley = VolleyPhase.Recover;
                _volleyPhaseUntil = Time.time + volleyReload;
                _volleyReadyTime = Time.time + volleyReload;
            }
            return;
        }

        if (_volley == VolleyPhase.Recover && Time.time >= _volleyPhaseUntil)
            _volley = VolleyPhase.Idle;
    }

    void CancelVolley()
    {
        if (_volley == VolleyPhase.Drawing)
        {
            for (int i = 0; i < _archers.Count; i++)
            {
                if (!Live(_archers[i])) continue;
                _archers[i].OrderCancelVolley();
            }
        }
        _volley = VolleyPhase.Idle;
    }

    Vector3 Snap(Vector3 p)
    {
        if (_pathfinder != null && _pathfinder.IsReady)
            return _pathfinder.NearestWalkableWorld(p, out _);
        return p;
    }

    static bool Live(SkeletonArcherBrain a)
    {
        return a != null && a.isActiveAndEnabled
            && (a.resources == null || a.resources.IsAlive);
    }

    static bool Alive(Transform t)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return false;
        var dmg = t.GetComponentInParent<IDamageable>();
        return dmg == null || dmg.IsAlive;
    }

    static Vector3 flatten(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.01f ? v.normalized : Vector3.forward;
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.9f, 0.75f, 0.2f, 0.8f);
        if (orderPoints != null)
        {
            Vector3 prev = transform.position;
            for (int i = 0; i < orderPoints.Length; i++)
            {
                if (orderPoints[i] == null) continue;
                Gizmos.DrawLine(prev, orderPoints[i].position);
                Gizmos.DrawWireSphere(orderPoints[i].position, 0.35f);
                prev = orderPoints[i].position;
            }
        }
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.7f);
        Gizmos.DrawWireSphere(Head, 0.4f);
        Gizmos.DrawLine(Head, Head + Forward * 2f);
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.6f);
        for (int i = 0; i < _slots.Count; i++)
            Gizmos.DrawWireCube(_slots[i] + Vector3.up * 0.05f, new Vector3(0.5f, 0.1f, 0.5f));
    }
}
