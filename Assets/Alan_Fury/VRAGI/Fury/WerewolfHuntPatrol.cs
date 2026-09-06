using UnityEngine;

/// <summary>
/// Охотничий патруль. Только точки маршрута + смотреть по сторонам + бросок на остановке.
/// Не читает живую позицию игрока. След пишет в NpcPerception.ReportCue.
/// Вешается на любого волка. Без Brain работает, если enabled.
/// </summary>
[RequireComponent(typeof(NpcPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfHuntPatrol : MonoBehaviour
{
    [Header("Ссылки")]
    public NpcPerception perception;
    public WerewolfLocomotion locomotion;
    public WerewolfWaypointRoute waypointRoute;

    [Header("Ход")]
    [Tooltip("Тихий шаг патруля (м/с). Ниже boundEnterSpeed.")]
    public float walkSpeed = 2.2f;
    [Tooltip("Прибытие в точку маршрута (м).")]
    public float arriveDistance = 2f;

    [Header("Смотреть по сторонам")]
    [Tooltip("Полуугол веера взгляда в движении (град).")]
    public float lookWeaveAngle = 35f;
    [Tooltip("Частота веера (Гц).")]
    public float lookWeaveHz = 0.35f;

    [Header("Остановка и бросок")]
    [Tooltip("Интервал между остановками (сек).")]
    public float stopInterval = 6f;
    [Tooltip("Разброс интервала (±сек).")]
    public float stopJitter = 2f;
    [Tooltip("Сколько стоять и мести мордой (сек).")]
    public float stopDuration = 1.6f;
    [Tooltip("Радиус броска «кто-то там» (м).")]
    public float scanRadius = 18f;
    [Tooltip("Базовый шанс на ближней дистанции при Noticeability=1.")]
    [Range(0f, 1f)] public float scanBaseChance = 0.35f;
    [Tooltip("Неопределённость следа после броска (м).")]
    public float scanCueRadius = 8f;

    private WerewolfBrain _brain;
    private float _stopIn;
    private float _stopLeft;
    private bool _rolledThisStop;
    private float _lookPhase;

    public void BindBrain(WerewolfBrain brain) => _brain = brain;

    void Awake()
    {
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (waypointRoute == null) waypointRoute = GetComponent<WerewolfWaypointRoute>();
    }

    void OnEnable()
    {
        _stopIn = NextStopWait();
        _stopLeft = 0f;
        _rolledThisStop = false;
        IWerewolfRoute r = Route;
        if (r != null && r.HasPoints) r.ResetToNearest(transform.position);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        IWerewolfRoute r = Route;
        if (r == null || !r.HasPoints)
        {
            SweepLook(transform.forward, lookWeaveAngle, dt);
            return;
        }

        if (_stopLeft > 0f)
        {
            _stopLeft -= dt;
            SweepLook(transform.forward, lookWeaveAngle * 2f, dt);
            if (!_rolledThisStop)
            {
                TryScan();
                _rolledThisStop = true;
            }
            if (_stopLeft <= 0f)
            {
                _stopIn = NextStopWait();
                _rolledThisStop = false;
            }
            return;
        }

        _stopIn -= dt;
        if (_stopIn <= 0f)
        {
            _stopLeft = stopDuration;
            _rolledThisStop = false;
            return;
        }

        Vector3 goal = r.CurrentPoint;
        bool arrived = Follow(goal, walkSpeed, dt);
        Vector3 look = r.LookHint(transform.position);
        Vector3 along = look - transform.position; along.y = 0f;
        if (along.sqrMagnitude < 0.01f) along = transform.forward;
        SweepLook(along.normalized, lookWeaveAngle, dt);

        if (arrived || FlatDist(transform.position, goal) <= arriveDistance)
            r.Advance();
    }

    private void SweepLook(Vector3 axis, float halfAngle, float dt)
    {
        _lookPhase += dt * lookWeaveHz * Mathf.PI * 2f;
        float ang = Mathf.Sin(_lookPhase) * halfAngle;
        Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * axis;
        Face(transform.position + dir * 4f, dt);
    }

    private void TryScan()
    {
        if (perception == null) return;
        var reg = PlayerRegistry.Instance;
        if (reg == null || reg.Count == 0) return;

        Vector3 self = transform.position;
        for (int i = 0; i < reg.Count; i++)
        {
            Transform t = reg.Players[i];
            if (t == null) continue;
            float dist = FlatDist(self, t.position);
            if (dist > scanRadius) continue;

            NpcPerception.ReadStealth(t, out float notice, out _);

            float falloff = 1f - Mathf.Clamp01(dist / Mathf.Max(0.01f, scanRadius));
            float chance = scanBaseChance * notice * falloff;
            if (Random.value > chance) continue;

            Vector3 to = t.position - self; to.y = 0f;
            if (to.sqrMagnitude < 0.01f) to = transform.forward;
            to.Normalize();
            Vector3 cue = self + to * dist;
            perception.ReportCue(cue, scanCueRadius);
            return;
        }
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

    private float NextStopWait() => stopInterval + Random.Range(-stopJitter, stopJitter);

    static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
