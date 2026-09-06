using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Мозг одиночного скелета. Движение и удар — только HumanoidLocomotion / HumanoidCombat.
/// Ритм: подойти шагом → серия ударов → щит и дистанция. Не спамит.
/// Временный визуал: кружок блока, треугольник замаха (цвет по стадии).
/// </summary>
[RequireComponent(typeof(HumanoidLocomotion))]
[RequireComponent(typeof(HumanoidCombat))]
[RequireComponent(typeof(NpcPerception))]
public class SkeletonBrain : MonoBehaviour
{
    public enum Mode { Idle, Patrol, Combat }

    [Header("Ссылки")]
    public HumanoidLocomotion locomotion;
    public HumanoidCombat combat;
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public Pathfinder pathfinder;
    public NpcPerception perception;

    [Header("Обнаружение")]
    public float aggroRadius = 16f;
    public float loseRadius = 24f;
    public bool drawVisionGizmo = true;
    public bool drawVisionAlways = false;

    [Header("Патруль (соло)")]
    public Transform[] waypoints;
    public float patrolRadius = 10f;
    public float arriveDistance = 1.2f;
    public float patrolWait = 1.8f;
    [Tooltip("1 walk / 2 run / 3 sprint")]
    public int patrolGait = 1;
    [Tooltip("Бег только если цель дальше farChaseDistance.")]
    public int chaseGait = 1;
    public int farChaseGait = 2;
    public float farChaseDistance = 9f;

    [Header("Бой")]
    public float attackCooldown = 0.7f;
    public float lightHold = 0.28f;
    public float heavyHold = 0.75f;
    [Range(0f, 1f)] public float heavyChance = 0.12f;
    public int comboHits = 2;
    public float recoverAfterCombo = 0.35f;
    public float spacingSlack = 0.55f;
    public float repathInterval = 0.55f;

    [Header("Блок")]
    [Tooltip("Щит после своей серии ударов (сек).")]
    public float blockAfterCombo = 1.15f;
    [Tooltip("Щит, если враг в замахе, пока сами не бьём.")]
    public float blockOnEnemyWindup = 0.7f;
    [Range(0f, 1f)] public float blockChance = 0.85f;
    public float blockRange = 5.5f;

    [Header("Индикаторы (временно)")]
    public bool showTells = true;
    public float tellHeight = 2.35f;
    public float tellSize = 0.32f;
    public Color windupIdle = new Color(0.45f, 0.45f, 0.45f, 0.35f);
    public Color windupCharge = new Color(0.95f, 0.25f, 0.15f, 0.9f);
    public Color windupWarm = new Color(0.95f, 0.85f, 0.2f, 0.95f);
    public Color windupHeavy = new Color(0.25f, 0.55f, 1f, 0.95f);
    public Color windupStrike = new Color(1f, 0.55f, 0.1f, 0.95f);
    public Color blockIdle = new Color(0.2f, 0.35f, 0.2f, 0.25f);
    public Color blockReady = new Color(0.25f, 0.85f, 0.3f, 0.7f);
    public Color blockHeld = new Color(0.95f, 0.85f, 0.2f, 0.95f);
    public Color blockRecover = new Color(0.55f, 0.55f, 0.6f, 0.55f);

    public Mode CurrentMode { get; private set; } = Mode.Idle;
    public Transform CurrentTarget { get; private set; }
    public bool ExternalControl { get; private set; }
    public bool IsGuarding => Time.time < _guardUntil;

    private Vector3 _home;
    private Vector3 _goal;
    private int _wpIndex;
    private float _waitTimer;
    private float _attackReadyTime;
    private float _holdUntil;
    private bool _holding;
    private bool _wantHeavy;
    private float _guardUntil;
    private int _hitsInCombo;
    private bool _windupBlockRolled;

    private readonly List<Vector3> _path = new List<Vector3>();
    private int _pathIndex;
    private float _repathTimer;
    private Vector3 _lastGoal;
    private const float RepathGoalMoveSqr = 4f;

