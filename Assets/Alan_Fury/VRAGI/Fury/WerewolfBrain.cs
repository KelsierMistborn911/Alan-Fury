using UnityEngine;

/// <summary>
/// ’оз€ин добоевых режимов оборотн€. ¬ешаетс€ по желанию Ч без него старые мозги живут как раньше.
///
/// –ежимы:
///   Patrol       Ч HuntPatrol
///   Investigate  Ч след есть, lock нет
///   Stalk        Ч lock (Notice дошЄл до 1)
///   Combat       Ч зарезервирован, вход через RequestCombat(); бой пока не прив€зан
///
/// ќдновременно включЄн один навесной скрипт. —крипты только ход€т и смотр€т.
/// AttackBrain / Surround сюда ещЄ не заведены.
/// </summary>
[RequireComponent(typeof(NpcPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfBrain : MonoBehaviour
{
    public enum Mode { None, Patrol, Investigate, Stalk, Combat }

    [Header("—сылки (пусто Ч GetComponent)")]
    public NpcPerception perception;
    public WerewolfLocomotion locomotion;
    public Pathfinder pathfinder;
    public IWerewolfRoute route;
    public WerewolfWaypointRoute waypointRoute;
    public WerewolfHuntPatrol patrol;
    public WerewolfInvestigate investigate;
    public WerewolfAlphaStalker stalker;

    [Header("ѕуть")]
    public float pathRepathInterval = 0.4f;

    public Mode CurrentMode => _mode;
    public IWerewolfRoute Route => route ?? (IWerewolfRoute)waypointRoute;

    private Mode _mode = Mode.None;
    private bool _combatRequested;

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
        if (investigate == null) investigate = GetComponent<WerewolfInvestigate>();
        if (stalker == null) stalker = GetComponent<WerewolfAlphaStalker>();
        if (waypointRoute == null) waypointRoute = GetComponent<WerewolfWaypointRoute>();
        if (pathfinder == null && WerewolfPackManager.Instance != null)
            pathfinder = WerewolfPackManager.Instance.pathfinder;
    }

    void Start()
    {
        if (patrol != null) patrol.BindBrain(this);
        if (investigate != null) investigate.BindBrain(this);
        ApplyMode(Mode.None, force: true);
    }

    void Update()
    {
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned) return;

        if (_combatRequested)
        {
            if (_mode != Mode.Combat) ApplyMode(Mode.Combat);
            return;
        }

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

    /// <summary>Ѕой заберЄм сюда позже. ѕока только глушит добоевые скрипты.</summary>
    public void RequestCombat()
    {
        _combatRequested = true;
    }

    public void ReleaseCombat()
    {
        _combatRequested = false;
    }

    /// <summary>–асследование закончило отход на маршрут.</summary>
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

    // ЧЧЧ движение дл€ навесных режимов ЧЧЧ

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
        {
            float d = FlatDist(transform.position, goal);
            return d <= 2f;
        }

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
}
