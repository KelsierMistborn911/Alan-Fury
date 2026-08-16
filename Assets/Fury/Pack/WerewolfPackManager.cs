using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Авторитет стаи. Один на уровень (WerewolfPackManager.Instance).
///
/// Обязанности (только это, без заделов на будущее):
///   • Спавнит волков — метод-кнопка SpawnPack() (для теста; вой альфы прикрутим позже).
///   • Держит игрока и список волков (волки регистрируются сами в Awake через Register).
///   • Раздаёт СЛОТЫ атаки по требованиям (здоровье, страх): держатель остаётся в слоте,
///     пока проходит требования — периодической пересборки ролей нет. Ранен или напуган →
///     слот уходит целому. Никто не годится → слот остаётся ПУСТЫМ, стая давит меньшим числом.
///   • Слот 1 получает признак avoidFront ("заходи не спереди").
///   • Держит ОДИН токен фронт-атаки: фронтовики бьют по очереди, чтобы не попасть друг в друга.
///   • Считает расталкивание для всей стаи одним проходом (SeparationFor) — волкам не нужен
///     свой Physics.OverlapSphere каждый кадр.
///
/// Чего менеджер НЕ делает (важно для будущей сети):
///   • Не двигает волков и не лезет в их компоненты — только выдаёт роль/признак/токен.
///   • Точку подхода волк считает сам в WerewolfAttackBrain.
///
/// Мозг волка обязан предоставить интерфейс IPackAgent (см. ниже).
/// </summary>
public class WerewolfPackManager : MonoBehaviour
{
    public static WerewolfPackManager Instance { get; private set; }

    public enum PackRole { Surround, Attack }

    /// <summary>Что менеджер требует от мозга волка. Реализует WerewolfAttackBrain.</summary>
    public interface IPackAgent
    {
        Transform Transform { get; }
        bool IsAlive { get; }
        /// <summary>Здоровье 0..1 — по нему слот атаки уходит от раненого к целому.</summary>
        float HealthPercent { get; }
        /// <summary>Страх 0..1 — напуганный не годится в атакующие.</summary>
        float Fear01 { get; }
        void SetRole(PackRole role, bool avoidFront);
        /// <summary>Разрешение бить в этот момент (токен фронта). Второй (сзади) всегда true.</summary>
        void SetAttackToken(bool hasToken);
        /// <summary>Сбить или поднять кураж извне (рана своего рядом).</summary>
        void AddAggression(float delta);
        /// <summary>Стая сорвалась: страх стаи упёрся в потолок — волк получает +panicFearTiers ступеней страха.</summary>
        void OnPackPanic();
    }

    [Header("Игрок")]
    [Tooltip("Если пусто — найдётся по тегу в Awake.")]
    public Transform player;
    public string playerTag = "Player";

    [Header("Спавн")]
    [Tooltip("Префаб волка (на нём WerewolfAttackBrain + WerewolfSurroundBrain + WerewolfCombat + локомоция/восприятие).")]
    public GameObject wolfPrefab;
    [Tooltip("Сколько волков рождает один SpawnPack().")]
    public int spawnCount = 5;
    [Tooltip("Радиус кольца спавна вокруг точки спавна (м).")]
    public float spawnRadius = 12f;
    [Tooltip("Точка, вокруг которой спавнятся волки. Пусто — вокруг самого менеджера.")]
    public Transform spawnCenter;

    [Header("Вой (авто-спавн)")]
    [Tooltip("Если true — через howlDelay после старта автоматически вызывается SpawnPack(). Обычно false: призыв идёт с объекта WolfSummonPoint.")]
    public bool autoHowlOnStart = false;
    [Tooltip("Через сколько секунд после старта альфа 'воет' и поднимает стаю (разово).")]
    public float howlDelay = 2f;
    [Tooltip("Альфа в сцене. Если задана — волки выходят веером с дальней от игрока стороны альфы.")]
    public Transform alphaTransform;
    [Tooltip("Полуугол веера выхода из-за альфы (град). 40 = узкий веер 'выбегают из-за неё'.")]
    public float spawnArcHalfAngle = 40f;

