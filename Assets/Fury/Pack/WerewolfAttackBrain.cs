using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Мозг АТАКИ оборотня (третий мозг). Живёт на префабе волка рядом с WerewolfSurroundBrain.
/// Реализует WerewolfPackManager.IPackAgent — менеджер выдаёт роль и токен, волк решает сам.
///
/// Роли (переключение мозгов — ЗДЕСЬ, менеджер в компоненты не лезет):
///   Attack   → включён этот мозг, WerewolfSurroundBrain выключен. Волк подходит и атакует.
///   Surround → включён WerewolfSurroundBrain (кружит), этот мозг выключен.
/// Два мозга разом никогда не работают.
///
/// В роли Attack — 4 фазы (Approach → Engage → Retreat → Orbit → снова Approach):
///   • Approach — подход. Цель считает сам: обычный — на игрока; avoidFront (второй) — НЕ спереди.
///     Патфайндер как раньше (не изменяется, только вызывается) + расталкивание от соседей.
///   • Engage — заход. На входе раскладывается серия по ступени агрессии (EnterEngage):
///     осторожный hitsCautious, средний hitsMid, злой hitsFierce, ярость — без счёта.
///     Пока удар не готов, волк НЕ топчется вплотную, а держит SafeDistance и закручивается к спине.
///     Дистанция решает атаку: ближе → свип, средне → особая, дальше → прыжок.
///   • Retreat — серия кончилась (или стамины нет даже на уворот) — отход на SafeDistance,
///     шагом в сторону мимо ближайшего соседа. Без стамины отход идёт пешком (walkSpeed).
///   • Orbit — кружит на SafeDistance; aggression сокращает время до нового захода.
///
/// Дистанция одна на все фазы — SafeDistance: Lerp(minHoldDistance, оружие игрока + safetyMargin)
/// по разнице страха и агрессии. Ярость жмётся к 5 м, ужас уходит на 7. Внутрь круга волк
/// заходит только на сам удар. Уворот стоит dodgeStaminaCost, и все, кроме ярости, держат
/// этот запас нетронутым (_reserveDodge) — иначе отходить будет нечем.
///
/// Отложено: прыжок для рельефа, уход с воды −4, «вцепиться/тащить» у второго.
/// Реализовано: здоровье (WerewolfStats, IDamageable), опаска замаха и отскок от удара игрока,
/// ступени агрессии (низкая — атака только из-за спины, средняя — вне сектора оружия,
/// высокая — прежнее поведение) и срыв стаи (OnPackPanic поднимает личный страх).
/// </summary>
[RequireComponent(typeof(WerewolfPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
[RequireComponent(typeof(WerewolfCombat))]
public class WerewolfAttackBrain : MonoBehaviour, WerewolfPackManager.IPackAgent
{
    [Header("Ссылки (авто-подхват, если пусто)")]
    public WerewolfPerception perception;
    public WerewolfLocomotion locomotion;
    public WerewolfCombat combat;
    [Tooltip("Мозг окружения. Включается в роли Surround, гасится в Attack.")]
    public WerewolfSurroundBrain surroundBrain;
    [Tooltip("Сетка пути. Пусто — найдётся на сцене.")]
    public Pathfinder pathfinder;
    [Tooltip("Параметры оборотня (агрессия). Пусто — найдётся на волке.")]
    public WerewolfStats stats;

    [Header("Скорость")]
    [Tooltip("Скорость бега к игроку (м/с). ~галоп локомоции.")]
    public float runSpeed = 8f;
    [Tooltip("Скорость отхода, когда стамины не осталось даже на уворот (м/с) — волк уходит пешком.")]
    public float walkSpeed = 3.5f;

    // Дистанции атак наследуются из WerewolfCombat конкретного волка (не дублируются здесь):
    //   MeleeRange — range свипа, SpecialReach — range особой, JumpRange — дальность наскока.
    private float MeleeRange => combat != null ? combat.swipe.range : 2f;
    private float SpecialReach => combat != null ? combat.special.range : 3.5f;
    private float JumpRange => combat != null ? combat.jumpLeapDistance : 6f;

    [Header("Заход не спереди (для второго)")]
    [Tooltip("На сколько метров позади игрока целиться второму волку.")]
    public float behindDistance = 2f;

    [Header("Фронт по лучу альфа→игрок")]
    [Tooltip("0 = целиться прямо на игрока; 1 = держаться на линии между игроком и альфой. Только для фронтовиков (не второго).")]
    [Range(0f, 1f)] public float frontLineBias = 0.6f;

    [Header("Агрессия от событий")]
    [Tooltip("Сколько агрессии (0..100) добавляется за одно своё попадание.")]
    public float aggroPerHit = 5f;

    [Header("Смерть")]
    [Tooltip("Класть тело набок вручную. Нужно только если клип смерти (VDEATH) не подцеплен к триггеру Death — иначе поворот подерётся с анимацией.")]
    public bool layDownOnDeath = false;

    [Header("Стойка")]
    [Tooltip("Ближе этой дистанции волк встаёт на две ноги (манёвры и бой), дальше — на четвереньки (скорость). " +
             "Волк с canBiped=false в локомоции остаётся на четвереньках всегда.")]
    public float bipedDistance = 6f;

    [Header("Ступени страха")]
    [Tooltip("Со ступени «напуган» (страх 50+) волк перестаёт атаковать: вся стамина уходит на увороты, дистанция держится максимальная.")]
    public WerewolfStats.FearTier noAttackTier = WerewolfStats.FearTier.Afraid;

    [Header("Ступени агрессии (пороги по 25 — в WerewolfStats.AggressionTier)")]
    [Tooltip("Сколько секунд продержаться в разрешённом секторе, чтобы атаковать (осторожная и средняя ступени).")]
    public float sectorHoldTime = 2f;
    [Tooltip("«За спиной» для осторожной ступени: угол от взгляда игрока больше этого (град).")]
    public float behindAngle = 120f;
    [Tooltip("Запас к полуконусу оружия игрока (WeaponHitbox.coneHalfAngle) для средней ступени (град).")]
    public float weaponSectorMargin = 20f;

    [Header("Путь")]
    [Tooltip("Как часто перестраивать путь (сек). Реже = дешевле, но менее отзывчиво.")]
    public float repathInterval = 0.4f;
    [Tooltip("Насколько близко подойти к вейпоинту, чтобы взять следующий (м).")]
    public float waypointArrive = 1.2f;

    [Header("После атаки: отход и облёт")]
    [Tooltip("Базовое время облёта перед новым заходом (сек); aggression сокращает его (см. EnterOrbit).")]
    public float disengageTime = 1.5f;
    [Tooltip("Угловая скорость облёта вокруг игрока (град/сек).")]
    public float orbitAngularSpeed = 45f;

    [Header("Безопасная дистанция")]
    [Tooltip("Ближе этого волк не держится даже в ярости (м). Внутрь заходит только на сам удар.")]
    public float minHoldDistance = 5f;
    [Tooltip("Запас к дальности оружия игрока: так далеко держится самый пугливый (м). 4 + 3 = 7.")]
    public float safetyMargin = 3f;
    [Tooltip("Случайный разброс радиуса облёта (±м), чтобы не липнуть к окружности.")]
    public float orbitRadiusJitter = 0.8f;
    [Tooltip("Как часто менять разброс радиуса (сек).")]
    public float jitterInterval = 0.7f;

    [Header("Осторожность: удар после атаки игрока")]
    [Tooltip("Окно после конца атаки игрока, когда волк охотно бьёт (сек). Осторожная ступень бьёт ТОЛЬКО в нём.")]
    public float opportunityWindow = 0.8f;

    [Header("Серия ударов за один заход")]
    [Tooltip("Сколько ударов подряд делает осторожный, прежде чем отойти.")]
    public int hitsCautious = 1;
    [Tooltip("Сколько ударов подряд делает средний.")]
    public int hitsMid = 2;
    [Tooltip("Сколько ударов подряд делает злой (третья ступень).")]
    public int hitsFierce = 3;
    [Tooltip("Сколько стамины стоит уворот. Пока волк не в ярости, этот запас не тратится на удары.")]
    public float dodgeStaminaCost = 12f;

    [Header("Реакция на игрока (опаска замаха / уворот)")]
    [Tooltip("Запас к длине оружия игрока: реагируем на замах/удар ближе (длина оружия + запас) м.")]
    public float threatRangeMargin = 1.5f;
    [Tooltip("Уворачивается только волк в секторе перед взглядом игрока: угол от взгляда меньше этого (град).")]
    public float dodgeThreatAngle = 60f;
    [Tooltip("Ближе этой дистанции уворот прыжком; дальше — отшагом (м).")]
    public float leapDodgeRange = 2.5f;
    [Tooltip("Импульс отшага вбок/назад без прыжка (м/с).")]
    public float sidestepImpulse = 6f;
    [Tooltip("Длина отскока-прыжка (м).")]
    public float dodgeDistance = 2.5f;
    [Tooltip("Высота дуги отскока.")]
    public float dodgeArc = 0.6f;
    [Tooltip("Пауза между отскоками (сек), чтобы волк не скакал без конца.")]
    public float dodgeCooldown = 1.5f;

    [Header("Не бежать кучей (расталкивание от других волков)")]
    [Tooltip("Сосед ближе этого — расталкиваемся (м).")]
    public float separationRadius = 2.2f;
    [Tooltip("Сила расталкивания от соседей.")]
    public float separationStrength = 2f;

    // ---- состояние роли/токена ----
    private WerewolfPackManager.PackRole _role = WerewolfPackManager.PackRole.Surround;
    private bool _avoidFront;
    private bool _hasToken;

    // ---- путь ----
    private readonly List<Vector3> _path = new List<Vector3>();
    private int _wp;
    private float _repathTimer;

    // ---- фазы: Approach → Engage → Retreat → Orbit → снова Approach ----
    private enum AttackPhase { Approach, Engage, Retreat, Orbit }
    private AttackPhase _phase = AttackPhase.Approach;
    private float _phaseTimer;
    private int _orbitDir = 1;
    private Vector3 _retreatSideDir;

    // ---- реакция на удар игрока ----
    private bool _playerWasAttacking;
    private float _nextDodgeTime;
    private float _opportunityUntil; // окно «игрок только что отмахал» для осторожных
    private float _radiusJitter;     // текущий разброс радиуса облёта
    private float _nextJitterTime;
    private float _sectorTimer;      // сколько волк держится в разрешённом секторе (ступени 1–2)
    private int _hitsLeft;           // сколько ударов осталось в текущей серии
    private bool _reserveDodge;      // держать ли запас стамины на уворот (все, кроме ярости)

    private WerewolfPackManager _manager;

    // ===================== IPackAgent =====================
    public Transform Transform => transform;
    public bool IsAlive => stats == null || stats.IsAlive;
    public float HealthPercent => stats != null ? stats.HealthPercent : 1f;
    public float Fear01 => stats != null ? stats.Fear01 : 0f;

    public void SetRole(WerewolfPackManager.PackRole role, bool avoidFront)
    {
        _role = role;
        _avoidFront = avoidFront;

        if (role == WerewolfPackManager.PackRole.Attack)
        {
            if (surroundBrain != null) surroundBrain.enabled = false;
            _phase = AttackPhase.Approach;   // начинаем заход заново
            _sectorTimer = 0f;
            enabled = true;                 // этот мозг работает
        }
        else // Surround
        {
            if (surroundBrain != null) surroundBrain.enabled = true;
            _path.Clear();
            enabled = false;                // отдаём управление WerewolfSurroundBrain
        }
    }

    public void SetAttackToken(bool hasToken) => _hasToken = hasToken;

    /// <summary>Менеджер сбивает кураж, когда рядом ранят своего.</summary>
    public void AddAggression(float delta)
    {
        if (stats != null) stats.AddAggression(delta);
    }

    // Стая сорвалась — личный страх скачком вверх. Агрессию не трогаем: это отдельная ось.
    public void OnPackPanic()
    {
        if (stats == null) return;
        stats.AddFearTiers(_manager != null ? _manager.panicFearTiers : 2);
    }

    // ===================== Жизненный цикл =====================

    void Awake()
    {
        if (perception == null) perception = GetComponent<WerewolfPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (combat == null) combat = GetComponent<WerewolfCombat>();
        if (surroundBrain == null) surroundBrain = GetComponent<WerewolfSurroundBrain>();
        if (pathfinder == null) pathfinder = FindObjectOfType<Pathfinder>();
        if (stats == null) stats = GetComponent<WerewolfStats>();
        if (stats == null)
            Debug.LogWarning("WerewolfAttackBrain: не найден WerewolfStats — агрессия считается как 0.5.");

        if (combat != null) combat.OnHitLanded += HandleHitLanded;
        if (stats != null) stats.OnDeath += HandleDeath;

        _manager = WerewolfPackManager.Instance;
        if (_manager != null)
        {
            _manager.Register(this);
        }
        else
        {
            Debug.LogWarning("WerewolfAttackBrain: нет WerewolfPackManager на сцене — остаюсь в окружении.");
        }

        // По умолчанию — окружение (ровно один мозг активен, пока менеджер не назначит роль).
        SetRole(WerewolfPackManager.PackRole.Surround, false);
    }

    void OnDestroy()
    {
        if (_manager != null) _manager.Unregister(this);
        if (combat != null) combat.OnHitLanded -= HandleHitLanded;
        if (stats != null) stats.OnDeath -= HandleDeath;
    }

    // Смерть: гасим мозги и бой, тело остаётся лежать. Анимацию играет клип VDEATH по триггеру Death.
    // Локомоция остаётся включённой — она держит гравитацию и дотормаживает тело.
    private void HandleDeath()
    {
        if (_manager != null) { _manager.Unregister(this); _manager = null; }
        if (combat != null) combat.enabled = false;
        if (surroundBrain != null) surroundBrain.enabled = false;
        var stalker = GetComponent<WerewolfAlphaStalker>();
        if (stalker != null) stalker.enabled = false;
        enabled = false;

        if (locomotion != null) locomotion.PlayDeath();

        // Заглушка на случай, если клип смерти не подцеплен: кладём тело набок вручную.
        if (layDownOnDeath)
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 90f);
    }

    // Свой удар попал → злее (растёт шанс продолжить/множественные атаки — через stats.aggression).
    private void HandleHitLanded()
    {
        if (stats != null) stats.AddAggression(aggroPerHit);
    }

    void Update()
    {
        // Update идёт только в роли Attack (в Surround компонент выключен).
        float dt = Time.deltaTime;
        if (!perception.HasPlayer) return;

        // Агрессия копится только в роли Attack (вне её значение замирает).
        if (stats != null) stats.AddAggression(stats.aggressionPerSecond * dt);

        // Таймер сектора (ступени 1–2): держится ли волк в разрешённой для атаки зоне.
        // Осторожный — за спиной (behindAngle), средний — вне сектора оружия (+запас).
        var atier = AggroTier;
        if (atier <= WerewolfStats.AggressionTier.Mid)
        {
            float need = atier == WerewolfStats.AggressionTier.Cautious
                ? behindAngle
                : perception.PlayerWeaponConeHalfAngle + weaponSectorMargin;
            if (perception.AngleFromPlayerGaze > need) _sectorTimer += dt;
            else _sectorTimer = 0f;
        }

        // Детект начала/конца удара игрока (фронты перехода IsAttacking).
        bool playerAttacking = perception.PlayerIsAttacking;
        bool attackStarted = playerAttacking && !_playerWasAttacking;
        bool attackEnded = !playerAttacking && _playerWasAttacking;
        _playerWasAttacking = playerAttacking;
        if (attackEnded) _opportunityUntil = Time.time + opportunityWindow;

        // Стойка: близко — на две ноги (манёвры, стрейф, бой), далеко — на четвереньки (скорость).
        // В ужасе всегда на четвереньках: убегать быстрее. Пока идёт смена, локомоция стоит на месте.
        bool wantBiped = !InTerror && perception.DistanceToPlayer <= bipedDistance;
        locomotion.SetStance(wantBiped
            ? WerewolfLocomotion.Stance.Biped
            : WerewolfLocomotion.Stance.Quad);
        if (locomotion.IsChangingStance) return;   // встаёт/опускается — не двигаемся и не бьём

        // Идёт атака — не двигаемся, ждём. Прыжок/замах ведёт WerewolfCombat/локомоция.
        if (combat.IsBusy) return;

        // Вне своей атаки волк всегда пытается уйти от замаха или удара игрока.
        if (attackStarted || perception.PlayerIsCharging) TryDodge();

        switch (_phase)
        {
            case AttackPhase.Approach: TickApproach(dt); break;
            case AttackPhase.Engage: TickEngage(dt); break;
            case AttackPhase.Retreat: TickRetreat(dt); break;
            case AttackPhase.Orbit: TickOrbit(dt); break;
        }
    }

    // ===================== Approach: далеко — подходим =====================

    private void TickApproach(float dt)
    {
        if (perception.DistanceToPlayer <= JumpRange) { EnterEngage(); return; }
        Approach(dt);
    }

    // Заход начался — раскладываем серию по ступени агрессии.
    // Ярость бьёт без счёта и без запаса на уворот, остальные оставляют стамину на один отскок.
    private void EnterEngage()
    {
        _phase = AttackPhase.Engage;
        switch (AggroTier)
        {
            case WerewolfStats.AggressionTier.Rage:
                _hitsLeft = int.MaxValue; _reserveDodge = false; break;
            case WerewolfStats.AggressionTier.Fierce:
                _hitsLeft = Mathf.Max(1, hitsFierce); _reserveDodge = true; break;
            case WerewolfStats.AggressionTier.Mid:
                _hitsLeft = Mathf.Max(1, hitsMid); _reserveDodge = true; break;
            default:
                _hitsLeft = Mathf.Max(1, hitsCautious); _reserveDodge = true; break;
        }
    }

    // ===================== Engage: в зоне удара =====================

    private void TickEngage(float dt)
    {
        float dist = perception.DistanceToPlayer;
        if (dist > JumpRange) { _phase = AttackPhase.Approach; return; } // игрок ушёл — снова подходим

        // Опаска замаха: игрок заряжает удар, мы близко — пятимся на безопасную, не атакуем.
        float threatRange = perception.PlayerWeaponRange + threatRangeMargin;
        if (perception.PlayerIsCharging && dist < threatRange && AggroTier < WerewolfStats.AggressionTier.Rage)
        {
            Vector3 away = perception.DirFromPlayerFlat;
            Vector3 backTarget = perception.PlayerPos + away * SafeDistance + SeparationOffset();
            locomotion.MoveTo(backTarget, runSpeed, dt);
            locomotion.FaceTowards(perception.PlayerPos, dt);   // после MoveTo — иначе поворот съест движение
            return;
        }

        locomotion.FaceTowards(perception.PlayerPos, dt);

        // Стамины не хватает даже на уворот — защищаться нечем, уходим за границу удара.
        if (stats != null && !stats.HasEnough(dodgeStaminaCost)) { EnterRetreat(); return; }

        if (MayAttackNow(dist) && TryAttackByDistance(dist))
        {
            _sectorTimer = 0f;
            if (_hitsLeft != int.MaxValue) _hitsLeft--;
            if (_hitsLeft <= 0) EnterRetreat();
            return;
        }

        // Удар сейчас не вышел (нет токена/стамины/кулдаун, не тот сектор или игрок машет) —
        // не топчемся в зоне поражения: держим безопасную дистанцию, закручиваясь к спине.
        MoveAroundToBack(dt, SafeDistance);
    }

    /// <summary>Можно ли бить прямо сейчас: страх, ступень агрессии, сектор, токен и запас стамины.</summary>
    private bool MayAttackNow(float dist)
    {
        if (TooScaredToAttack) return false;                    // трус только уворачивается

        var tier = AggroTier;
        bool opportunity = Time.time < _opportunityUntil;
        bool playerBusy = perception.PlayerIsAttacking || perception.PlayerIsCharging;

        // Осторожный ждёт удобного случая — окна сразу после удара игрока.
        if (tier == WerewolfStats.AggressionTier.Cautious && !opportunity) return false;
        // Остальные, кроме ярости, не лезут под работающее оружие.
        if (playerBusy && !opportunity && tier < WerewolfStats.AggressionTier.Rage) return false;

        // Ступени 1–2 сначала выдерживают сектор: осторожный — за спиной, средний — вне дуги оружия.
        if (tier <= WerewolfStats.AggressionTier.Mid && _sectorTimer < sectorHoldTime) return false;

        // Токен фронта. Осторожный бьёт со спины и очереди не занимает; второй (avoidFront) тоже.
        if (tier != WerewolfStats.AggressionTier.Cautious && !_avoidFront && !_hasToken) return false;

        // Запас на уворот: тратим стамину до конца только в ярости.
        if (_reserveDodge && stats != null &&
            !stats.HasEnough(AttackCostFor(dist) + dodgeStaminaCost)) return false;

        return true;
    }

    /// <summary>Во сколько стамины обойдётся удар, который выберется на этой дистанции.</summary>
    private float AttackCostFor(float dist)
    {
        if (combat == null) return 0f;
        if (dist <= MeleeRange) return combat.swipe.staminaCost;
        if (dist <= SpecialReach) return combat.special.staminaCost;
        return combat.jump.staminaCost;
    }

    // Кружит вокруг игрока на заданном радиусе в сторону увеличения угла от его взгляда (к спине).
    private void MoveAroundToBack(float dt, float radius)
    {
        Vector3 p = perception.PlayerPos;
        Vector3 dir = transform.position - p; dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;

        float signed = Vector3.SignedAngle(perception.PlayerForwardFlat, dir, Vector3.up);
        float side = Mathf.Abs(signed) < 5f ? (Random.value > 0.5f ? 1f : -1f) : Mathf.Sign(signed);

        Vector3 rotated = Quaternion.AngleAxis(orbitAngularSpeed * side * dt, Vector3.up) * dir;
        Vector3 target = p + rotated * radius + SeparationOffset();
        locomotion.MoveTo(target, runSpeed, dt);

        // Смотрим на игрока, а не туда, куда идём: это и есть манёвр вокруг цели.
        // В Quad локомоция сама решит — на бегу морда всё равно смотрит по ходу.
        locomotion.FaceTowards(perception.PlayerPos, dt);
    }

    private bool TryAttackByDistance(float dist)
    {
        // Напуган — не лезет бить вообще, какой бы ни была агрессия.
        if (TooScaredToAttack) return false;

        if (dist <= MeleeRange) return combat.TrySwipe();
        if (dist <= SpecialReach) return combat.TrySpecial();
        // Между SpecialReach и JumpRange. Прыжок — с разрешения стаи (интервал + лучшая позиция).
        if (_manager != null && !_manager.RequestJump(perception)) return false;
        return combat.TryJump();
    }

    // Уворот от замаха/удара игрока: вплотную — прыжок вбок/назад, чуть дальше — быстрый отшаг.
    private void TryDodge()
    {
        float dist = perception.DistanceToPlayer;
        float threatRange = perception.PlayerWeaponRange + threatRangeMargin;
        if (Time.time < _nextDodgeTime) return;
        if (dist > threatRange) return;
        if (perception.AngleFromPlayerGaze > dodgeThreatAngle) return; // мы не под ударом — не дёргаемся

        // Уворот стоит стамину. Пусто — прыгать нечем, уходим пешком (это делает фаза Retreat).
        if (stats != null)
        {
            if (!stats.HasEnough(dodgeStaminaCost)) { EnterRetreat(); return; }
            stats.Spend(dodgeStaminaCost);
        }

        _nextDodgeTime = Time.time + dodgeCooldown;
        Vector3 away = perception.DirFromPlayerFlat;
        Vector3 side = Vector3.Cross(Vector3.up, away) * (Random.value > 0.5f ? 1f : -1f);
        Vector3 dir = (away + side * 0.7f).normalized;

        if (dist <= leapDodgeRange)
            locomotion.Leap(transform.position + dir * dodgeDistance, dodgeArc); // впритык — только прыжком
        else
            locomotion.AddImpulse(dir * sidestepImpulse); // есть запас — отшаг без прыжка
    }

    // ===================== Retreat: атаковал — отошёл, обходя соседа =====================

    private void EnterRetreat()
    {
        _phase = AttackPhase.Retreat;
        _retreatSideDir = PickSideStepDir();
    }

    private void TickRetreat(float dt)
    {
        float safe = SafeDistance;

        Vector3 outward = transform.position - perception.PlayerPos; outward.y = 0f;
        outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : transform.forward;

        Vector3 target = perception.PlayerPos + outward * safe
                        + _retreatSideDir * 1.5f + SeparationOffset();

        // Без стамины на уворот бежать некуда — отходим пешком, зато не тратим последнее.
        bool tired = stats != null && !stats.HasEnough(dodgeStaminaCost);
        locomotion.MoveTo(target, tired ? walkSpeed : runSpeed, dt);
        locomotion.FaceTowards(perception.PlayerPos, dt); // пятится, но смотрит на игрока

        if (perception.DistanceToPlayer >= safe * 0.95f) EnterOrbit();
    }

    // Сторона обхода — мимо ближайшего соседа (если есть), иначе случайно.
    private Vector3 PickSideStepDir()
    {
        Vector3 right = transform.right;
        // Вектор расталкивания уже показывает, куда уходить от соседей — берём его знак.
        Vector3 push = SeparationOffset();
        if (push.sqrMagnitude > 1e-4f)
            return Vector3.Dot(push, right) > 0f ? right : -right;
        return Random.value > 0.5f ? right : -right;
    }

    // ===================== Orbit: кружит перед новым заходом =====================

    private float Aggression => stats != null ? stats.Aggression01 : 0.5f;

    /// <summary>Ступень агрессии. Без stats — считаем средней.</summary>
    private WerewolfStats.AggressionTier AggroTier =>
        stats != null ? stats.AggroTier : WerewolfStats.AggressionTier.Mid;

    /// <summary>
    /// Дистанция, на которой волк держится, пока не бьёт. Ярость жмётся к minHoldDistance,
    /// ужас уходит на «дальность оружия игрока + safetyMargin». Поровну страха и агрессии — середина.
    /// Внутрь этого круга волк заходит только на сам удар и сразу выходит обратно.
    /// </summary>
    private float SafeDistance
    {
        get
        {
            float far = perception.PlayerWeaponRange + safetyMargin;
            float t = Mathf.Clamp01(0.5f + (Fear01 - Aggression) * 0.5f);
            return Mathf.Max(minHoldDistance, Mathf.Lerp(minHoldDistance, far, t));
        }
    }

    /// <summary>Ступень страха. Без stats — считаем спокойным.</summary>
    private WerewolfStats.FearTier FearTier =>
        stats != null ? stats.Tier : WerewolfStats.FearTier.Calm;

    /// <summary>Напуган настолько, что не лезет в атаку (порог noAttackTier).</summary>
    private bool TooScaredToAttack => FearTier >= noAttackTier;

    /// <summary>Ужас — волк выходит из боя. Пока просто держится максимально далеко;
    /// полноценное бегство появится вместе с токеном Flee.</summary>
    private bool InTerror => FearTier >= WerewolfStats.FearTier.Terror;

    private void EnterOrbit()
    {
        _phase = AttackPhase.Orbit;
        _orbitDir = Random.value > 0.5f ? 1 : -1;
        // Aggression сокращает время облёта: борзый почти сразу снова идёт в атаку.
        _phaseTimer = Mathf.Lerp(disengageTime, disengageTime * 0.3f, Aggression);
    }

    private void TickOrbit(float dt)
    {
        _phaseTimer -= dt;

        Vector3 toSelf = transform.position - perception.PlayerPos; toSelf.y = 0f;

        // Радиус зависит от агрессии: борзый жмётся к дистанции удара, пуганый маневрирует поодаль.
        // Шум радиуса, чтобы волки не липли к идеальной окружности.
        if (Time.time >= _nextJitterTime)
        {
            _nextJitterTime = Time.time + jitterInterval;
            _radiusJitter = Random.Range(-orbitRadiusJitter, orbitRadiusJitter);
        }
        float radius = SafeDistance + _radiusJitter;
        Vector3 dir = toSelf.sqrMagnitude > 1e-4f ? toSelf.normalized : transform.forward;
        Vector3 rotated = Quaternion.AngleAxis(orbitAngularSpeed * _orbitDir * dt, Vector3.up) * dir;

        Vector3 target = perception.PlayerPos + rotated * radius + SeparationOffset();
        locomotion.MoveTo(target, runSpeed, dt);
        // После MoveTo: в Biped волк развернётся к игроку, в Quad локомоция оставит морду по ходу бега.
        locomotion.FaceTowards(perception.PlayerPos, dt);

        if (_phaseTimer <= 0f) _phase = AttackPhase.Approach;
    }

    // ===================== Расталкивание (не бежать кучей) =====================

    private Vector3 SeparationOffset()
    {
        // Считает менеджер: у него есть список стаи, поэтому обходимся без Physics.OverlapSphere,
        // который создавал новый массив на каждый вызов (а вызывался он каждый кадр).
        if (_manager == null) return Vector3.zero;
        return _manager.SeparationFor(transform, separationRadius, separationStrength);
    }

    // ===================== Подход =====================

    private void Approach(float dt)
    {
        Vector3 target = ApproachTarget();

        _repathTimer -= dt;
        if (_repathTimer <= 0f)
        {
            _repathTimer = repathInterval;
            RebuildPath(target);
        }

        Vector3 wp = NextWaypoint(target) + SeparationOffset(); // не бежать кучей — подруливаем от соседей
        locomotion.MoveTo(wp, runSpeed, dt);
    }

    // Куда идём: второй — за спину игроку; фронтовики — на линию между игроком и альфой.
    private Vector3 ApproachTarget()
    {
        Vector3 p = perception.PlayerPos;
        if (_avoidFront) return p - perception.PlayerForwardFlat * behindDistance;

        // Фронт: держимся со стороны альфы (между игроком и альфой), сила — frontLineBias.
        Transform alpha = _manager != null ? _manager.alphaTransform : null;
        if (alpha != null && frontLineBias > 0f)
        {
            Vector3 toAlpha = alpha.position - p; toAlpha.y = 0f;
            if (toAlpha.sqrMagnitude > 1e-4f)
            {
                Vector3 frontPoint = p + toAlpha.normalized * MeleeRange; // точка на стороне альфы
                return Vector3.Lerp(p, frontPoint, frontLineBias);
            }
        }
        return p;
    }

    private void RebuildPath(Vector3 target)
    {
        _wp = 0;
        if (pathfinder != null && pathfinder.IsReady &&
            pathfinder.TryFindPath(transform.position, target, _path))
        {
            return; // путь построен
        }
        _path.Clear(); // пути нет — пойдём напрямую (см. NextWaypoint)
    }

    private Vector3 NextWaypoint(Vector3 fallbackTarget)
    {
        if (_path.Count == 0) return fallbackTarget; // прямой ход, локомоция перелезет рельеф

        // Продвигаемся по вейпоинтам.
        while (_wp < _path.Count)
        {
            Vector3 cur = _path[_wp];
            Vector3 d = cur - transform.position; d.y = 0f;
            if (d.magnitude <= waypointArrive) { _wp++; continue; }
            return cur;
        }
        return fallbackTarget; // прошли путь — дожимаем к цели
    }
}