    private Transform _tellRoot;
    private MeshRenderer _blockCircle;
    private MeshRenderer _windupTri;

    void Awake()
    {
        if (locomotion == null) locomotion = GetComponent<HumanoidLocomotion>();
        if (combat == null) combat = GetComponent<HumanoidCombat>();
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (pathfinder == null) pathfinder = FindObjectOfType<Pathfinder>();
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (perception == null)
            perception = gameObject.AddComponent<NpcPerception>();
        perception.ApplyHumanoid();
    }

    void Start()
    {
        _home = transform.position;
        _goal = _home;
        if (resources != null) resources.onDeath += HandleDeath;
        if (GetComponent<SkeletonStatsHUD>() == null)
            gameObject.AddComponent<SkeletonStatsHUD>();
        EnsureDrawn();
        if (showTells) BuildTells();
    }

    void OnDestroy()
    {
        if (resources != null) resources.onDeath -= HandleDeath;
    }

    void HandleDeath()
    {
        CurrentMode = Mode.Idle;
        CurrentTarget = null;
        if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
        if (combat != null)
        {
            if (combat.IsCharging) combat.CancelCharge();
            combat.SetBlocking(false);
            combat.ClearTarget();
        }
        if (_tellRoot != null) _tellRoot.gameObject.SetActive(false);
        enabled = false;
    }

    void Update()
    {
        if (resources != null && resources.IsDead) return;
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned)
        {
            if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
            return;
        }

        float dt = Time.deltaTime;
        if (!ExternalControl)
            TickSoloAcquire();

        if (CurrentTarget != null && !IsTargetAlive(CurrentTarget))
            CurrentTarget = null;

        if (CurrentTarget != null)
            CurrentMode = Mode.Combat;
        else if (HasPatrolWork())
            CurrentMode = Mode.Patrol;
        else
            CurrentMode = Mode.Idle;