    [Header("Слоты атаки")]
    [Tooltip("Сколько волков одновременно в роли Attack. Остальные — Surround.")]
    public int maxAttackers = 3;
    [Tooltip("Как часто проверять требования слотов (сек). Слот меняется только если держатель перестал подходить или освободился — периодической пересортировки нет.")]
    public float slotCheckInterval = 1f;

    [Header("Требования к слоту атаки")]
    [Tooltip("Ниже этого здоровья (0..1) волк отдаёт слот атаки: раненый уходит в окружение, слот забирает целый.")]
    [Range(0f, 1f)] public float attackMinHealth = 0.4f;
    [Tooltip("Выше этого страха (0..1) волк не годится в атакующие. 0.5 = ступень «напуган».")]
    [Range(0f, 1f)] public float attackMaxFear = 0.5f;
    [Tooltip("Насколько ближе к цели должен быть претендент, чтобы отобрать ЗАНЯТЫЙ слот (м). Мешает слоту прыгать между равными.")]
    public float slotStealMargin = 2f;
    [Tooltip("Сколько секунд слот залочен после смены держателя.")]
    public float slotLockTime = 3f;

    [Header("Кольцо окружения")]
    [Tooltip("Доля окружающих, встающих в переднюю дугу со стороны альфы. Остальные — фланги и спина.")]
    [Range(0f, 1f)] public float frontShare = 0.6f;
    [Tooltip("Полуугол передней дуги от оси игрок→альфа (град). 60 = дуга шириной 120.")]
    public float frontArcHalfAngle = 60f;
    [Tooltip("Как часто пара волков хаотично меняется местами между фронтом и флангами (сек).")]
    public float regroupInterval = 6f;
    [Tooltip("Разброс к интервалу обмена (±сек).")]
    public float regroupJitter = 3f;

    [Header("Лог стаи")]
    [Tooltip("Писать в консоль выдачу слотов и причины отказа.")]
    public bool logSlots = true;

    [Header("Токен фронт-атаки")]
    [Tooltip("Как долго один фронтовик держит право удара, прежде чем токен уйдёт другому (сек).")]
    public float frontTokenHold = 1.25f;

    [Header("Прыжки стаи")]
    [Tooltip("Минимальный интервал между атакующими прыжками всей стаи (сек).")]
    public float packJumpInterval = 1.2f;

    [Header("Страх стаи")]
    [Tooltip("Потолок страха стаи. Урон по любому волку добавляет столько же страха; сам не затухает.")]
    public float maxPackFear = 500f;
    [Tooltip("Сколько ступеней страха получает каждый волк при срыве стаи (ступень = 25).")]
    public int panicFearTiers = 2;
    [Tooltip("Сколько страха стаи остаётся после срыва. 0.5 = половина, второй срыв наступит вдвое быстрее.")]
    [Range(0f, 1f)] public float packFearAfterPanic = 0.5f;
    [Tooltip("Спад страха стаи вне срыва (ед/сек). Без спада слоты не вернутся после паники.")]
    public float packFearDecayPerSecond = 8f;
    [Tooltip("Ниже этой доли PackFearPercent стая снова может выдавать слоты атаки.")]
    [Range(0f, 1f)] public float packRecoverFearPercent = 0.35f;
    [Tooltip("Минимальная длительность разбега после срыва (сек).")]
    public float packScatterMinDuration = 4f;

    [Header("Рана рядом сбивает кураж")]
    [Tooltip("Радиус, в котором соседи видят рану и теряют агрессию (м).")]
    public float woundAggroRadius = 8f;
    [Tooltip("Сколько агрессии снимается за единицу урона. Делится поровну между соседями в радиусе.")]
    public float woundAggroPerDamage = 1f;

    private float _nextJumpAllowed;
    private float _packFear;
    private bool _packScattering;
    private float _scatterUntil;

    /// <summary>Текущий страх стаи (0..maxPackFear).</summary>
    public float PackFear => _packFear;
    public float PackFearPercent => maxPackFear > 0f ? _packFear / maxPackFear : 0f;
    /// <summary>Стая в массовом разбеге — слоты атаки не выдаются, Surround бежит дальше.</summary>
    public bool IsPackScattering => _packScattering;

