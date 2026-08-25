using UnityEngine;

/// <summary>
/// Мозг волка-окружателя (роль Surround).
/// Волк держит сектор кольца, выданный менеджером стаи, и ищет в нём лучшую позицию:
/// подальше от соседей, поближе к середине своего сектора, покороче переход.
/// Сблизился игрок — отступает. На взгляд игрока не реагирует, это открытое окружение.
///
/// Ничего сам не рендерит и не бьёт: восприятие — WerewolfPerception,
/// движение и повороты — WerewolfLocomotion, сектор — WerewolfPackManager.
/// </summary>
[RequireComponent(typeof(WerewolfPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfSurroundBrain : MonoBehaviour
{
    [Header("Компоненты (подтянутся сами, если пусто)")]
    public WerewolfPerception perception;
    public WerewolfLocomotion locomotion;

    [Header("Дистанции (м)")]
    [Tooltip("Внешний край полосы окружения.")]
    public float preferredDistance = 32f;
    [Tooltip("Внутренний край полосы. Игрок ближе — волк отступает.")]
    public float minDistance = 20f;
    [Tooltip("Только для Gizmo в редакторе. В логике не используется.")]
    public float noticeRange = 30f;

    [Header("Скорости (м/с)")]
    [Tooltip("Скорость обхода. Ниже boundSpeedThreshold, чтобы шёл шагом.")]
    public float circleSpeed = 4f;
    [Tooltip("Скорость отступа. Выше boundSpeedThreshold, чтобы отпрыгивал скачками.")]
    public float fleeSpeed = 10f;

    [Header("Отступление")]
    [Tooltip("Во сколько раз дальше preferredDistance отбегает при сближении.")]
    public float retreatDistanceFactor = 1.3f;

    [Header("Ритм")]
    [Tooltip("Как часто волк пересчитывает позицию (сек).")]
    public float holdInterval = 1f;
    [Tooltip("Разброс к интервалу (±сек). 0 — строго по таймеру.")]
    public float holdJitter = 0f;

    [Header("Выбор точки в секторе")]
    [Tooltip("Сколько случайных точек в своём секторе перебрать за раз.")]
    public int candidateCount = 6;
    [Tooltip("Насколько новая точка должна быть лучше текущей, чтобы волк тронулся с места.")]
    public float switchMargin = 1.5f;
    [Tooltip("Радиус тесноты: соседи ближе этого портят оценку точки (м).")]
    public float crowdRadius = 4f;
    [Tooltip("Вес тесноты в оценке точки.")]
    public float crowdWeight = 1f;
    [Tooltip("Вес отклонения от середины полосы радиусов. 0 — любая глубина полосы равноценна.")]
    public float bandWeight = 0f;
    [Tooltip("Вес отклонения от середины своего сектора. Выше — волки стоят по кольцу ровнее.")]
    public float sectorCenterWeight = 0.5f;
    [Tooltip("Вес длины перехода: выше — волк ленивее двигаться.")]
    public float travelWeight = 0.3f;

    [Header("Расталкивание")]
    [Tooltip("С какой дистанции волки отжимают друг друга (м).")]
    public float separationRadius = 5f;
    [Tooltip("Сила отжима. Выше — расходятся резче.")]
    public float separationStrength = 1f;
    [Tooltip("Толчок короче этого игнорируем, иначе волк дрожит на месте (м).")]
    public float separationDeadzone = 0.3f;

    private enum State { Hold, Circle, Retreat }
    private State _state = State.Hold;
    private Vector3 _target;
    private float _holdTimer;

    void Start()
    {
        if (perception == null) perception = GetComponent<WerewolfPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();

        _target = transform.position;
        ResetHoldTimer();
    }

    void Update()
    {
        if (perception == null || !perception.HasPlayer) return;

        float dt = Time.deltaTime;
        float dist = perception.DistanceToPlayer;
        var pack = WerewolfPackManager.Instance;
        bool packScatter = pack != null && pack.IsPackScattering;

        // Срыв стаи — все жмут наружу; иначе отступаем только от сближения.
        bool threatened = packScatter || dist < minDistance;

        // Стойка: на месте / медленный hold → Biped (наблюдение);
        // круг и отход/разбег → Quad (бег).
        bool wantBiped = _state == State.Hold && !packScatter;
        if (locomotion != null)
        {
            locomotion.SetStance(wantBiped
                ? WerewolfLocomotion.Stance.Biped
                : WerewolfLocomotion.Stance.Quad);
            if (locomotion.IsChangingStance) return;
        }

        switch (_state)
        {
            case State.Hold:
                if (packScatter) { EnterRetreat(); break; }
                locomotion.FaceTowards(perception.PlayerPos, dt);
                if (threatened) { EnterRetreat(); break; }

                Vector3 push = Separation();
                if (push.sqrMagnitude > separationDeadzone * separationDeadzone)
                    locomotion.MoveTo(transform.position + push, circleSpeed, dt);

                _holdTimer -= dt;
                if (_holdTimer <= 0f) EnterCircle();
                break;

            case State.Circle:
                if (threatened) { EnterRetreat(); break; }
                bool arrived = locomotion.MoveTo(_target + Separation(), circleSpeed, dt);
                // Quad: морда по курсу; biped (на всякий) — можно на игрока.
                if (locomotion.IsBiped) locomotion.FaceTowards(perception.PlayerPos, dt);
                if (arrived) EnterHold();
                break;

            case State.Retreat:
                bool reached = locomotion.MoveTo(_target, fleeSpeed, dt);
                if (!packScatter && !threatened) { EnterHold(); break; }
                if (reached) _target = packScatter ? ComputeScatterPoint() : ComputeRetreatPoint();
                break;
        }
    }

    // Дальняя точка разбега при срыве стаи (не просто +30% кольца).
    private Vector3 ComputeScatterPoint()
    {
        Vector3 away = perception.DirFromPlayerFlat;
        float radius = preferredDistance * Mathf.Max(retreatDistanceFactor, 1.6f);
        return perception.PlayerPos + away * radius + Separation();
    }

    // ============ Переходы ============

    private void EnterHold()
    {
        _state = State.Hold;
        ResetHoldTimer();
    }

    private void EnterCircle()
    {
        Vector3 best = BestPointInSector(out float bestScore);
        float stayScore = ScorePoint(transform.position);
        if (!InSector(transform.position)) stayScore += 1000f;   // вне своего сектора — уходим в любом случае

        // Новая точка не лучше текущей на switchMargin — остаёмся стоять.
        if (bestScore + switchMargin >= stayScore) { ResetHoldTimer(); return; }

        _state = State.Circle;
        _target = best;
    }

    private void EnterRetreat() { _state = State.Retreat; _target = ComputeRetreatPoint(); }

    private void ResetHoldTimer()
        => _holdTimer = holdInterval + Random.Range(-holdJitter, holdJitter);

    // ============ Выбор позиции ============

    // Границы своего сектора от менеджера. Нет менеджера или сектора — работаем по всему кольцу.
    private bool GetSector(out float minA, out float maxA)
    {
        var mgr = WerewolfPackManager.Instance;
        if (mgr != null && mgr.SectorFor(transform, out minA, out maxA)) return true;
        minA = 0f; maxA = 360f;
        return false;
    }

    private bool InSector(Vector3 point)
    {
        GetSector(out float minA, out float maxA);
        Vector3 d = point - perception.PlayerPos; d.y = 0f;
        float r = d.magnitude;
        if (r < minDistance || r > preferredDistance) return false;
        float a = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        return Mathf.Repeat(a - minA, 360f) <= (maxA - minA);
    }

    // Несколько случайных точек в своём секторе (полоса minDistance..preferredDistance), берём лучшую.
    private Vector3 BestPointInSector(out float bestScore)
    {
        GetSector(out float minA, out float maxA);
        Vector3 p = perception.PlayerPos;

        Vector3 best = transform.position;
        bestScore = float.MaxValue;
        for (int i = 0; i < candidateCount; i++)
        {
            float ang = Mathf.Lerp(minA, maxA, Random.value) * Mathf.Deg2Rad;
            float r = Random.Range(minDistance, preferredDistance);
            Vector3 point = p + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
            float s = ScorePoint(point);
            if (s < bestScore) { bestScore = s; best = point; }
        }
        return best;
    }

    // Штраф точки, меньше — лучше: теснота + отклонение от середины полосы +
    // отклонение от середины своего сектора + длина перехода.
    private float ScorePoint(Vector3 point)
    {
        Vector3 d = point - perception.PlayerPos; d.y = 0f;
        float r = d.magnitude;
        float mid = (minDistance + preferredDistance) * 0.5f;

        var mgr = WerewolfPackManager.Instance;
        float crowd = mgr != null ? mgr.CrowdAt(point, transform, crowdRadius) : 0f;
        float travel = Vector3.Distance(transform.position, point);

        // Отклонение по углу переводим в метры по дуге, чтобы слагаемые были в одних единицах.
        GetSector(out float minA, out float maxA);
        float centerA = (minA + maxA) * 0.5f;
        float a = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        float offArc = Mathf.Abs(Mathf.DeltaAngle(centerA, a)) * Mathf.Deg2Rad * r;

        return crowdWeight * crowd
             + bandWeight * Mathf.Abs(r - mid)
             + sectorCenterWeight * offArc
             + travelWeight * travel;
    }

    // Толчок от соседей одним проходом по стае — считает менеджер, свой OverlapSphere не нужен.
    private Vector3 Separation()
    {
        var mgr = WerewolfPackManager.Instance;
        return mgr != null ? mgr.SeparationFor(transform, separationRadius, separationStrength) : Vector3.zero;
    }

    // Отступление строго по лучу от игрока (со случайным сносом), за внешний край полосы.
    private Vector3 ComputeRetreatPoint()
    {
        Vector3 p = perception.PlayerPos;
        Vector3 away = perception.DirFromPlayerFlat;
        float j = Random.Range(-25f, 25f) * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(
            away.x * Mathf.Cos(j) - away.z * Mathf.Sin(j),
            0f,
            away.x * Mathf.Sin(j) + away.z * Mathf.Cos(j));
        return p + dir * (preferredDistance * retreatDistanceFactor);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (perception == null || !perception.HasPlayer) return;
        Vector3 p = perception.PlayerPos;

        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(p, minDistance);
        Gizmos.color = new Color(0.9f, 0.8f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(p, preferredDistance);
        Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.4f);
        Gizmos.DrawWireSphere(p, noticeRange);

        if (Application.isPlaying)
        {
            // Границы выданного сектора.
            if (GetSector(out float minA, out float maxA))
            {
                Gizmos.color = new Color(0.2f, 1f, 0.5f, 0.7f);
                Vector3 a1 = new Vector3(Mathf.Cos(minA * Mathf.Deg2Rad), 0f, Mathf.Sin(minA * Mathf.Deg2Rad));
                Vector3 a2 = new Vector3(Mathf.Cos(maxA * Mathf.Deg2Rad), 0f, Mathf.Sin(maxA * Mathf.Deg2Rad));
                Gizmos.DrawLine(p + a1 * minDistance, p + a1 * preferredDistance);
                Gizmos.DrawLine(p + a2 * minDistance, p + a2 * preferredDistance);
            }

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, _target);
            Gizmos.DrawWireCube(_target, Vector3.one * 0.6f);
        }
    }
#endif
}
