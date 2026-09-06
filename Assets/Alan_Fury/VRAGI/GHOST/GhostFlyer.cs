using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Летун на префабе ghost-set. Только полёт: парит над землёй, летит к точке / за целью.
/// Вне боя обходит деревья (Pathfinder + сфера). В бою проходит сквозь объекты.
/// Мозг носителя / разведка / фаза-2 сюда не входят.
/// </summary>
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(Rigidbody))]
public class GhostFlyer : MonoBehaviour
{
    public enum Role { Scout, Spirit }

    [Header("Роль (масштаб позже)")]
    public Role role = Role.Scout;

    [Header("Источники мира")]
    public Pathfinder pathfinder;
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;
    public MapBoundary boundary;

    [Header("Полёт")]
    [Tooltip("Высота корпуса над землёй (м).")]
    public float hoverHeight = 2.2f;
    [Tooltip("Крейсер вне боя (м/с).")]
    public float cruiseSpeed = 4.2f;
    [Tooltip("Скорость в бою (м/с).")]
    public float combatSpeed = 6.5f;
    public float acceleration = 8f;
    public float deceleration = 10f;
    [Tooltip("Дистанция начала торможения (м).")]
    public float slowdownDistance = 2.4f;
    public float arriveThreshold = 0.7f;
    [Tooltip("Скорость доворота корпуса.")]
    public float turnSpeed = 5f;

    [Header("Парение на месте")]
    public float bobAmplitude = 0.16f;
    public float bobHz = 0.45f;

    [Header("Столкновения вне боя")]
    [Tooltip("Слои, сквозь которые вне боя нельзя. В бою игнор.")]
    public LayerMask obstacleMask = ~0;
    public float bodyRadius = 0.4f;
    [Tooltip("Сколько боковых проб при упирании в стену.")]
    public int slideProbes = 5;

    [Header("Бой")]
    [Tooltip("Вкл — можно клипать сквозь объекты. Мозг выставит позже.")]
    public bool inCombat;

    [Header("Цель полёта (опционально)")]
    public Transform follow;
    [Tooltip("Если нет follow и нет MoveTo — висит на стартовой XZ.")]
    public bool hoverInPlace = true;

    [Header("Аниматор")]
    public Animator animator;

    public bool InCombat => inCombat;
    public bool HasGoal => _hasPoint || follow != null;
    public bool Arrived { get; private set; }
    public Vector3 Velocity => _vel;
    public Vector3 GoalPoint => _goal;

    private Vector3 _goal;
    private bool _hasPoint;
    private Vector3 _vel;
    private float _bobPhase;
    private Vector3 _home;
    private bool _homeSet;
    private readonly List<Vector3> _path = new List<Vector3>();
    private int _pathIndex;
    private float _tileSize = 4f;
    private Vector3 _mapOrigin;
    private CapsuleCollider _capsule;
    private Rigidbody _rb;

    public void MoveTo(Vector3 worldPoint)
    {
        follow = null;
        _goal = worldPoint;
        _hasPoint = true;
        Arrived = false;
        RebuildPath();
    }

    public void Follow(Transform t)
    {
        follow = t;
        _hasPoint = false;
        Arrived = false;
        _path.Clear();
        _pathIndex = 0;
    }

    public void Stop()
    {
        follow = null;
        _hasPoint = false;
        _path.Clear();
        _pathIndex = 0;
        _vel = Vector3.zero;
        Arrived = true;
    }

    public void SetInCombat(bool value) => inCombat = value;

    void Awake()
    {
        _capsule = GetComponent<CapsuleCollider>();
        _capsule.radius = Mathf.Max(_capsule.radius, bodyRadius);
        if (_capsule.height < 1.2f) _capsule.height = 1.6f;
        if (_capsule.center == Vector3.zero) _capsule.center = new Vector3(0f, 0.8f, 0f);
        _capsule.isTrigger = true;

        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (animator == null) animator = GetComponent<Animator>();
        if (animator != null) animator.applyRootMotion = false;

        _bobPhase = Random.value * Mathf.PI * 2f;
    }

    void Start()
    {
        ResolveWorld();
        _home = transform.position;
        _homeSet = true;
        SnapToHover(_home);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned) return;
        ResolveWorld();

        Vector3 pos = transform.position;
        Vector3 desired = DesiredPoint(pos);
        Vector3 flat = desired - pos;
        flat.y = 0f;
        float dist = flat.magnitude;

        float speedCap = inCombat ? combatSpeed : cruiseSpeed;
        Vector3 wish = Vector3.zero;
        if (HasGoal && dist > arriveThreshold)
        {
            Arrived = false;
            Vector3 dir = flat / Mathf.Max(dist, 0.001f);
            if (!inCombat)
                dir = AvoidObstacles(pos, dir, speedCap * dt);
            float slow = dist < slowdownDistance ? Mathf.Clamp01(dist / slowdownDistance) : 1f;
            wish = dir * (speedCap * slow);
        }
        else if (HasGoal)
        {
            Arrived = true;
        }

        _vel = Vector3.MoveTowards(_vel, wish, (wish.sqrMagnitude > _vel.sqrMagnitude ? acceleration : deceleration) * dt);

        Vector3 delta = _vel * dt;
        if (boundary != null)
            delta = boundary.Constrain(pos, delta);