    /// <summary>Рана у волка: страх стаи += урон. Потолок → срыв всей стаи.</summary>
    public void ReportWound(float damage, Vector3 at, Transform wounded)
    {
        SpreadWoundAggro(damage, at, wounded);

        _packFear = Mathf.Min(maxPackFear, _packFear + damage);
        if (_packFear < maxPackFear) return;

        BeginPackScatter();
    }

    private void BeginPackScatter()
    {
        // Все атакующие → Surround, слоты пустые.
        for (int i = 0; i < _attackSlots.Count; i++)
        {
            if (_attackSlots[i] == null) continue;
            Demote(_attackSlots[i]);
            _attackSlots[i] = null;
            _slotLockedUntil[i] = 0f;
        }

        int panicked = 0;
        for (int i = 0; i < _wolves.Count; i++)
            if (_wolves[i] != null && _wolves[i].IsAlive)
            {
                _wolves[i].OnPackPanic();
                panicked++;
            }

        _packFear = maxPackFear * packFearAfterPanic;
        _packScattering = true;
        _scatterUntil = Time.time + packScatterMinDuration;
        Debug.Log($"[Стая] СРЫВ: {panicked} волков, разбег до {packScatterMinDuration:0.#}с+. Страх стаи {_packFear:0}/{maxPackFear:0}.");
    }

    private void TickPackFear(float dt)
    {
        if (_packFear > 0f && packFearDecayPerSecond > 0f)
            _packFear = Mathf.Max(0f, _packFear - packFearDecayPerSecond * dt);

        if (!_packScattering) return;
        if (Time.time < _scatterUntil) return;
        if (PackFearPercent > packRecoverFearPercent) return;

        _packScattering = false;
        Debug.Log($"[Стая] Сбор: страх стаи {PackFearPercent:0%} ≤ {packRecoverFearPercent:0%}, слоты снова можно выдавать.");
    }

    /// <summary>Рана своего рядом сбивает кураж: урон делится поровну между волками
    /// в радиусе и уходит им в минус к агрессии. Раненый не считается — у него свой страх.</summary>
    private void SpreadWoundAggro(float damage, Vector3 at, Transform wounded)
    {
        if (damage <= 0f || woundAggroPerDamage <= 0f) return;

        _woundNear.Clear();
        float r2 = woundAggroRadius * woundAggroRadius;
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (w == null || !w.IsAlive || w.Transform == null) continue;
            if (w.Transform == wounded) continue;
            if ((w.Transform.position - at).sqrMagnitude > r2) continue;
            _woundNear.Add(w);
        }
        if (_woundNear.Count == 0) return;

