using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Расследование следа. Скрытный ход к диску cue. Живого player.pos не читает.
/// След протух / диск пуст → быстрый отход на маршрут по укрытиям (FogPatch).
/// Вешается на любого волка.
/// </summary>
[RequireComponent(typeof(NpcPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfInvestigate : MonoBehaviour
{
    [Header("Ссылки")]
    public NpcPerception perception;
    public WerewolfLocomotion locomotion;
    public WerewolfWaypointRoute waypointRoute;
    public Pathfinder pathfinder;

    [Header("Ход")]
    [Tooltip("Тихий шаг к следу (м/с).")]
    public float sneakSpeed = 2.0f;
    [Tooltip("Отход на маршрут после потери следа (м/с).")]
    public float retreatSpeed = 8f;

    [Header("След")]
    [Tooltip("Считать диск проверенным, если стоим внутри столько секунд без освежения.")]
    public float inspectSeconds = 2.2f;
    [Tooltip("Дополнительный запас к радиусу диска при «дошли» (м).")]
    public float arriveSlack = 1.5f;

    [Header("Укрытия отхода")]
    public string coverTag = "FogPatch";
    public float coverRadius = 12f;

    private WerewolfBrain _brain;
    private bool _retreating;
    private float _inspectLeft;
    private readonly List<Transform> _covers = new List<Transform>();
    private Transform _coverStep;
    private Vector3 _resume;

    public bool IsRetreating => _retreating;

    public void BindBrain(WerewolfBrain brain) => _brain = brain;

    void Awake()
    {
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (waypointRoute == null) waypointRoute = GetComponent<WerewolfWaypointRoute>();
        if (pathfinder == null && WerewolfPackManager.Instance != null)
            pathfinder = WerewolfPackManager.Instance.pathfinder;
        RefreshCovers();
    }

    void OnEnable()
    {
        _retreating = perception == null || !perception.HasCue;
        _inspectLeft = inspectSeconds;
        _coverStep = null;
        if (_retreating) BeginRetreat();
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (perception == null) return;

        if (_retreating && perception.HasCue && perception.CueAge < 0.35f)
        {
            _retreating = false;
            _inspectLeft = inspectSeconds;
            _coverStep = null;
        }

        if (!_retreating && !perception.HasCue)
        {
            BeginRetreat();
        }

        if (_retreating)
        {
            TickRetreat(dt);
            return;
        }

        Vector3 cue = perception.CuePos;
        float rad = Mathf.Max(0.5f, perception.CueRadius) + arriveSlack;
        Face(cue, dt);

        float dist = FlatDist(transform.position, cue);
        if (dist > rad)
        {
            _inspectLeft = inspectSeconds;
            Follow(cue, sneakSpeed, dt);
            return;
        }

        // В диске: топчемся, смотрим, ждём освежения следа.
        Follow(cue, sneakSpeed * 0.4f, dt);
        if (perception.CueAge < 0.4f)
            _inspectLeft = inspectSeconds;
        else
            _inspectLeft -= dt;

        if (_inspectLeft <= 0f)
        {
            perception.ClearCue();
            BeginRetreat();
        }
    }

    private void BeginRetreat()
    {
        _retreating = true;
        _coverStep = null;
        IWerewolfRoute r = Route;
        _resume = r != null && r.HasPoints
            ? r.ResumePoint(transform.position)
            : transform.position;
        _resume = Clamp(_resume);
        PickCoverTowardResume();
    }

    private void TickRetreat(float dt)
    {
        Vector3 goal = _coverStep != null ? _coverStep.position : _resume;
        goal = Clamp(goal);
        Face(goal, dt);
        bool reached = Follow(goal, retreatSpeed, dt) || FlatDist(transform.position, goal) <= 2.2f;

        if (!reached) return;

        if (_coverStep != null)
        {
            _coverStep = null;
            if (FlatDist(transform.position, _resume) > 3f)
            {
                PickCoverTowardResume();
                if (_coverStep != null) return;
            }
        }

        IWerewolfRoute r = Route;
        if (r != null && r.HasPoints) r.ResetToNearest(transform.position);
        _retreating = false;
        if (_brain != null) _brain.OnReturnedToRoute();
        else enabled = false;
    }

    private void PickCoverTowardResume()
    {
        _coverStep = null;
        Vector3 self = transform.position;
        float selfToResume = FlatDist(self, _resume);
        if (selfToResume <= 4f) return;

        Transform best = null;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < _covers.Count; i++)
        {
            Transform c = _covers[i];
            if (c == null) continue;
            float toResume = FlatDist(c.position, _resume);
            if (toResume >= selfToResume - 1f) continue; // не дальше от маршрута, чем мы
            float toSelf = FlatDist(self, c.position);
            if (toSelf < 2f) continue;
            float score = -toResume - toSelf * 0.25f;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        _coverStep = best;
    }

    public void RefreshCovers()
    {
        _covers.Clear();
        if (string.IsNullOrEmpty(coverTag)) return;
        GameObject[] found;
        try { found = GameObject.FindGameObjectsWithTag(coverTag); }
        catch (UnityException)
        {
            return;
        }
        foreach (var go in found) _covers.Add(go.transform);
    }

    private IWerewolfRoute Route
    {
        get
        {
            if (_brain != null && _brain.Route != null) return _brain.Route;
            return waypointRoute;
        }
    }

    private bool Follow(Vector3 goal, float speed, float dt)
    {
        if (_brain != null) return _brain.FollowGoal(goal, speed, dt);
        if (locomotion == null) return false;
        return locomotion.MoveTo(goal, speed, dt);
    }

    private void Face(Vector3 p, float dt)
    {
        if (_brain != null) _brain.Face(p, dt);
        else if (locomotion != null) locomotion.FaceTowards(p, dt);
    }

    private Vector3 Clamp(Vector3 p)
    {
        if (_brain != null) return _brain.ClampGoal(p);
        if (pathfinder != null && pathfinder.IsReady)
            return pathfinder.NearestWalkableWorld(p, out _);
        return p;
    }

    static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