        Vector3 next = pos + delta;
        if (!inCombat && pathfinder != null && pathfinder.IsReady && !pathfinder.IsWalkableWorld(next))
        {
            bool found;
            Vector3 safe = pathfinder.NearestWalkableWorld(next, out found);
            if (found)
            {
                next.x = safe.x;
                next.z = safe.z;
            }
            else
            {
                next.x = pos.x;
                next.z = pos.z;
                _vel.x = 0f;
                _vel.z = 0f;
            }
        }

        float ground = GroundY(next);
        _bobPhase += dt * bobHz * Mathf.PI * 2f;
        float bob = Mathf.Sin(_bobPhase) * bobAmplitude;
        next.y = ground + hoverHeight + bob;

        transform.position = next;

        Vector3 face = _vel;
        face.y = 0f;
        if (face.sqrMagnitude > 0.04f)
        {
            Quaternion look = Quaternion.LookRotation(face.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-turnSpeed * dt));
        }
    }

    Vector3 DesiredPoint(Vector3 pos)
    {
        if (follow != null)
        {
            Vector3 p = follow.position;
            p.y = GroundY(p) + hoverHeight;
            return p;
        }

        if (_hasPoint)
        {
            if (!inCombat && _path.Count > 0)
            {
                while (_pathIndex < _path.Count - 1)
                {
                    Vector3 wp = _path[_pathIndex];
                    Vector3 to = wp - pos;
                    to.y = 0f;
                    if (to.sqrMagnitude <= arriveThreshold * arriveThreshold)
                        _pathIndex++;
                    else
                        break;
                }
                Vector3 cur = _path[Mathf.Min(_pathIndex, _path.Count - 1)];
                cur.y = GroundY(cur) + hoverHeight;
                return cur;
            }

            Vector3 g = _goal;
            g.y = GroundY(g) + hoverHeight;
            return g;
        }

        if (hoverInPlace && _homeSet)
        {
            Vector3 h = _home;
            h.y = GroundY(h) + hoverHeight;
            return h;
        }

        return pos;
    }

    void RebuildPath()
    {
        _path.Clear();
        _pathIndex = 0;
        if (inCombat) return;
        if (pathfinder == null || !pathfinder.IsReady) return;
        pathfinder.TryFindPath(transform.position, _goal, _path);
    }

    Vector3 AvoidObstacles(Vector3 pos, Vector3 dir, float step)
    {
        if (step <= 0.0001f) return dir;
        float probe = Mathf.Max(step, bodyRadius * 1.5f);
        if (!Physics.SphereCast(pos, bodyRadius, dir, out RaycastHit hit, probe, obstacleMask, QueryTriggerInteraction.Ignore))
            return dir;

        Vector3 best = Vector3.zero;
        float bestDot = -2f;
        int n = Mathf.Max(slideProbes, 2);
        for (int i = 0; i < n; i++)
        {
            float ang = Mathf.Lerp(-75f, 75f, n == 1 ? 0.5f : i / (float)(n - 1));
            Vector3 side = Quaternion.Euler(0f, ang, 0f) * dir;
            if (Physics.SphereCast(pos, bodyRadius, side, out _, probe, obstacleMask, QueryTriggerInteraction.Ignore))
                continue;
            float dot = Vector3.Dot(dir, side);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = side;
            }
        }
        return best.sqrMagnitude > 0.01f ? best.normalized : Vector3.zero;
    }

    float GroundY(Vector3 world)
    {
        if (heightSource != null && heightSource.isGenerated)
            return heightSource.GetHeightAtWorldPos(world, _tileSize, _mapOrigin);

        Vector3 origin = world + Vector3.up * 40f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 80f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return 0f;
    }

    void SnapToHover(Vector3 world)
    {
        Vector3 p = world;
        p.y = GroundY(p) + hoverHeight;
        transform.position = p;
    }

    void ResolveWorld()
    {
        if (pathfinder == null)
            pathfinder = FindObjectOfType<Pathfinder>();
        if (heightSource == null && pathfinder != null)
            heightSource = pathfinder.heightSource;
        if (heightSource == null)
            heightSource = FindObjectOfType<HeightMapGenerator>();
        if (chunkedBuilder == null && heightSource != null)
            chunkedBuilder = heightSource.chunkedBuilder;
        if (chunkedBuilder == null)
            chunkedBuilder = FindObjectOfType<ChunkedTerrainBuilder>();
        if (boundary == null)
            boundary = FindObjectOfType<MapBoundary>();

        _tileSize = chunkedBuilder != null ? chunkedBuilder.tileSize : 4f;
        if (heightSource != null)
        {
            float hw = heightSource.width * _tileSize * 0.5f;
            float hd = heightSource.depth * _tileSize * 0.5f;
            _mapOrigin = new Vector3(-hw, 0f, -hd);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = inCombat ? new Color(0.7f, 0.4f, 1f, 0.7f) : new Color(0.4f, 0.8f, 1f, 0.7f);
        Gizmos.DrawWireSphere(transform.position, bodyRadius);
        if (HasGoal)
        {
            Gizmos.DrawLine(transform.position, DesiredPoint(transform.position));
            Gizmos.DrawSphere(DesiredPoint(transform.position), 0.15f);
        }
    }
}