        float share = damage * woundAggroPerDamage / _woundNear.Count;
        for (int i = 0; i < _woundNear.Count; i++) _woundNear[i].AddAggression(-share);
    }

    /// <summary>Буфер соседей для SpreadWoundAggro — чтобы не плодить мусор на каждой ране.</summary>
    private readonly List<IPackAgent> _woundNear = new List<IPackAgent>();

    // ---- состояние ----
    private readonly List<IPackAgent> _wolves = new List<IPackAgent>();
    private float _slotTimer;

    // Слоты атаки: держатель + время, до которого слот залочен от перехвата.
    // null в ячейке = слот пуст (никто не прошёл требования — стая давит меньшим числом).
    private readonly List<IPackAgent> _attackSlots = new List<IPackAgent>();
    private readonly List<float> _slotLockedUntil = new List<float>();

    // Секторы окружения: волк → границы угла в градусах (мировой Atan2(z, x), как в SurroundBrain).
    private readonly Dictionary<Transform, Vector2> _sectors = new Dictionary<Transform, Vector2>();
    private readonly List<IPackAgent> _surrounders = new List<IPackAgent>();
    private readonly List<float> _sectorKeys = new List<float>();
    // Порядок = номер сектора. Держится постоянным между пересчётами, иначе обмен групп
    // откатывался бы следующей же сортировкой по углу.
    private readonly List<IPackAgent> _order = new List<IPackAgent>();
    private float _regroupTimer;

    // Токен фронта: кто из фронтовиков сейчас бьёт.
    private IPackAgent _frontTokenHolder;
    private float _frontTokenUntil;

    // Вой: одноразовый авто-спавн через howlDelay после старта.
    private float _howlTimer;
    private bool _howled;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("WerewolfPackManager: на сцене уже есть Instance — этот отключаю.");
            enabled = false;
            return;
        }
        Instance = this;

        if (player == null && !string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindGameObjectWithTag(playerTag);
            if (go != null) player = go.transform;
        }

        _howlTimer = howlDelay; // отсчёт до воя стартует со сцены
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ===================== Регистрация (зовёт волк) =====================

    public void Register(IPackAgent wolf)
    {
        if (wolf != null && !_wolves.Contains(wolf)) _wolves.Add(wolf);
    }

    public void Unregister(IPackAgent wolf)
    {
        _wolves.Remove(wolf);
        _order.Remove(wolf);
        if (_frontTokenHolder == wolf) _frontTokenHolder = null;
        int slot = _attackSlots.IndexOf(wolf);
        if (slot >= 0) _attackSlots[slot] = null;
    }

    // ===================== Спавн (кнопка теста) =====================

    /// <summary>Родить стаю вокруг точки спавна и раздать роли. Для теста — зови из кнопки/клавиши.</summary>
    [ContextMenu("Spawn Pack")]
    public void SpawnPack()
    {
        if (wolfPrefab == null)
        {
            Debug.LogWarning("WerewolfPackManager: не назначен wolfPrefab — спавнить нечего.");
            return;
        }

        bool useAlpha = alphaTransform != null;

        // Центр спавна: у альфы (если есть) или как раньше — spawnCenter/менеджер.
        Vector3 center = useAlpha
            ? alphaTransform.position
            : (spawnCenter != null ? spawnCenter.position : transform.position);

        // Базовое направление веера: от игрока к альфе (дальняя от игрока сторона).
        Vector3 baseDir = Vector3.forward;
        if (useAlpha)
        {
            Vector3 fromPlayer = player != null ? (center - player.position) : alphaTransform.forward;
            fromPlayer.y = 0f;
            baseDir = fromPlayer.sqrMagnitude > 1e-4f ? fromPlayer.normalized : Vector3.forward;
        }

        for (int i = 0; i < spawnCount; i++)
        {
            Vector3 dir;
            if (useAlpha)
            {
                // Веер ±spawnArcHalfAngle вокруг baseDir — волки выходят из-за альфы.
                float t = spawnCount > 1 ? i / (float)(spawnCount - 1) : 0.5f;
                float deg = Mathf.Lerp(-spawnArcHalfAngle, spawnArcHalfAngle, t);
                dir = Quaternion.AngleAxis(deg, Vector3.up) * baseDir;
            }
            else
            {
                // Прежнее поведение: полное кольцо вокруг центра.
                float ang = (360f / Mathf.Max(1, spawnCount)) * i * Mathf.Deg2Rad;
                dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
            }

            Instantiate(wolfPrefab, center + dir * spawnRadius, Quaternion.identity);
            // Волк сам зарегистрируется в Awake.
        }

        // Сразу раздать слоты, не дожидаясь таймера.
        UpdateSlots();
        Log($"Стая поднята: {_wolves.Count} волков, слотов атаки {maxAttackers}.");
    }

    // ===================== Роли + токен =====================

    void Update()
    {
        // Вой: разово через howlDelay (только если autoHowlOnStart). Иначе призыв с WolfSummonPoint.
        if (autoHowlOnStart && !_howled)
        {
            _howlTimer -= Time.deltaTime;
            if (_howlTimer <= 0f)
            {
                _howled = true;
                SpawnPack();
            }
        }

        TickPackFear(Time.deltaTime);

        _slotTimer -= Time.deltaTime;
        if (_slotTimer <= 0f)
        {
            _slotTimer = slotCheckInterval;
            UpdateSlots();
        }

        _regroupTimer -= Time.deltaTime;
        if (_regroupTimer <= 0f)
        {
            _regroupTimer = regroupInterval + Random.Range(-regroupJitter, regroupJitter);
            if (!_packScattering) SwapGroups();
        }

        UpdateFrontToken();
    }

    /// <summary>Годится ли волк в атакующие. Причина отказа возвращается для лога.</summary>
    private bool Qualifies(IPackAgent w, out string reason)
    {
        reason = null;
        if (w == null || !w.IsAlive) { reason = "мёртв"; return false; }
        if (_packScattering) { reason = "срыв стаи"; return false; }
        if (w.HealthPercent < attackMinHealth) { reason = "ранен"; return false; }
        if (w.Fear01 > attackMaxFear) { reason = "страх"; return false; }
        return true;
    }

    private float DistToPlayerSqr(IPackAgent w) =>
        (w.Transform.position - player.position).sqrMagnitude;

    /// <summary>
    /// Проверка слотов атаки. В отличие от старых ролей НЕ пересобирает всё заново:
    /// держатель остаётся, пока проходит требования. Слот освобождается, только если
    /// волк ранен / напуган / погиб, и уходит лучшему из свободных. Никто не подходит —
    /// слот остаётся ПУСТЫМ, и стая давит меньшим числом (позже сюда встанет вой альфы).
    /// </summary>
    private void UpdateSlots()
    {
        PruneDead();
        if (player == null) return;

        // Подгоняем число слотов под maxAttackers (меняется в инспекторе на лету).
        while (_attackSlots.Count < maxAttackers) { _attackSlots.Add(null); _slotLockedUntil.Add(0f); }
        while (_attackSlots.Count > maxAttackers)
        {
            int last = _attackSlots.Count - 1;
            if (_attackSlots[last] != null) Demote(_attackSlots[last]);
            _attackSlots.RemoveAt(last); _slotLockedUntil.RemoveAt(last);
        }

        // 1. Держатели, переставшие подходить, освобождают слот.
        for (int i = 0; i < _attackSlots.Count; i++)
        {
            var holder = _attackSlots[i];
            if (holder == null) continue;
            if (!Qualifies(holder, out string why))
            {
                Log($"Слот атаки {i}: {Name(holder)} отдал слот ({why})");
                Demote(holder);
                _attackSlots[i] = null;
            }
        }

        // 2. Пустые слоты заполняем лучшим из свободных (ближайший к цели).
        for (int i = 0; i < _attackSlots.Count; i++)
        {
            if (_attackSlots[i] != null) continue;
            var cand = PickBestFree();
            if (cand == null) continue;      // никто не годится — слот остаётся пустым
            Assign(cand, i);
        }

        // 3. Перехват: свободный волк заметно ближе держателя — забирает слот.
        // Запас slotStealMargin и лок slotLockTime не дают слоту прыгать между равными.
        for (int i = 0; i < _attackSlots.Count; i++)
        {
            var holder = _attackSlots[i];
            if (holder == null || Time.time < _slotLockedUntil[i]) continue;

            var cand = PickBestFree();
            if (cand == null) continue;

            float dHolder = Mathf.Sqrt(DistToPlayerSqr(holder));
            float dCand = Mathf.Sqrt(DistToPlayerSqr(cand));
            if (dCand + slotStealMargin >= dHolder) continue;

            Log($"Слот атаки {i}: {Name(holder)} → {Name(cand)} (ближе на {dHolder - dCand:0.0} м)");
            Demote(holder);
            Assign(cand, i);
        }

        // Все, кто не в слотах, — в окружение.
        for (int i = 0; i < _wolves.Count; i++)
            if (!_attackSlots.Contains(_wolves[i]))
                _wolves[i].SetRole(PackRole.Surround, false);

        AssignSectors();
    }

    // ===================== Секторы окружения =====================

    /// <summary>
    /// Режет окружающих (тех, кто не в слотах атаки) на две группы: frontShare встают в переднюю
    /// дугу со стороны альфы, остальные — на оставшееся кольцо (фланги и спина). Каждому свой
    /// непересекающийся сектор. Номер сектора берётся из постоянного порядка _order, поэтому
    /// хаотичный обмен (SwapGroups) держится, а не откатывается следующим пересчётом.
    /// Ось кольца — от игрока к альфе; нет альфы — вперёд по взгляду игрока.
    /// </summary>
    private void AssignSectors()
    {
        _sectors.Clear();
        if (player == null) return;

        // 1. Кто сейчас в окружении.
        _surrounders.Clear();
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (w == null || !w.IsAlive || _attackSlots.Contains(w)) continue;
            _surrounders.Add(w);
        }
        int n = _surrounders.Count;
        if (n == 0) { _order.Clear(); return; }

        // 2. Состав сменился (кто-то умер, ушёл в атаку или вернулся) — раскладываем заново по углу.
        if (OrderChanged()) RebuildOrder();

        // 3. Геометрия секторов от текущей оси. Ось едет за альфой и игроком — строй поворачивается.
        Vector3 axis = alphaTransform != null ? alphaTransform.position - player.position : player.forward;
        axis.y = 0f;
        if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward;
        float zero = Mathf.Atan2(axis.z, axis.x) * Mathf.Rad2Deg - frontArcHalfAngle; // левый край передней дуги

        int frontCount = FrontCount(n);
        int restCount = n - frontCount;
        float frontSpan = frontArcHalfAngle * 2f;
        float restSpan = 360f - frontSpan;

        for (int i = 0; i < frontCount; i++)
        {
            float w = frontSpan / frontCount;
            float lo = zero + w * i;
            _sectors[_order[i].Transform] = new Vector2(lo, lo + w);
        }
        for (int i = 0; i < restCount; i++)
        {
            float w = restSpan / restCount;
            float lo = zero + frontSpan + w * i;
            _sectors[_order[frontCount + i].Transform] = new Vector2(lo, lo + w);
        }
    }

    private int FrontCount(int n)
    {
        int f = Mathf.Clamp(Mathf.RoundToInt(n * frontShare), 0, n);
        if (f == 0 && frontShare > 0f) f = 1;   // один окружающий — всё равно фронтовик
        return f;
    }

    private bool OrderChanged()
    {
        if (_order.Count != _surrounders.Count) return true;
        for (int i = 0; i < _order.Count; i++)
            if (_order[i] == null || !_surrounders.Contains(_order[i])) return true;
        return false;
    }

    // Раскладка по текущему углу вокруг игрока: сектор достаётся ближайший, стая не перебегает кольцо.
    private void RebuildOrder()
    {
        _sectorKeys.Clear();
        _order.Clear();
        for (int i = 0; i < _surrounders.Count; i++)
        {
            Vector3 d = _surrounders[i].Transform.position - player.position; d.y = 0f;
            float a = d.sqrMagnitude > 1e-4f ? Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg : 0f;
            _order.Add(_surrounders[i]);
            _sectorKeys.Add(Mathf.Repeat(a, 360f));
        }

        // Сортировка вставками: стая мелкая, обходимся без компаратора и его аллокации.
        for (int i = 1; i < _order.Count; i++)
        {
            float k = _sectorKeys[i];
            var w = _order[i];
            int j = i - 1;
            while (j >= 0 && _sectorKeys[j] > k) { _sectorKeys[j + 1] = _sectorKeys[j]; _order[j + 1] = _order[j]; j--; }
            _sectorKeys[j + 1] = k; _order[j + 1] = w;
        }
    }

    /// <summary>Хаотичный обмен: один фронтовик и один с фланга/спины меняются секторами.</summary>
    private void SwapGroups()
    {
        for (int i = _order.Count - 1; i >= 0; i--)
            if (_order[i] == null || !_order[i].IsAlive) _order.RemoveAt(i);

        int n = _order.Count;
        int front = FrontCount(n);
        if (front <= 0 || front >= n) return;   // меняться не с кем

        int a = Random.Range(0, front);
        int b = Random.Range(front, n);
        var tmp = _order[a]; _order[a] = _order[b]; _order[b] = tmp;
        Log($"Обмен в окружении: {Name(_order[a])} ушёл во фронт, {Name(_order[b])} на фланг.");
    }

    /// <summary>Границы сектора окружения (град). false — сектора нет: волк в атаке или стая ещё не роздана.</summary>
    public bool SectorFor(Transform self, out float minAngle, out float maxAngle)
    {
        if (self != null && _sectors.TryGetValue(self, out Vector2 s))
        {
            minAngle = s.x; maxAngle = s.y;
            return true;
        }
        minAngle = 0f; maxAngle = 360f;
        return false;
    }

    /// <summary>Теснота в точке: сумма перекрытий с волками ближе radius. Для выбора позиции в секторе.</summary>
    public float CrowdAt(Vector3 point, Transform ignore, float radius)
    {
        float sum = 0f;
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (w == null || w.Transform == null || w.Transform == ignore) continue;
            Vector3 d = point - w.Transform.position; d.y = 0f;
            float dist = d.magnitude;
            if (dist < radius) sum += radius - dist;
        }
        return sum;
    }

    /// <summary>Лучший свободный кандидат: проходит требования, не в слоте, ближайший к цели.</summary>
    private IPackAgent PickBestFree()
    {
        IPackAgent best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (_attackSlots.Contains(w)) continue;
            if (!Qualifies(w, out _)) continue;
            float d = DistToPlayerSqr(w);
            if (d < bestDist) { bestDist = d; best = w; }
        }
        return best;
    }

    private void Assign(IPackAgent w, int slot)
    {
        _attackSlots[slot] = w;
        _slotLockedUntil[slot] = Time.time + slotLockTime;
        // Слот 1 (второй атакующий) заходит не спереди — как было в старых ролях.
        w.SetRole(PackRole.Attack, slot == 1);
        OnSlotGranted?.Invoke(w, slot);
    }

    private void Demote(IPackAgent w)
    {
        if (w == null || !w.IsAlive) return;
        w.SetRole(PackRole.Surround, false);
        if (_frontTokenHolder == w) { w.SetAttackToken(false); _frontTokenHolder = null; }
        OnSlotRevoked?.Invoke(w);
    }

    /// <summary>Волк сам сдаёт слот (Fleeish / не бьёт) → Surround, слот свободен для замены.</summary>
    public void YieldAttackSlot(IPackAgent w)
    {
        if (w == null) return;
        for (int i = 0; i < _attackSlots.Count; i++)
        {
            if (_attackSlots[i] != w) continue;
            _attackSlots[i] = null;
            _slotLockedUntil[i] = 0f;
            Demote(w);
            Log($"Слот {i}: {w.Transform.name} сдал атаку (Fleeish/страх).");
            return;
        }
        // Не в слотах, но роль Attack — всё равно в Surround.
        Demote(w);
    }

    /// <summary>Волк занял слот атаки. Под будущий рык/вой и подсветку в редакторе.</summary>
    public System.Action<IPackAgent, int> OnSlotGranted;
    /// <summary>Волк потерял слот атаки.</summary>
    public System.Action<IPackAgent> OnSlotRevoked;

    /// <summary>
    /// Вектор расталкивания от соседей для одного волка. Считается перебором списка стаи,
    /// а не Physics.OverlapSphere у каждого волка каждый кадр (тот создавал новый массив
    /// на каждый вызов — 10 волков × 60 fps = 600 аллокаций в секунду на сборку мусора).
    /// </summary>
    public Vector3 SeparationFor(Transform self, float radius, float strength)
    {
        Vector3 push = Vector3.zero;
        Vector3 pos = self.position;
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (w == null || w.Transform == self) continue;
            Vector3 away = pos - w.Transform.position; away.y = 0f;
            float dist = away.magnitude;
            if (dist < 0.05f || dist >= radius) continue;
            push += away / dist * (radius - dist);
        }
        return push * strength;
    }

    private void Log(string msg) { if (logSlots) Debug.Log($"[Стая] {msg}"); }
    private static string Name(IPackAgent w) => w?.Transform != null ? w.Transform.name : "???";

    /// <summary>Сводка по стае: сколько атакует, сколько окружает, кто почему отсеян.</summary>
    [ContextMenu("Лог: состояние стаи")]
    public void LogPackState()
    {
        PruneDead();
        int filled = 0;
        for (int i = 0; i < _attackSlots.Count; i++) if (_attackSlots[i] != null) filled++;

        int wounded = 0, scared = 0;
        for (int i = 0; i < _wolves.Count; i++)
        {
            if (_attackSlots.Contains(_wolves[i])) continue;
            if (!Qualifies(_wolves[i], out string why)) { if (why == "ранен") wounded++; else if (why == "страх") scared++; }
        }

        string refused = (wounded + scared) > 0 ? $", отказ: {wounded} ранены, {scared} страх" : "";
        Debug.Log($"[Стая] Атака: {filled}/{_wolves.Count} (слотов {_attackSlots.Count}{refused}). " +
                  $"Страх стаи {_packFear:0}/{maxPackFear:0}.");
    }

    // Токен фронта: право удара по очереди, чтобы фронтовики не били друг через друга.
    // Задний (слот 1, avoidFront) токен игнорирует — ему разрешено всегда.
    private void UpdateFrontToken()
    {
        PruneDead();

        if (_frontTokenHolder == null || Time.time >= _frontTokenUntil || !_frontTokenHolder.IsAlive)
        {
            IPackAgent next = PickNextFrontHolder();
            // Снять токен с прошлого, выдать новому.
            if (_frontTokenHolder != null && _frontTokenHolder.IsAlive)
                _frontTokenHolder.SetAttackToken(false);

            _frontTokenHolder = next;
            _frontTokenUntil = Time.time + frontTokenHold;

            if (_frontTokenHolder != null)
                _frontTokenHolder.SetAttackToken(true);
        }
    }

    // Следующий занятый слот после текущего держателя (карусель по слотам, пустые пропускаем).
    private IPackAgent PickNextFrontHolder()
    {
        int n = _attackSlots.Count;
        if (n == 0) return null;

        int startIdx = _frontTokenHolder != null ? _attackSlots.IndexOf(_frontTokenHolder) : -1;
        for (int step = 1; step <= n; step++)
        {
            int idx = ((startIdx + step) % n + n) % n;
            var cand = _attackSlots[idx];
            if (cand != null && cand.IsAlive) return cand;
        }
        return null;
    }

    private void PruneDead()
    {
        for (int i = _wolves.Count - 1; i >= 0; i--)
            if (_wolves[i] == null || !_wolves[i].IsAlive)
            {
                var dead = _wolves[i];
                _order.Remove(dead);
                if (_frontTokenHolder == dead) _frontTokenHolder = null;
                // Слот освобождается сразу: следующая проверка отдаст его живому.
                int slot = _attackSlots.IndexOf(dead);
                if (slot >= 0) { _attackSlots[slot] = null; Log($"Слот атаки {slot} освободился (волк убит)"); }
                _wolves.RemoveAt(i);
            }
    }

    /// <summary>Разрешить атакующий прыжок: не чаще packJumpInterval на стаю,
    /// и только волку с максимальным углом от взгляда игрока (лучшая позиция).</summary>
    public bool RequestJump(WerewolfPerception asker)
    {
        if (Time.time < _nextJumpAllowed) return false;
        if (asker == null) { _nextJumpAllowed = Time.time + packJumpInterval; return true; }

        float myAngle = asker.AngleFromPlayerGaze;
        for (int i = 0; i < _wolves.Count; i++)
        {
            var w = _wolves[i];
            if (w == null || !w.IsAlive || w.Transform == asker.transform) continue;
            var p = w.Transform.GetComponent<WerewolfPerception>();
            if (p != null && p.AngleFromPlayerGaze > myAngle + 1f) return false; // есть волк в позиции лучше
        }

        _nextJumpAllowed = Time.time + packJumpInterval;
        return true;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 c = spawnCenter != null ? spawnCenter.position : transform.position;
        Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.6f);
        Gizmos.DrawWireSphere(c, spawnRadius);
    }
#endif
}