        switch (CurrentMode)
        {
            case Mode.Combat:
                TickCombat(dt);
                break;
            case Mode.Patrol:
                TickPatrol(dt);
                break;
            default:
                IdleStand();
                break;
        }
    }

    void LateUpdate()
    {
        TickTells();
    }

    public void TakeExternalControl()
    {
        ExternalControl = true;
        ClearPath();
    }

    public void ReleaseExternalControl()
    {
        ExternalControl = false;
        CurrentTarget = null;
        _goal = transform.position;
        ClearPath();
    }

    public void CommandMoveTo(Vector3 worldPoint, int gait)
    {
        ExternalControl = true;
        CurrentTarget = null;
        _goal = worldPoint;
        _waitTimer = 0f;
        FollowPoint(worldPoint, gait);
    }

    public void CommandEngage(Transform target)
    {
        ExternalControl = true;
        CurrentTarget = IsTargetAlive(target) ? target : null;
    }

    public void CommandIdle()
    {
        ExternalControl = true;
        CurrentTarget = null;
        IdleStand();
    }

    void TickSoloAcquire()
    {
        if (CurrentTarget != null)
        {
            if (FlatDist(transform.position, CurrentTarget.position) > loseRadius
                || !IsTargetAlive(CurrentTarget))
            {
                CurrentTarget = null;
                ResetCombo();
            }
            return;
        }

        Transform nearest = null;
        if (PlayerRegistry.Instance != null)
            nearest = PlayerRegistry.Instance.GetNearestFlat(transform.position, aggroRadius);
        if (nearest == null)
        {
            Transform primary = PlayerRegistry.ResolvePrimary();
            if (primary != null && FlatDist(transform.position, primary.position) <= aggroRadius)
                nearest = primary;
        }
        if (nearest != null && IsTargetAlive(nearest) && CanSee(nearest))
            CurrentTarget = nearest;
    }

    bool HasPatrolWork()
    {
        return (waypoints != null && waypoints.Length > 0) || patrolRadius > 0.5f;
    }

    void TickPatrol(float dt)
    {
        if (_waitTimer > 0f)
        {
            _waitTimer -= dt;
            IdleStand();
            if (_waitTimer <= 0f) PickNextPatrolGoal();
            return;
        }

        if (_goal == Vector3.zero) PickNextPatrolGoal();

        if (FlatDist(transform.position, _goal) <= arriveDistance)
        {
            _waitTimer = patrolWait;
            IdleStand();
            return;
        }

        FollowPoint(_goal, patrolGait);
    }

    void PickNextPatrolGoal()
    {
        if (waypoints != null && waypoints.Length > 0)
        {
            _wpIndex = (_wpIndex + 1) % waypoints.Length;
            Transform wp = waypoints[_wpIndex];
            _goal = wp != null ? wp.position : _home;
        }
        else
        {
            Vector2 r = Random.insideUnitCircle * patrolRadius;
            _goal = _home + new Vector3(r.x, 0f, r.y);
        }

        if (pathfinder != null && pathfinder.IsReady)
            _goal = pathfinder.NearestWalkableWorld(_goal, out _);
        ClearPath();
    }

    void TickCombat(float dt)
    {
        Transform t = CurrentTarget;
        if (t == null)
        {
            IdleStand();
            return;
        }

        if (combat != null)
        {
            combat.CommandTarget = t;
            Vector3 aim = t.position - transform.position;
            aim.y = 0f;
            if (aim.sqrMagnitude > 0.01f)
                combat.AimDirection = aim.normalized;
            EnsureDrawn();
        }

        float dist = FlatDist(transform.position, t.position);
        float reach = ResolveReach();
        float ideal = reach * (combat != null ? combat.spacingIdealFraction : 0.72f);

        Vector3 to = t.position - transform.position;
        to.y = 0f;
        Vector3 face = to.sqrMagnitude > 0.01f ? to.normalized : transform.forward;
        if (locomotion != null) locomotion.SetFace(face);

        HoldDistance(dist, ideal, face);

        if (combat != null && combat.IsCharging)
        {
            if (Time.time >= _holdUntil)
            {
                combat.ReleaseAttack();
                _holding = false;
                OnSwingCommitted();
            }
            return;
        }

        if (combat != null && combat.IsInAttackPipeline)
            return;

        if (TickGuard(t, dist, face))
            return;

        if (dist > ideal + spacingSlack + 0.8f)
            return;

        if (Time.time < _attackReadyTime || combat == null) return;

        _wantHeavy = _hitsInCombo >= comboHits - 1 && Random.value < heavyChance;
        if (combat.TryHoldAttack())
        {
            _holding = true;
            _holdUntil = Time.time + (_wantHeavy ? heavyHold : lightHold);
        }
    }

    void HoldDistance(float dist, float ideal, Vector3 face)
    {
        if (locomotion == null) return;
        if (combat != null && combat.IsInAttackPipeline)
        {
            locomotion.SetMove(Vector3.zero, 1, false);
            return;
        }

        float error = dist - ideal;
        if (Mathf.Abs(error) <= spacingSlack)
        {
            locomotion.SetMove(Vector3.zero, 1, false);
            return;
        }

        if (error < 0f)
        {
            locomotion.SetMove(-face, 1, false);
            return;
        }

        int gait = dist >= farChaseDistance ? farChaseGait : chaseGait;
        if (dist > ideal + 2.5f)
            FollowPoint(CurrentTarget.position, gait);
        else
            locomotion.SetMove(face, 1, false);
    }

    void OnSwingCommitted()
    {
        _hitsInCombo++;
        _attackReadyTime = Time.time + attackCooldown;
        if (_hitsInCombo >= comboHits)
        {
            _hitsInCombo = 0;
            _attackReadyTime = Time.time + recoverAfterCombo;
            if (CanBlock())
                _guardUntil = Time.time + blockAfterCombo;
        }
    }

    bool TickGuard(Transform t, float dist, Vector3 face)
    {
        if (combat == null || !CanBlock())
        {
            if (combat != null) combat.SetBlocking(false);
            return false;
        }

        bool incoming = dist <= blockRange && TargetIsAttacking(t);
        if (incoming)
        {
            if (!_windupBlockRolled)
            {
                _windupBlockRolled = true;
                if (Random.value <= blockChance)
                    _guardUntil = Mathf.Max(_guardUntil, Time.time + blockOnEnemyWindup);
            }
        }
        else
            _windupBlockRolled = false;

        bool guard = Time.time < _guardUntil;
        combat.SetBlocking(guard);
        if (!guard) return false;

        if (locomotion != null)
        {
            float reach = ResolveReach();
            float ideal = reach * (combat.spacingIdealFraction);
            float error = dist - ideal;
            if (error < -spacingSlack)
                locomotion.SetMove(-face, 1, false);
            else
                locomotion.SetMove(Vector3.zero, 1, false);
        }
        return true;
    }

    void FollowPoint(Vector3 worldPoint, int gait)
    {
        if (locomotion == null) return;

        Vector3 dest = worldPoint;
        if (pathfinder != null && pathfinder.IsReady)
        {
            _repathTimer -= Time.deltaTime;
            bool need = _path.Count == 0 || _pathIndex >= _path.Count
                     || _repathTimer <= 0f || FlatSqr(worldPoint, _lastGoal) > RepathGoalMoveSqr;
            if (need)
            {
                _repathTimer = repathInterval;
                _lastGoal = worldPoint;
                dest = pathfinder.NearestWalkableWorld(worldPoint, out _);
                if (pathfinder.TryFindPath(transform.position, dest, _path))
                    _pathIndex = 0;
                else
                    _path.Clear();
            }

            if (_path.Count > 0 && _pathIndex < _path.Count)
            {
                Vector3 wp = _path[_pathIndex];
                if (FlatDist(transform.position, wp) <= arriveDistance)
                {
                    _pathIndex++;
                    if (_pathIndex >= _path.Count)
                    {
                        ClearPath();
                        locomotion.SetMove(Vector3.zero, gait, false);
                        return;
                    }
                    wp = _path[_pathIndex];
                }
                dest = wp;
            }
        }

        Vector3 dir = dest - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f)
        {
            locomotion.SetMove(Vector3.zero, gait, false);
            return;
        }
        dir.Normalize();
        locomotion.SetMove(dir, gait, false);
        locomotion.SetFace(dir);
    }

    void IdleStand()
    {
        if (locomotion != null)
            locomotion.SetMove(Vector3.zero, 1, false);
        if (combat != null)
        {
            combat.SetBlocking(false);
            if (CurrentTarget == null)
                combat.CommandTarget = null;
        }
        ResetCombo();
    }

    void ResetCombo()
    {
        _hitsInCombo = 0;
        _holding = false;
        _guardUntil = 0f;
        _windupBlockRolled = false;
        if (combat != null) combat.SetBlocking(false);
    }

    void EnsureDrawn()
    {
        if (combat == null) return;
        if (CanBlock())
        {
            if (!combat.IsArmed || !combat.IsShieldArmed)
                combat.DrawAll();
        }
        else if (!combat.IsArmed)
            combat.DrawSword();
    }

    bool CanBlock()
    {
        return loadout != null && loadout.HasShield();
    }

    bool TargetIsAttacking(Transform t)
    {
        if (t == null) return false;
        var hc = t.GetComponentInParent<HumanoidCombat>();
        return hc != null && hc.IsInAttackPipeline;
    }

    void ClearPath()
    {
        _path.Clear();
        _pathIndex = 0;
        _repathTimer = 0f;
    }

    float ResolveReach()
    {
        WeaponData w = loadout != null ? loadout.GetMainWeapon() : null;
        if (w != null) return w.ScaledRange;
        return 2f * WeaponData.RangeScale;
    }

    static bool IsTargetAlive(Transform t)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return false;
        var dmg = t.GetComponentInParent<IDamageable>();
        return dmg == null || dmg.IsAlive;
    }

    static float FlatSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    static float FlatDist(Vector3 a, Vector3 b) => Mathf.Sqrt(FlatSqr(a, b));

    // ——— временные знаки: кружок блок / треугольник замах ———

    void BuildTells()
    {
        _tellRoot = new GameObject("SkeletonTells").transform;
        _tellRoot.SetParent(transform, false);
        _tellRoot.localPosition = new Vector3(0f, tellHeight, 0f);

        _blockCircle = MakeCircle(_tellRoot, "BlockCircle", new Vector3(-0.28f, 0f, 0f));
        _windupTri = MakeTriangle(_tellRoot, "WindupTriangle", new Vector3(0.28f, 0f, 0f));
    }

    MeshRenderer MakeCircle(Transform parent, string name, Vector3 local)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = new Vector3(tellSize, 0.012f, tellSize);
        return ApplyUnlit(go);
    }

    MeshRenderer MakeTriangle(Transform parent, string name, Vector3 local)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localScale = Vector3.one * tellSize;

        var mf = go.AddComponent<MeshFilter>();
        var mesh = new Mesh { name = "TellTriangle" };
        float h = 0.6f;
        mesh.vertices = new[]
        {
            new Vector3(0f, h, 0f),
            new Vector3(-0.5f, -h * 0.5f, 0f),
            new Vector3(0.5f, -h * 0.5f, 0f)
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 1 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.mesh = mesh;
        go.AddComponent<MeshRenderer>();
        return ApplyUnlit(go);
    }

    static MeshRenderer ApplyUnlit(GameObject go)
    {
        var mr = go.GetComponent<MeshRenderer>();
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var mat = new Material(sh);
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
        }
        mr.material = mat;
        return mr;
    }

    void TickTells()
    {
        if (!showTells || _tellRoot == null) return;

        bool live = CurrentMode == Mode.Combat && combat != null;
        _tellRoot.gameObject.SetActive(live);
        if (!live) return;

        if (Camera.main != null)
            _tellRoot.rotation = Camera.main.transform.rotation;

        if (_windupTri != null)
        {
            Color c = windupIdle;
            bool on = false;
            if (combat.IsCharging)
            {
                on = true;
                if (combat.IsHeavyReady) c = windupHeavy;
                else
                {
                    float t = Mathf.Clamp01(combat.ChargePercent / Mathf.Max(0.01f, combat.heavyChargeThreshold));
                    c = Color.Lerp(windupCharge, windupWarm, t);
                }
            }
            else if (combat.IsWindingUp || combat.IsAttacking)
            {
                on = true;
                c = windupStrike;
            }
            _windupTri.enabled = on;
            if (on) _windupTri.material.color = c;
        }

        if (_blockCircle != null)
        {
            Color c = blockIdle;
            bool on = CanBlock();
            if (combat.IsBlocking) c = blockHeld;
            else if (IsGuarding) c = blockRecover;
            else if (CurrentMode == Mode.Combat) c = blockReady;
            _blockCircle.enabled = on;
            if (on) _blockCircle.material.color = c;
        }
    }

    bool CanSee(Transform t)
    {
        if (perception != null) return perception.CanSee(t);
        return t != null && FlatDist(transform.position, t.position) <= aggroRadius;
    }

    void OnDrawGizmos()
    {
        if (drawVisionGizmo && drawVisionAlways) DrawVisionGizmo();
    }

    void OnDrawGizmosSelected()
    {
        DrawVisionGizmo();
    }

    void DrawVisionGizmo()
    {
        Gizmos.color = new Color(0.8f, 0.85f, 0.4f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, aggroRadius);
        Gizmos.color = new Color(0.55f, 0.55f, 0.55f, 0.2f);
        Gizmos.DrawWireSphere(transform.position, loseRadius);
    }
}
