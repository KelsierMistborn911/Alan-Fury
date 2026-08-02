using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Добоевое поведение альфа-оборотня на дереве поведения (BT). Бой — отдельный скрипт.
///
/// Дерево (Selector, приоритет сверху вниз):
///   1. Сбегает   — игрок ближе spookRadius; бежит ОТ игрока веером (вдоль края, не в край),
///                  пока дистанция не станет больше fleeUntilDistance.
///   2. Преследует — игрок дальше stalkMax; подходит до stalkMax.
///   3. Прячется   — иначе: старается стоять в тумане в ЗАДНЕЙ полусфере игрока.
///        • текущий туман задний и в полосе дистанции → стоит, смотрит;
///        • игрок ближе stalkMin → отступает (от игрока + снос вбок), быстрее;
///        • туман перестал быть задним / игрок вышел из зоны → выждать reactDelay
///          (короче, если игрок близко), затем шаг к ближайшему заднему туману
///          (не сквозь игрока), по дороге пересчёт пути; дошёл → переоценка.
///
/// ВСЕ скорости держим ниже boundEnterSpeed локомоушна → баллистические скачки в добою
/// не включаются, оборотень не вылетает за карту. Точки целей подтягиваются на сетку
/// (Pathfinder.NearestWalkableWorld), поэтому к краю он не идёт.
///
/// Укрытия — объекты с тегом coverTag (FogPlacer вешает на очаги тумана).
/// IsConcealedAt() зовётся из WerewolfPerception — это «невидим в тумане».
/// ВАЖНО: пока работает сталкинг, держи WerewolfBrain выключенным (оба зовут MoveTo).
/// </summary>
[RequireComponent(typeof(WerewolfPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
public class WerewolfAlphaStalker : MonoBehaviour
{
    [Header("Ссылки")]
    public WerewolfPerception perception;
    public WerewolfLocomotion locomotion;
    [Tooltip("Сетка проходимости. ОБЯЗАТЕЛЬНА: на ней держится «не за карту».")]
    public Pathfinder pathfinder;

    [Header("Дистанции (м): spook < stalkMin < stalkMax < fleeUntil")]
    public float spookRadius = 6f;
    public float stalkMin = 10f;
    public float stalkMax = 22f;
    public float fleeUntilDistance = 50f;

    [Header("Задняя полусфера")]
    [Tooltip("Полуугол задней зоны от спины игрока (градусы). 90 = вся задняя половина; больше = только глубоко сзади.")]
    public float rearAngle = 90f;

    [Header("Укрытия (туман)")]
    public string coverTag = "FogPatch";
    [Tooltip("Радиус укрытия (м): внутри него оборотень считается спрятанным.")]
    public float coverRadius = 12f;
    [Tooltip("Запас к stalkMax при отборе укрытий по дистанции до игрока (м).")]
    public float coverSnapRadius = 14f;
    [Tooltip("Базовая задержка реакции на потерю «задней» позиции (сек). Вблизи игрока сокращается.")]
    public float reactDelay = 0.5f;
    [Tooltip("Не выбирать укрытие, путь к которому проходит ближе этого к игроку (м).")]
    public float coverCrossPlayerRadius = 4f;

    [Header("Скорости (м/с) — все НИЖЕ boundEnterSpeed, чтобы не было скачков")]
    public float stalkSpeed = 4f;     // тихий шаг между укрытиями
    public float retreatSpeed = 8f;   // отступ при сближении
    public float pursueSpeed = 7f;    // подход издалека
    public float fleeSpeed = 8f;      // сбегание

    [Header("Поиск пути")]
    public float pathRepathInterval = 0.4f;

    [Header("Давление сталкинга (задел под вой/призыв)")]
    public float pressurePerSecond = 0.15f;
    public bool autoCallWhenPressured = false;
    public System.Action onReadyToCall;
    public float StalkPressure01 => Mathf.Clamp01(_pressure);

    // ============ Рантайм ============
    private float _dt, _dist;
    private bool _seen;
    private bool _fleeing;
    private float _pressure;
    private float _reactTimer;

    private readonly List<Transform> _covers = new List<Transform>();
    private Transform _currentCover;

    private readonly List<Vector3> _path = new List<Vector3>();
    private int _pathIndex;
    private float _repathTimer;
    private Vector3 _lastGoal;
    private const float RepathGoalMoveSqr = 9f;

    private const int AwaySamples = 7;     // веер направлений отхода
    private const float AwayArcDeg = 70f;  // полусектор веера

    private BTNode _root;

    void Start()
    {
        if (perception == null) perception = GetComponent<WerewolfPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (pathfinder == null) pathfinder = FindObjectOfType<Pathfinder>();
        if (pathfinder == null)
            Debug.LogWarning("AlphaStalker: Pathfinder не назначен — защита от ухода за карту не работает!");
        RefreshCovers();
        BuildTree();
    }

    private void BuildTree()
    {
        _root = new BTSelector(
            new BTSequence(new BTCondition(ShouldFlee), new BTAction(TickFlee)),
            new BTSequence(new BTCondition(ShouldPursue), new BTAction(TickPursue)),
            new BTAction(TickHide)
        );
    }

    void Update()
    {
        _dt = Time.deltaTime;
        if (perception == null || !perception.HasPlayer) return; // поиск/обнаружение — позже
        _dist = perception.DistanceToPlayer;
        _seen = perception.IsSeenByPlayer();
        _root.Tick();
    }

    // =================== Ветки ===================

    private bool ShouldFlee()
    {
        if (_dist < spookRadius) _fleeing = true;
        else if (_dist > fleeUntilDistance) _fleeing = false;
        return _fleeing;
    }

    private NodeStatus TickFlee()
    {
        Vector3 goal = ChooseAwayGoal(stalkMax * 1.3f);
        MoveAlongPath(goal, fleeSpeed, _dt);
        locomotion.FaceTowards(goal, _dt);
        return NodeStatus.Running;
    }

    private bool ShouldPursue() => !_fleeing && _dist > stalkMax;

    private NodeStatus TickPursue()
    {
        Vector3 toMe = perception.DirFromPlayerFlat;
        Vector3 goal = ClampGoal(perception.PlayerPos + toMe * stalkMax);
        MoveAlongPath(goal, pursueSpeed, _dt);
        locomotion.FaceTowards(perception.PlayerPos, _dt);
        return NodeStatus.Running;
    }

    private NodeStatus TickHide()
    {
        Vector3 player = perception.PlayerPos;
        _pressure += pressurePerSecond * _dt;
        if (autoCallWhenPressured && _pressure >= 1f) onReadyToCall?.Invoke();

        // Слишком близко → отступаем (от игрока + снос), сбрасываем укрытие.
        if (_dist < stalkMin)
        {
            _currentCover = null; _reactTimer = 0f;
            Vector3 g = ChooseRetreatGoal();
            MoveAlongPath(g, retreatSpeed, _dt);
            locomotion.FaceTowards(player, _dt);
            return NodeStatus.Running;
        }

        bool good = _currentCover != null && InBand(_currentCover) && IsRear(_currentCover.position);

        // В хорошем заднем тумане — стоим, смотрим, таймер реакции сброшен.
        if (good)
        {
            _reactTimer = 0f;
            MoveAlongPath(ClampGoal(_currentCover.position), stalkSpeed, _dt); // дойти и стоять
            locomotion.FaceTowards(player, _dt);
            return NodeStatus.Running;
        }

        // Позиция испортилась → ждём reactDelay (короче вблизи), потом меняем туман.
        float t = Mathf.InverseLerp(stalkMax, stalkMin, _dist);     // 1 = близко
        float delay = reactDelay * Mathf.Lerp(1f, 0.3f, t);
        if (_currentCover != null && _reactTimer < delay)
        {
            _reactTimer += _dt;
            locomotion.FaceTowards(player, _dt);                    // выжидаем на месте
            return NodeStatus.Running;
        }

        // Шаг к ближайшему заднему туману.
        Transform next = PickRearCover();
        if (next != null && next != _currentCover) { _currentCover = next; ClearPath(); }

        if (_currentCover != null)
        {
            bool reached = MoveAlongPath(ClampGoal(_currentCover.position), stalkSpeed, _dt);
            if (reached) _reactTimer = 0f; // переоценим в следующем кадре
        }
        locomotion.FaceTowards(player, _dt);
        return NodeStatus.Running;
    }

    // =================== Выбор укрытия ===================

    private bool InBand(Transform c)
    {
        if (c == null) return false;
        float toP = FlatDist(perception.PlayerPos, c.position);
        return toP >= stalkMin && toP <= stalkMax + coverSnapRadius;
    }

    // Укрытие в задней полусфере игрока?
    private bool IsRear(Vector3 cover)
    {
        Vector3 d = cover - perception.PlayerPos; d.y = 0f;
        if (d.sqrMagnitude < 0.01f) return true;
        d.Normalize();
        float cosThr = Mathf.Cos(rearAngle * Mathf.Deg2Rad);
        return Vector3.Dot(perception.PlayerForwardFlat, d) < cosThr;
    }

    // Ближайшее к себе укрытие: задний туман, в полосе, путь не сквозь игрока.
    private Transform PickRearCover()
    {
        Transform best = null;
        float bestSelf = float.MaxValue;
        for (int i = 0; i < _covers.Count; i++)
        {
            Transform c = _covers[i];
            if (c == null || !InBand(c) || !IsRear(c.position)) continue;
            if (CrossesPlayer(c.position)) continue;

            float s = FlatDist(transform.position, c.position);
            if (s < bestSelf) { bestSelf = s; best = c; }
        }
        return best;
    }

    private bool CrossesPlayer(Vector3 target)
    {
        Vector2 a = new Vector2(transform.position.x, transform.position.z);
        Vector2 b = new Vector2(target.x, target.z);
        Vector2 p = new Vector2(perception.PlayerPos.x, perception.PlayerPos.z);
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float u = len2 > 1e-4f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
        Vector2 closest = a + ab * u;
        return (p - closest).sqrMagnitude < coverCrossPlayerRadius * coverCrossPlayerRadius;
    }

    // Отступ: точка на кольце stalkMax ВОКРУГ ИГРОКА (обновляется от его позиции),
    // веером выбираем ту, что меньше всего упирается в край.
    private Vector3 ChooseRetreatGoal()
    {
        Vector3 player = perception.PlayerPos;
        Vector3 away = perception.DirFromPlayerFlat; // от игрока к оборотню

        Vector3 best = ClampGoal(player + away * stalkMax);
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < AwaySamples; i++)
        {
            float angle = Mathf.Lerp(-AwayArcDeg, AwayArcDeg, i / (float)(AwaySamples - 1));
            Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * away;
            Vector3 raw = player + dir * stalkMax;          // кольцо вокруг игрока
            Vector3 clamped = ClampGoal(raw);

            float pull = FlatDist(raw, clamped);            // утянуло к проходимой клетке (край = много)
            float score = -pull;
            if (score > bestScore) { bestScore = score; best = clamped; }
        }
        return best;
    }

    // Точка отхода от игрока: веер направлений, берём ту, что меньше всего упирается в край.
    private Vector3 ChooseAwayGoal(float range)
    {
        Vector3 away = perception.DirFromPlayerFlat; // от игрока к оборотню
        Vector3 player = perception.PlayerPos;

        Vector3 bestGoal = ClampGoal(transform.position + away * range);
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < AwaySamples; i++)
        {
            float angle = Mathf.Lerp(-AwayArcDeg, AwayArcDeg, i / (float)(AwaySamples - 1));
            Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * away;
            Vector3 raw = transform.position + dir * range;
            Vector3 clamped = ClampGoal(raw);

            float pull = FlatDist(raw, clamped);              // насколько цель «утянуло» (край = много)
            float gain = FlatDist(clamped, player);           // как далеко от игрока
            float score = gain - pull * 2f;                   // от игрока, но не в край
            if (score > bestScore) { bestScore = score; bestGoal = clamped; }
        }
        return bestGoal;
    }

    // =================== Укрытия: кэш и API ===================

    public void RefreshCovers()
    {
        _covers.Clear();
        if (string.IsNullOrEmpty(coverTag)) return;
        GameObject[] found;
        try { found = GameObject.FindGameObjectsWithTag(coverTag); }
        catch (UnityException)
        {
            Debug.LogWarning($"AlphaStalker: тег '{coverTag}' не создан в Project Settings → Tags.");
            return;
        }
        foreach (var go in found) _covers.Add(go.transform);
    }

    /// <summary>Скрыт ли hider от seeker'а укрытием. Зовётся из WerewolfPerception.</summary>
    public bool IsConcealedAt(Vector3 hider, Vector3 seeker)
    {
        float r2 = coverRadius * coverRadius;
        for (int i = 0; i < _covers.Count; i++)
        {
            if (_covers[i] == null) continue;
            Vector3 c = _covers[i].position;
            if (FlatSqr(hider, c) <= r2 && FlatSqr(seeker, c) > r2) return true;
        }
        return false;
    }

    // =================== Ведение по пути ===================

    private bool MoveAlongPath(Vector3 goal, float speed, float dt)
    {
        if (pathfinder == null || !pathfinder.IsReady)
            return locomotion.MoveTo(goal, speed, dt);

        _repathTimer -= dt;
        bool needRepath = _path.Count == 0 || _pathIndex >= _path.Count
                       || _repathTimer <= 0f || FlatSqr(goal, _lastGoal) > RepathGoalMoveSqr;
        if (needRepath)
        {
            _repathTimer = pathRepathInterval;
            _lastGoal = goal;
            if (pathfinder.TryFindPath(transform.position, goal, _path)) _pathIndex = 0;
            else _path.Clear();
        }

        if (_path.Count == 0) return FlatDistSelf(goal) <= 2f;

        Vector3 wp = _path[_pathIndex];
        if (locomotion.MoveTo(wp, speed, dt))
        {
            _pathIndex++;
            if (_pathIndex >= _path.Count) return true;
        }
        return false;
    }

    private Vector3 ClampGoal(Vector3 goal)
    {
        if (pathfinder == null || !pathfinder.IsReady) return goal;
        return pathfinder.NearestWalkableWorld(goal, out _);
    }

    private void ClearPath() { _path.Clear(); _pathIndex = 0; _repathTimer = 0f; }

    // =================== Утилиты ===================

    private static float FlatSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
    private static float FlatDist(Vector3 a, Vector3 b) => Mathf.Sqrt(FlatSqr(a, b));
    private float FlatDistSelf(Vector3 p) { Vector3 d = p - transform.position; d.y = 0f; return d.magnitude; }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (perception == null || !perception.HasPlayer) return;
        Vector3 p = perception.PlayerPos;
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.7f); Gizmos.DrawWireSphere(p, spookRadius);
        Gizmos.color = new Color(0.9f, 0.5f, 0.2f, 0.6f); Gizmos.DrawWireSphere(p, stalkMin);
        Gizmos.color = new Color(0.9f, 0.8f, 0.2f, 0.6f); Gizmos.DrawWireSphere(p, stalkMax);

        // Спина игрока
        Gizmos.color = Color.green;
        Gizmos.DrawLine(p, p - perception.PlayerForwardFlat * stalkMax);

        Gizmos.color = new Color(0.7f, 0.7f, 0.9f, 0.35f);
        for (int i = 0; i < _covers.Count; i++)
            if (_covers[i] != null) Gizmos.DrawWireSphere(_covers[i].position, coverRadius);

        if (Application.isPlaying && _currentCover != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, _currentCover.position);
            Gizmos.DrawWireCube(_currentCover.position, Vector3.one * 0.6f);
        }
    }
#endif
}
