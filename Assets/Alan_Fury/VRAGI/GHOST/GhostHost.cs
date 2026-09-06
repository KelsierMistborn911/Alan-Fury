using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Главное ползущее существо. Голова едет по Pathfinder, корпус пока цельный.
/// Извилистость — медленный доворот + синус в сторону, не цепочка костей.
/// Призраки роятся на слотах вокруг и по очереди летят проверить кусок маршрута впереди.
/// </summary>
[RequireComponent(typeof(GhostStats))]
[RequireComponent(typeof(NpcPerception))]
public class GhostHost : MonoBehaviour
{
    [Header("Мир")]
    public Pathfinder pathfinder;
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;
    public MapBoundary boundary;

    [Header("Полз")]
    [Tooltip("Скорость головы (м/с).")]
    public float crawlSpeed = 1.35f;
    public float acceleration = 3f;
    public float deceleration = 4f;
    [Tooltip("Макс. доворот (град/с). Мало — дуга, как у змеи.")]
    public float maxTurnDegPerSec = 38f;
    [Tooltip("Прибытие в точку пути (м).")]
    public float arriveThreshold = 1.8f;
    public float bodyRadius = 0.7f;

    [Header("Извилина (без костей)")]
    [Tooltip("Амплитуда увода в сторону от пути (м). 0 — строго по клеткам.")]
    public float weaveAmplitude = 1.6f;
    [Tooltip("Длина одной полуволны (м).")]
    public float weaveLength = 9f;

    [Header("Блуждание по карте")]
    [Tooltip("Как далеко искать следующую точку (м).")]
    public float wanderRadius = 40f;
    [Tooltip("Минимальный шаг блуждания (м).")]
    public float wanderMin = 14f;
    public Transform[] waypoints;

    [Header("Рой")]
    [Tooltip("Префаб летуна (ghost-set с GhostFlyer). Пусто — не спавнит, берёт детей / scouts[].")]
    public GameObject escortPrefab;
    [Tooltip("Сколько мелких сопровождения создать.")]
    public int escortCount = 5;
    [Tooltip("Масштаб сопровождения относительно префаба.")]
    public float escortScale = 0.45f;
    public GhostFlyer[] scouts;
    public float orbitRadius = 3.4f;
    public float orbitHeight = 2.4f;
    [Tooltip("Скорость вращения роя (град/с).")]
    public float orbitDegPerSec = 22f;
    [Tooltip("Как часто выпускать разведчика (сек).")]
    public float scoutInterval = 5.5f;
    [Tooltip("На сколько метров вперёд по маршруту слать разведчика.")]
    public float scoutLookahead = 16f;
    [Tooltip("Сколько разведчик висит на точке перед возвратом (сек).")]
    public float scoutHold = 1.2f;

    [Header("Аниматор модели")]
    public Animator animator;
    [Tooltip("Имя float-скорости, если в контроллере есть. Пусто — не пишем.")]
    public string speedParam = "";

    public Vector3 Heading => _heading;
    public bool HasPath => _path.Count > 0;

    private readonly List<Vector3> _path = new List<Vector3>();
    private int _pathIndex;
    private Vector3 _heading = Vector3.forward;
    private Vector3 _vel;
    private float _distTravelled;
    private float _tileSize = 4f;
    private Vector3 _mapOrigin;
    private float _scoutIn;
    private float _orbitAngle;
    private Transform[] _slots;
    private float[] _scoutHoldLeft;
    private bool[] _scouting;
    private int _wpIndex;
    private GhostStats _stats;
    private NpcPerception _vision;

    void Awake()
    {
        _stats = GetComponent<GhostStats>();
        _vision = GetComponent<NpcPerception>();
        if (_vision == null) _vision = gameObject.AddComponent<NpcPerception>();
        _vision.ApplyHost();
        if (animator == null) animator = GetComponent<Animator>();
        if (animator != null) animator.applyRootMotion = false;
        _heading = transform.forward;
        _heading.y = 0f;
        if (_heading.sqrMagnitude < 0.01f) _heading = Vector3.forward;
        _heading.Normalize();
        _scoutIn = scoutInterval * 0.4f;
    }

    void Start()
    {
        ResolveWorld();
        SnapToGround();
        SpawnEscorts();
        BindScouts();
        PickNextGoal();
    }

    void Update()
    {
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned) return;

        if (_stats != null && !_stats.IsAlive)
        {
            ParkScouts();
            return;
        }

