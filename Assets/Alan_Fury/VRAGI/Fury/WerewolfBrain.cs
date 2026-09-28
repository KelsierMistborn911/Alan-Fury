using UnityEngine;

/// <summary>
/// Хозяин добоевых режимов оборотня. Вешается по желанию — без него старые мозги живут как раньше.
///
/// Режимы:
///   Patrol       — HuntPatrol
///   Investigate  — след есть, lock нет
///   Stalk        — lock (Notice дошёл до 1)
///   Combat       — зарезервирован, вход через RequestCombat(); бой пока не привязан
///
/// Одновременно включён один навесной скрипт. Скрипты только ходят и смотрят.
/// AttackBrain / Surround сюда ещё не заведены.
/// </summary>
[RequireComponent(typeof(NpcPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfBrain : MonoBehaviour
{
    public enum Mode { None, Patrol, Investigate, Stalk, Combat, Herald, Assemble }

    [Header("Ссылки (пусто — GetComponent)")]
    public NpcPerception perception;
    public WerewolfLocomotion locomotion;
    public Pathfinder pathfinder;
    public IWerewolfRoute route;
    public WerewolfWaypointRoute waypointRoute;
    public WerewolfHuntPatrol patrol;
    public WerewolfInvestigate investigate;
    public WerewolfAlphaStalker stalker;

    [Header("Путь")]
    public float pathRepathInterval = 0.4f;

    public Mode CurrentMode => _mode;
    public bool CombatRequested => _combatRequested;
    public IWerewolfRoute Route => route ?? (IWerewolfRoute)waypointRoute;

    private Mode _mode = Mode.None;
    private bool _combatRequested;
    private bool _heraldMission;
    private bool _assembleMission;

    private readonly System.Collections.Generic.List<Vector3> _path = new System.Collections.Generic.List<Vector3>();
    private int _pathIndex;
    private float _repathTimer;
    private Vector3 _lastGoal;
    private const float RepathGoalMoveSqr = 9f;

    void Awake()
    {
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (patrol == null) patrol = GetComponent<WerewolfHuntPatrol>();
        if (patrol == null) patrol = gameObject.AddComponent<WerewolfHuntPatrol>();
        if (investigate == null) investigate = GetComponent<WerewolfInvestigate>();
        if (investigate == null) investigate = gameObject.AddComponent<WerewolfInvestigate>();
        if (stalker == null) stalker = GetComponent<WerewolfAlphaStalker>();
        if (waypointRoute == null) waypointRoute = GetComponent<WerewolfWaypointRoute>();
        if (waypointRoute == null) waypointRoute = gameObject.AddComponent<WerewolfWaypointRoute>();
        if (pathfinder == null && WerewolfPackManager.Instance != null)
            pathfinder = WerewolfPackManager.Instance.pathfinder;
    }

    void Start()
    {
        if (patrol != null) patrol.BindBrain(this);
        if (investigate != null) investigate.BindBrain(this);
        ApplyMode(Mode.Patrol, force: true);
    }

    void Update()
    {
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned) return;

        if (IsSelfAlpha())
            _combatRequested = false;
        else if (perception != null && perception.IsLocked)
            RequestCombat();
        else if (perception != null && !perception.IsLocked)
            ReleaseCombat();

        if (_combatRequested)
        {
            if (_mode != Mode.Combat) ApplyMode(Mode.Combat);
            var atk = GetComponent<WerewolfAttackBrain>();
            if (atk == null || !atk.enabled)
            {
                Vector3 goal = perception != null && perception.HasPlayer
                    ? perception.PlayerPos
                    : (perception != null && perception.HasCue ? perception.CuePos : transform.position);
                Face(goal, Time.deltaTime);
                FollowGoal(goal, 8f, Time.deltaTime);
            }
            return;
        }

        if (_heraldMission)
        {
            TickHeraldWalk(Time.deltaTime);
            return;
        }

        if (_assembleMission)
        {
            TickAssemble(Time.deltaTime);
            return;
        }

        var pack = WerewolfPackManager.Instance;
        if (pack != null && pack.TryConsumeSignal(this, out WerewolfHowl.Type howl, out Vector3 at, out float rad))
            ApplyPackSignal(howl, at, rad);

        if (perception == null) return;

        Mode want;
        if (perception.IsLocked) want = Mode.Stalk;
        else if (perception.HasCue || (investigate != null && investigate.IsRetreating))
            want = Mode.Investigate;
        else
            want = Mode.Patrol;

        if (want == Mode.Stalk && stalker == null) want = perception.HasCue ? Mode.Investigate : Mode.Patrol;
        if (want == Mode.Investigate && investigate == null) want = Mode.Patrol;
        if (want == Mode.Patrol && patrol == null) want = Mode.None;

        if (want != _mode) ApplyMode(want);
    }

    /// <summary>Бой заберём сюда позже. Пока только глушит добоевые скрипты.</summary>
    public void RequestCombat()
    {
        if (IsSelfAlpha()) return;
        if (_combatRequested) return;
        _combatRequested = true;
        var pack = WerewolfPackManager.Instance;
        if (pack != null) pack.NotifyLocalContact();
    }

    public void ReleaseCombat()
    {
        if (!_combatRequested) return;
        _combatRequested = false;
        var pack = WerewolfPackManager.Instance;
        if (pack != null) pack.NotifyLocalContact();
    }

    public void BeginHerald()
    {
        _heraldMission = true;
        ApplyMode(Mode.Herald);
    }

    public void EndHerald()
    {
        _heraldMission = false;
    }

    public void BeginAssemble()
    {
        _assembleMission = true;
        ApplyMode(Mode.Assemble);
    }

    void ApplyPackSignal(WerewolfHowl.Type type, Vector3 at, float rad)
    {
        switch (type)
        {
            case WerewolfHowl.Type.Contact:
            case WerewolfHowl.Type.Rally:
            case WerewolfHowl.Type.Lost:
            case WerewolfHowl.Type.BeaterPing:
                if (perception != null) perception.ReportCue(at, Mathf.Max(4f, rad));
                break;
            case WerewolfHowl.Type.Assemble:
                BeginAssemble();
                break;
            case WerewolfHowl.Type.OrderAttack:
            case WerewolfHowl.Type.OrderPursue:
            case WerewolfHowl.Type.OrderRejoin:
                if (perception != null) perception.ReportCue(at, Mathf.Max(4f, rad));
                break;
            case WerewolfHowl.Type.OrderRetreat:
                ReleaseCombat();
                if (perception != null) perception.ClearCue();
                break;
        }
    }

    void TickHeraldWalk(float dt)
    {
        if (_mode != Mode.Herald) ApplyMode(Mode.Herald);
        var pack = WerewolfPackManager.Instance;
        if (pack == null || pack.player == null)
        {
            _heraldMission = false;
            return;
        }
        Vector3 self = transform.position;
        Vector3 player = pack.player.position;
        Vector3 alpha = pack.alphaTransform != null ? pack.alphaTransform.position : self;
        Vector3 away = self - player;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = transform.forward;
        away.Normalize();
        Vector3 fromAlpha = self - alpha;
        fromAlpha.y = 0f;
        if (fromAlpha.sqrMagnitude > 0.01f) away = (away + fromAlpha.normalized).normalized;
        Vector3 goal = ClampGoal(self + away * 8f);
        FollowGoal(goal, 8f, dt);
    }

    void TickAssemble(float dt)
    {
        if (_mode != Mode.Assemble) ApplyMode(Mode.Assemble);
        var pack = WerewolfPackManager.Instance;
        Vector3 dest = pack != null && pack.alphaTransform != null
            ? pack.alphaTransform.position
            : transform.position;
        dest = ClampGoal(dest);
        Face(dest, dt);
        if (FollowGoal(dest, 8f, dt) || FlatDist(transform.position, dest) <= 3f)
            _assembleMission = false;
    }

    /// <summary>Расследование закончило отход на маршрут.</summary>
    public void OnReturnedToRoute()
    {
        if (_combatRequested) return;
        if (perception != null) perception.ClearCue();
        ApplyMode(patrol != null ? Mode.Patrol : Mode.None);
    }

    private void ApplyMode(Mode m, bool force = false)
    {
        if (!force && _mode == m) return;
        _mode = m;
        SetEnabled(patrol, m == Mode.Patrol);
        SetEnabled(investigate, m == Mode.Investigate);
        SetEnabled(stalker, m == Mode.Stalk);
        ClearPath();
    }

    private static void SetEnabled(Behaviour b, bool on)
    {
        if (b != null && b.enabled != on) b.enabled = on;
    }

    // ——— движение для навесных режимов ———

    public bool FollowGoal(Vector3 goal, float speed, float dt)
    {
        if (locomotion == null) return false;
        goal = ClampGoal(goal);
        if (pathfinder == null || !pathfinder.IsReady)
            return locomotion.MoveTo(goal, speed, dt);

        _repathTimer -= dt;
        bool need = _path.Count == 0 || _pathIndex >= _path.Count
                 || _repathTimer <= 0f || FlatSqr(goal, _lastGoal) > RepathGoalMoveSqr;
        if (need)
        {
            _repathTimer = pathRepathInterval;
            _lastGoal = goal;
            if (pathfinder.TryFindPath(transform.position, goal, _path, avoidRoads: true)) _pathIndex = 0;
            else _path.Clear();
        }

        if (_path.Count == 0)
            return locomotion.MoveTo(goal, speed, dt);

        Vector3 wp = _path[_pathIndex];
        if (locomotion.MoveTo(wp, speed, dt))
        {
            _pathIndex++;
            if (_pathIndex >= _path.Count) return true;
        }
        return false;
    }

    public void Face(Vector3 worldPoint, float dt)
    {
        if (locomotion != null) locomotion.FaceTowards(worldPoint, dt);
    }

    public Vector3 ClampGoal(Vector3 goal)
    {
        if (pathfinder == null || !pathfinder.IsReady) return goal;
        return pathfinder.NearestWalkableWorld(goal, out _);
    }

    public void ClearPath()
    {
        _path.Clear();
        _pathIndex = 0;
        _repathTimer = 0f;
    }

    static float FlatSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    static float FlatDist(Vector3 a, Vector3 b) => Mathf.Sqrt(FlatSqr(a, b));

    bool IsSelfAlpha()
    {
        var pack = WerewolfPackManager.Instance;
        return pack != null && pack.IsAlphaTransform(transform);
    }
}