        float dt = Time.deltaTime;
        ResolveWorld();
        TickCrawl(dt);
        TickOrbit(dt);
        TickScouts(dt);
        WriteAnimator();
    }

    void TickCrawl(float dt)
    {
        if (_path.Count == 0)
        {
            PickNextGoal();
            return;
        }

        AdvancePathIndex();
        Vector3 target = WeavePoint(PathPoint(_pathIndex));
        Vector3 pos = transform.position;
        Vector3 to = target - pos;
        to.y = 0f;

        if (to.sqrMagnitude <= arriveThreshold * arriveThreshold && _pathIndex >= _path.Count - 1)
        {
            PickNextGoal();
            return;
        }

        Vector3 want = to.sqrMagnitude > 0.0001f ? to.normalized : _heading;
        float maxRad = maxTurnDegPerSec * Mathf.Deg2Rad * dt;
        _heading = Vector3.RotateTowards(_heading, want, maxRad, 0f).normalized;

        float cap = crawlSpeed;
        Vector3 wish = _heading * cap;
        _vel = Vector3.MoveTowards(_vel, wish, (wish.sqrMagnitude > _vel.sqrMagnitude ? acceleration : deceleration) * dt);

        Vector3 delta = _vel * dt;
        if (boundary != null)
            delta = boundary.Constrain(pos, delta);

        Vector3 next = pos + delta;
        if (pathfinder != null && pathfinder.IsReady && !pathfinder.IsWalkableWorld(next))
        {
            bool found;
            Vector3 safe = pathfinder.NearestWalkableWorld(next, out found);
            if (found) { next.x = safe.x; next.z = safe.z; }
            else { next.x = pos.x; next.z = pos.z; _vel = Vector3.zero; }
        }

        next.y = GroundY(next);
        float step = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(next.x, 0f, next.z));
        _distTravelled += step;
        transform.position = next;

        if (_heading.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(_heading, Vector3.up);
    }

    void TickOrbit(float dt)
    {
        if (_slots == null) return;
        _orbitAngle += orbitDegPerSec * dt;
        int n = _slots.Length;
        for (int i = 0; i < n; i++)
        {
            if (_slots[i] == null) continue;
            float a = (_orbitAngle + 360f * i / Mathf.Max(n, 1)) * Mathf.Deg2Rad;
            _slots[i].localPosition = new Vector3(Mathf.Sin(a) * orbitRadius, orbitHeight, Mathf.Cos(a) * orbitRadius);
        }
    }

    void TickScouts(float dt)
    {
        if (scouts == null || scouts.Length == 0) return;

        for (int i = 0; i < scouts.Length; i++)
        {
            GhostFlyer f = scouts[i];
            if (f == null) continue;
            if (_scouting[i])
            {
                if (f.Arrived)
                {
                    _scoutHoldLeft[i] -= dt;
                    if (_scoutHoldLeft[i] <= 0f)
                    {
                        _scouting[i] = false;
                        if (_slots[i] != null) f.Follow(_slots[i]);
                    }
                }
            }
            else if (f.follow == null && _slots[i] != null)
            {
                f.Follow(_slots[i]);
            }
        }

        _scoutIn -= dt;
        if (_scoutIn > 0f) return;
        _scoutIn = scoutInterval;

        int free = -1;
        for (int i = 0; i < scouts.Length; i++)
        {
            if (scouts[i] == null || _scouting[i]) continue;
            free = i;
            break;
        }
        if (free < 0) return;

        Vector3 ahead = LookaheadPoint(scoutLookahead);
        scouts[free].MoveTo(ahead);
        _scouting[free] = true;
        _scoutHoldLeft[free] = scoutHold;
    }

    public Vector3 LookaheadPoint(float meters)
    {
        if (_path.Count == 0) return transform.position + _heading * meters;

        float left = meters;
        Vector3 cur = transform.position;
        for (int i = _pathIndex; i < _path.Count; i++)
        {
            Vector3 p = PathPoint(i);
            float d = Vector3.Distance(Flat(cur), Flat(p));
            if (d >= left)
            {
                Vector3 dir = Flat(p) - Flat(cur);
                if (dir.sqrMagnitude < 0.0001f) return p;
                return cur + dir.normalized * left;
            }
            left -= d;
            cur = p;
        }
        return PathPoint(_path.Count - 1);
    }

    void PickNextGoal()
    {
        _path.Clear();
        _pathIndex = 0;
        if (pathfinder == null || !pathfinder.IsReady) return;

        Vector3 goal;
        if (waypoints != null && waypoints.Length > 0)
        {
            int guard = 0;
            Transform t = null;
            while (guard++ < waypoints.Length)
            {
                t = waypoints[_wpIndex % waypoints.Length];
                _wpIndex++;
                if (t != null) break;
            }
            goal = t != null ? t.position : transform.position + _heading * wanderMin;
        }
        else
        {
            Vector2 rnd = Random.insideUnitCircle.normalized;
            float r = Random.Range(wanderMin, wanderRadius);
            goal = transform.position + new Vector3(rnd.x, 0f, rnd.y) * r;
        }

        bool found;
        goal = pathfinder.NearestWalkableWorld(goal, out found);
        if (!found) return;
        pathfinder.TryFindPath(transform.position, goal, _path);
    }

    void AdvancePathIndex()
    {
        while (_pathIndex < _path.Count - 1)
        {
            Vector3 to = PathPoint(_pathIndex) - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude <= arriveThreshold * arriveThreshold) _pathIndex++;
            else break;
        }
    }

    Vector3 WeavePoint(Vector3 pathPoint)
    {
        if (weaveAmplitude <= 0.01f || weaveLength <= 0.1f) return pathPoint;
        Vector3 right = Vector3.Cross(Vector3.up, _heading);
        if (right.sqrMagnitude < 0.01f) return pathPoint;
        right.Normalize();
        float wave = Mathf.Sin((_distTravelled / weaveLength) * Mathf.PI * 2f);
        pathPoint += right * (wave * weaveAmplitude);
        return pathPoint;
    }

    Vector3 PathPoint(int i)
    {
        i = Mathf.Clamp(i, 0, _path.Count - 1);
        Vector3 p = _path[i];
        p.y = GroundY(p);
        return p;
    }

    static Vector3 Flat(Vector3 p) { p.y = 0f; return p; }

    void SpawnEscorts()
    {
        if (escortPrefab == null) return;
        int n = Mathf.Max(0, escortCount);
        if (n == 0) return;

        var folder = new GameObject("GhostEscorts").transform;
        folder.SetParent(transform, false);

        scouts = new GhostFlyer[n];
        for (int i = 0; i < n; i++)
        {
            GameObject go = Instantiate(escortPrefab, transform.position, Quaternion.identity, folder);
            go.name = "GhostEscort_" + i;
            go.layer = gameObject.layer;
            go.transform.localScale = Vector3.one * escortScale;

            GhostFlyer f = go.GetComponent<GhostFlyer>();
            if (f == null) f = go.AddComponent<GhostFlyer>();
            if (go.GetComponent<GhostStats>() == null)
                go.AddComponent<GhostStats>();
            NpcPerception vis = go.GetComponent<NpcPerception>();
            if (vis == null) vis = go.AddComponent<NpcPerception>();
            vis.ApplyScout();

            f.role = GhostFlyer.Role.Scout;
            f.pathfinder = pathfinder;
            f.heightSource = heightSource;
            f.chunkedBuilder = chunkedBuilder;
            f.boundary = boundary;
            f.hoverInPlace = false;
            f.inCombat = false;
            scouts[i] = f;
        }
    }

    void BindScouts()
    {
        if (scouts == null || scouts.Length == 0)
            scouts = GetComponentsInChildren<GhostFlyer>(true);

        int n = scouts != null ? scouts.Length : 0;
        _slots = new Transform[n];
        _scoutHoldLeft = new float[n];
        _scouting = new bool[n];

        for (int i = 0; i < n; i++)
        {
            var slot = new GameObject("GhostOrbit_" + i).transform;
            slot.SetParent(transform, false);
            _slots[i] = slot;
            if (scouts[i] != null)
            {
                scouts[i].hoverInPlace = false;
                scouts[i].Follow(slot);
                NpcPerception vis = scouts[i].GetComponent<NpcPerception>();
                if (vis == null) vis = scouts[i].gameObject.AddComponent<NpcPerception>();
                if (vis.profile != NpcPerception.Profile.GhostHost) vis.ApplyScout();
            }
        }
        TickOrbit(0f);
    }

    void ParkScouts()
    {
        if (scouts == null) return;
        for (int i = 0; i < scouts.Length; i++)
            if (scouts[i] != null) scouts[i].Stop();
    }

    void WriteAnimator()
    {
        if (animator == null || string.IsNullOrEmpty(speedParam)) return;
        animator.SetFloat(speedParam, _vel.magnitude);
    }

    void SnapToGround()
    {
        Vector3 p = transform.position;
        p.y = GroundY(p);
        transform.position = p;
    }

    float GroundY(Vector3 world)
    {
        if (heightSource != null && heightSource.isGenerated)
            return heightSource.GetHeightAtWorldPos(world, _tileSize, _mapOrigin);
        if (Physics.Raycast(world + Vector3.up * 40f, Vector3.down, out RaycastHit hit, 80f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return world.y;
    }

    void ResolveWorld()
    {
        if (pathfinder == null) pathfinder = FindObjectOfType<Pathfinder>();
        if (heightSource == null && pathfinder != null) heightSource = pathfinder.heightSource;
        if (heightSource == null) heightSource = FindObjectOfType<HeightMapGenerator>();
        if (chunkedBuilder == null && heightSource != null) chunkedBuilder = heightSource.chunkedBuilder;
        if (chunkedBuilder == null) chunkedBuilder = FindObjectOfType<ChunkedTerrainBuilder>();
        if (boundary == null) boundary = FindObjectOfType<MapBoundary>();
        _tileSize = chunkedBuilder != null ? chunkedBuilder.tileSize : 4f;
        if (heightSource != null)
        {
            _mapOrigin = new Vector3(
                -heightSource.width * _tileSize * 0.5f,
                0f,
                -heightSource.depth * _tileSize * 0.5f);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.85f, 0.55f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, bodyRadius);
        Gizmos.DrawRay(transform.position + Vector3.up * 0.4f, _heading * 3f);
        Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * orbitHeight, orbitRadius);
        if (_path == null) return;
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.7f);
        for (int i = 1; i < _path.Count; i++)
            Gizmos.DrawLine(_path[i - 1] + Vector3.up * 0.3f, _path[i] + Vector3.up * 0.3f);
    }
}
