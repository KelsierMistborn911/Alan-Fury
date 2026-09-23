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
///     Дистанция + позиция: сзади в упоре → укус (special), вплотную спереди → свип, дальше → прыжок.
///   • Retreat — серия кончилась (или стамины нет даже на уворот) — отход на SafeDistance,
///     шагом в сторону мимо ближайшего соседа. Без стамины отход идёт пешком (walkSpeed).
///   • Orbit — кружит на SafeDistance; aggression сокращает время до нового захода.
///
/// Дистанция одна на все фазы — SafeDistance: страх тянет кольцо наружу, ярость — к minHold.
/// Внутрь круга волк заходит только на сам удар. Уворот всегда пытается, если есть угроза;
/// настроение больше не роллит шанс. После своего удара/прыжка — инерция (lock + скольжение).
/// Уворот стоит dodgeStaminaCost; все, кроме ярости, держат запас (_reserveDodge).
///
/// Отложено: прыжок для рельефа, уход с воды −4, «вцепиться/тащить» у второго.
/// Реализовано: здоровье (WerewolfStats, IDamageable), опаска замаха и отскок от удара игрока,
/// ступени агрессии (низкая — атака только из-за спины, средняя — вне сектора оружия,
/// высокая — прежнее поведение) и срыв стаи (OnPackPanic поднимает личный страх).
/// </summary>
[RequireComponent(typeof(NpcPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
[RequireComponent(typeof(WerewolfCombat))]
public class WerewolfAttackBrain : MonoBehaviour, WerewolfPackManager.IPackAgent
{
    [Header("Ссылки (авто-подхват, если пусто)")]
    public NpcPerception perception;
    public WerewolfLocomotion locomotion;
    public WerewolfCombat combat;
    [Tooltip("Мозг окружения. Включается в роли Surround, гасится в Attack.")]
    public WerewolfSurroundBrain surroundBrain;
    [Tooltip("Сетка пути. Пусто — берётся из WerewolfPackManager.")]
    public Pathfinder pathfinder;
    [Tooltip("Параметры оборотня (агрессия). Пусто — найдётся на волке.")]
    public WerewolfStats stats;

    [Header("Скорость")]
    [Tooltip("Скорость бега к игроку (м/с). ~галоп локомоции.")]
    public float runSpeed = 8f;
    [Tooltip("Скорость отхода, когда стамины не осталось даже на уворот (м/с) — волк уходит пешком.")]
    public float walkSpeed = 3.5f;

    // Пояса: swipe = Mid, special = свой короткий range (укус), jump = jumpLeapDistance.
    private float MeleeRange => CombatRangeTable.Default.Outer(CombatRange.Mid);
    private float SpecialReach => combat != null ? combat.special.range : CombatRangeTable.Default.Outer(CombatRange.Special);
    private float JumpRange => combat != null ? combat.jumpLeapDistance : 6f;

    [Header("Заход не спереди (для второго)")]
    [Tooltip("На сколько метров позади игрока целиться второму волку.")]
    public float behindDistance = 2f;
    [Tooltip("Угол от взгляда игрока, выше которого special считается «сзади» и получает приоритет (град).")]
    public float specialBehindAngle = 110f;

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
    public float bipedDistance = 8f;

    [Header("Ступени страха")]
    [Tooltip("Со ступени «напуган» (страх 50+) волк перестаёт атаковать: вся стамина уходит на увороты, дистанция держится максимальная.")]
    public WerewolfStats.FearTier noAttackTier = WerewolfStats.FearTier.Afraid;

    [Header("Ступени агрессии (пороги по 25 — в WerewolfStats.AggressionTier)")]
    [Tooltip("Сколько секунд продержаться в разрешённом секторе (средняя ступень). Осторожный мягче.")]
    public float sectorHoldTime = 0.75f;
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
    [Tooltip("Базовое время манёвра между сериями (сек); aggression только укорачивает паузу.")]
    public float disengageTime = 5.5f;
    [Tooltip("Угловая скорость облёта вокруг игрока (град/сек). Дуга между ударами серии.")]
    public float orbitAngularSpeed = 45f;
    [Tooltip("Угол дуги между ударами серии (град).")]
    public float arcDegrees = 48f;
    public float maneuverHoldMin = 1.1f;
    public float maneuverHoldMax = 2.6f;

    [Header("Безопасная дистанция")]
    [Tooltip("Ближнее кольцо (м): сюда заходит только на удар/серию. База даже в ярости.")]
    public float minHoldDistance = 7f;
    [Tooltip("Запас к дальности оружия игрока для расчёта ближнего кольца (м).")]
    public float safetyMargin = 5f;
    [Tooltip("Дальнее кольцо = ближнее × этот множитель. Здесь маневрируют, пока не бьют.")]
    [Range(1.1f, 2f)] public float outerDistanceMult = 1.33f;
    [Tooltip("Высота дуги прыжка-захода с дальнего кольца к игроку.")]
    public float commitLeapArc = 0.85f;
    [Tooltip("Случайный разброс радиуса облёта (±м).")]
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
    public float threatRangeMargin = 2f;
    [Tooltip("Ближе этой дистанции уворот прыжком (если скорость уже низкая); дальше — импульс.")]
    public float leapDodgeRange = 4.5f;
    [Tooltip("Импульс отшага, когда нельзя прыгать из-за инерции (м/с).")]
    public float sidestepImpulse = 7f;
    [Tooltip("Минимальная длина уворота из зоны (м).")]
    public float dodgeMin = 2f;
    [Tooltip("Максимальная длина уворота из зоны (м).")]
    public float dodgeMax = 4f;
    [Tooltip("Запас за край зоны (м).")]
    public float dodgeMargin = 0.45f;
    [Tooltip("Высота дуги короткого отскока.")]
    public float dodgeArc = 0.4f;
    [Tooltip("Пауза между отскоками (сек). Не зависит от страха.")]
    public float dodgeCooldown = 0.5f;
    [Tooltip("После конца своей атаки столько секунд нельзя увернуться (recovery).")]
    public float postAttackDodgeLock = 0.32f;
    [Tooltip("После приземления прыжка нельзя увернуться (сек).")]
    public float dodgeLandLock = 0.2f;
    [Tooltip("Выше этой горизонтальной скорости уворот только импульсом, без нового прыжка.")]
    public float dodgeSlideSpeed = 3.8f;
    [Tooltip("Сколько метров страха добавляет к дальнему кольцу (при Fear=1).")]
    public float fearHoldExtra = 3.5f;

    [Header("Не бежать кучей (расталкивание от других волков)")]
    [Tooltip("Сосед ближе этого — расталкиваемся (м).")]
    public float separationRadius = 3.5f;
    [Tooltip("Сила расталкивания от соседей.")]
    public float separationStrength = 3.5f;

    // ---- состояние роли/токена ----
    private WerewolfPackManager.PackRole _role = WerewolfPackManager.PackRole.Surround;
    private bool _avoidFront;
    private bool _hasToken;

    // ---- путь ----
    private readonly List<Vector3> _path = new List<Vector3>();
    private int _wp;
    private float _repathTimer;

    // ---- фазы: Approach → Engage → Retreat → Orbit → снова Approach ----
    private enum AttackPhase { Approach, Engage, Arc, Retreat, Orbit }
    private AttackPhase _phase = AttackPhase.Approach;
    private float _phaseTimer;
    private int _orbitDir = 1;
    private Vector3 _retreatSideDir;

    // ---- реакция на удар игрока ----
    private bool _playerWasAttacking;
    private float _nextDodgeTime;
    private float _opportunityUntil; // окно «игрок только что отмахал» для осторожных
    private bool _wasCombatBusy;
    private float _postAttackLockUntil; // после своей атаки нельзя сразу увернуться
    private float _radiusJitter;     // текущий разброс радиуса облёта
    private float _nextJitterTime;
    private float _sectorTimer;      // сколько волк держится в разрешённом секторе (ступени 1–2)
    private int _hitsLeft;           // сколько ударов осталось в текущей серии
    private bool _reserveDodge;      // держать ли запас стамины на уворот (все, кроме ярости)
    private bool _wasClinging;
    private float _maneuverHold;
    private Vector3 _arcTarget;

    private WerewolfPackManager _manager;

    // ===================== IPackAgent =====================
    public Transform Transform => transform;
    public bool IsAlive => stats == null || stats.IsAlive;
    public float HealthPercent => stats != null ? stats.HealthPercent : 1f;
    public float Fear01 => stats != null ? stats.Fear01 : 0f;

    public bool CanRotateRole =>
        IsAlive
        && (combat == null || !combat.IsClinging)
        && (combat == null || !combat.IsBusy || _role == WerewolfPackManager.PackRole.Orbit);

    public void SetRole(WerewolfPackManager.PackRole role, bool avoidFront)
    {
        _role = role;
        _avoidFront = avoidFront;

        if (role == WerewolfPackManager.PackRole.Attack
            || role == WerewolfPackManager.PackRole.Orbit)
        {
            if (surroundBrain != null) surroundBrain.enabled = false;
            _sectorTimer = 0f;
            enabled = true;
            if (role == WerewolfPackManager.PackRole.Orbit)
            {
                _phase = AttackPhase.Orbit;
                _phaseTimer = 999f;
                PickManeuverPoint();
            }
            else
                _phase = AttackPhase.Approach;
        }
        else
        {
            if (surroundBrain != null) surroundBrain.enabled = true;
            _path.Clear();
            enabled = false;
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
        if (perception == null) perception = GetComponent<NpcPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (combat == null) combat = GetComponent<WerewolfCombat>();
        if (surroundBrain == null) surroundBrain = GetComponent<WerewolfSurroundBrain>();
        if (stats == null) stats = GetComponent<WerewolfStats>();
        if (stats == null)
            Debug.LogWarning("WerewolfAttackBrain: не найден WerewolfStats — агрессия считается как 0.5.");

        if (combat != null) combat.OnHitLanded += HandleHitLanded;
        if (stats != null) stats.OnDeath += HandleDeath;

        _manager = WerewolfPackManager.Instance;
        if (_manager != null)
        {
            _manager.Register(this);
            if (pathfinder == null) pathfinder = _manager.pathfinder;
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
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned)
        {
            if (locomotion != null) locomotion.MoveTo(transform.position, 0f, dt);
            return;
        }
        if (!perception.HasPlayer) return;

        // Агрессия копится только в роли Attack (вне её значение замирает).
        if (stats != null) stats.AddAggression(stats.aggressionPerSecond * dt);

        // Fleeish: не бьёт → сдать слот, назад в Surround (замена другим).
        if (CurrentMood == WerewolfStats.CombatMood.Fleeish)
        {
            if (_manager != null) _manager.YieldAttackSlot(this);
            else SetRole(WerewolfPackManager.PackRole.Surround, false);
            return;
        }

        // Таймер сектора: бонус для Skittish/Tense (не жёсткий стоп для всех).
        var mood = CurrentMood;
        if (mood == WerewolfStats.CombatMood.Skittish || mood == WerewolfStats.CombatMood.Tense)
        {
            float need = mood == WerewolfStats.CombatMood.Skittish
                ? behindAngle
                : perception.PlayerWeaponConeHalfAngle + weaponSectorMargin;
            if (perception.AngleFromPlayerGaze > need) _sectorTimer += dt;
            else _sectorTimer = 0f;
        }
        else _sectorTimer = sectorHoldTime; // Rage/Aggressive — сектор не блокирует

        // Детект начала/конца удара игрока (фронты перехода IsAttacking).
        bool playerAttacking = perception.PlayerIsAttacking;
        bool attackStarted = playerAttacking && !_playerWasAttacking;
        bool attackEnded = !playerAttacking && _playerWasAttacking;
        _playerWasAttacking = playerAttacking;
        if (attackEnded) _opportunityUntil = Time.time + opportunityWindow;

        if (_role == WerewolfPackManager.PackRole.Orbit)
        {
            bool orbitBiped = !InTerror && perception.DistanceToPlayer <= ManeuverDistance * 0.9f;
            locomotion.SetStance(orbitBiped
                ? WerewolfLocomotion.Stance.Biped
                : WerewolfLocomotion.Stance.Quad);
            if (locomotion.IsChangingStance) return;
            if (!combat.IsBusy && Time.time >= _postAttackLockUntil &&
                (PlayerStrikeIncoming() || attackStarted))
                TryDodge();
            if (combat.IsBusy) return;
            _phaseTimer = 999f;
            TickOrbit(dt);
            return;
        }

        // Стойка по роли движения (аниматор: Stance bool, StandUp/DropDown, Gait):
        //   Biped — боевой контакт: стрейф вокруг игрока, взгляд на него.
        //   Quad  — дальние перебежки (подход/длинный отход): быстрее, только вперёд по курсу.
        // В ужасе всегда Quad.
        bool wantBiped = !InTerror && WantsBipedStance();
        locomotion.SetStance(wantBiped
            ? WerewolfLocomotion.Stance.Biped
            : WerewolfLocomotion.Stance.Quad);
        if (locomotion.IsChangingStance) return;   // встаёт/опускается — ждём StandUp/DropDown

        // Конец своей атаки → короткий lock на уворот (recovery), потом снова можно отпрыгнуть.
        bool busy = combat.IsBusy;
        if (_wasCombatBusy && !busy)
            _postAttackLockUntil = Time.time + postAttackDodgeLock;
        _wasCombatBusy = busy;

        // Уворот — реакция на телеграф/удар игрока. Во время своего IsBusy прыгать нельзя;
        // сразу после своей атаки — небольшая задержка (postAttackDodgeLock).
        if (!busy && Time.time >= _postAttackLockUntil &&
            (PlayerStrikeIncoming() || attackStarted))
            TryDodge();

        if (combat != null && combat.IsClinging)
        {
            _wasClinging = true;
            return;
        }
        if (_wasClinging)
        {
            _wasClinging = false;
            EnterRetreat();
            return;
        }

        // Идёт своя атака — движение/мозг ждут, фазу ведёт Combat/локомоция.
        if (busy) return;

        switch (_phase)
        {
            case AttackPhase.Approach: TickApproach(dt); break;
            case AttackPhase.Engage: TickEngage(dt); break;
            case AttackPhase.Arc: TickArc(dt); break;
            case AttackPhase.Retreat: TickRetreat(dt); break;
            case AttackPhase.Orbit: TickOrbit(dt); break;
        }
    }

    // ===================== Approach: далеко — подходим =====================

    /// <summary>
    /// Biped = close-combat: стрейф, удары, контроль (Engage / Orbit / почти на месте).
    /// Quad = бег: Approach, длинный Retreat, спринт. Не «потому что далеко», а «потому что бегу».
    /// </summary>
    private bool WantsBipedStance()
    {
        switch (_phase)
        {
            case AttackPhase.Engage:
            case AttackPhase.Arc:
                return true;
            case AttackPhase.Orbit:
                return perception.DistanceToPlayer <= ManeuverDistance * 0.9f;
            case AttackPhase.Retreat:
                // Короткий шаг у кольца — biped; длинный отход — quad (бег).
                return perception.DistanceToPlayer <= ManeuverDistance * 0.85f;
            default: // Approach — бежим к кольцу
                return false;
        }
    }

    private void TickApproach(float dt)
    {
        // Дошли до дальнего кольца (или дальности наскока) — переходим в Engage/маневр.
        float enterAt = Mathf.Max(JumpRange, ManeuverDistance);
        if (perception.DistanceToPlayer <= enterAt) { EnterEngage(); return; }
        Approach(dt);
    }

    // Заход: длина серии от CombatMood (Fear+Aggro).
    private void EnterEngage()
    {
        _phase = AttackPhase.Engage;
        switch (CurrentMood)
        {
            case WerewolfStats.CombatMood.Rage:
                _hitsLeft = int.MaxValue; _reserveDodge = false; break;
            case WerewolfStats.CombatMood.Aggressive:
                _hitsLeft = Mathf.Max(1, hitsFierce); _reserveDodge = true; break;
            case WerewolfStats.CombatMood.Tense:
                _hitsLeft = Mathf.Max(1, hitsMid); _reserveDodge = true; break;
            default: // Skittish
                _hitsLeft = Mathf.Max(1, hitsCautious); _reserveDodge = true; break;
        }
    }

    // ===================== Engage: в зоне удара =====================

    private void TickEngage(float dt)
    {
        float dist = perception.DistanceToPlayer;
        // Слишком далеко от дальнего кольца — снова подход.
        if (dist > Mathf.Max(JumpRange, ManeuverDistance + 2f)) { _phase = AttackPhase.Approach; return; }

        // Опаска: только живая зона / уже идущий удар — отойти. Заряд не считается.
        float threatRange = perception.PlayerWeaponRange + threatRangeMargin;
        if (PlayerStrikeIncoming() && dist < threatRange && CurrentMood != WerewolfStats.CombatMood.Rage)
        {
            Vector3 away = perception.DirFromPlayerFlat;
            Vector3 backTarget = perception.PlayerPos + away * ManeuverDistance + SeparationOffset();
            locomotion.MoveTo(backTarget, runSpeed, dt);
            locomotion.FaceTowards(perception.PlayerPos, dt);
            return;
        }

        locomotion.FaceTowards(perception.PlayerPos, dt);

        // Готов бить → с дальнего скачок/подход, с ближнего — удар. Не ждёт игрока.
        if (MayAttackNow(dist))
        {
            if (TryAttackByDistance(dist))
            {
                _sectorTimer = 0f;
                if (_hitsLeft != int.MaxValue) _hitsLeft--;
                if (_hitsLeft <= 0) EnterRetreat();
                else EnterArc();
                return;
            }
            CloseInForAttack(dt);
            return;
        }

        // Не готов — маневр на дальнем кольце.
        LooseFlank(dt);
    }

    /// <summary>Можно ли бить: токен/стамина + шанс от CombatMood.</summary>
    private bool MayAttackNow(float dist)
    {
        var mood = CurrentMood;
        if (mood == WerewolfStats.CombatMood.Fleeish) return false;

        bool opportunity = Time.time < _opportunityUntil;
        bool playerBusy = PlayerStrikeIncoming();

        // Под уже идущим ударом — только Rage или окно после него. Заряд не блокирует.
        if (playerBusy && !opportunity && mood != WerewolfStats.CombatMood.Rage) return false;

        // Skittish/Tense: сектор как мягкий гейт.
        if (mood == WerewolfStats.CombatMood.Tense && _sectorTimer < sectorHoldTime * 0.5f) return false;
        if (mood == WerewolfStats.CombatMood.Skittish && _sectorTimer < sectorHoldTime * 0.35f && !opportunity)
            return false;

        // Токен фронта (фланкер без токена; Skittish тоже может без токена — бьёт сбоку/сзади).
        if (mood != WerewolfStats.CombatMood.Skittish && !_avoidFront && !_hasToken) return false;

        if (_reserveDodge && stats != null &&
            !stats.HasEnough(AttackCostFor(dist) + dodgeStaminaCost)) return false;

        float chance = MoodAttackChance(mood);
        bool frontal = perception.AngleFromPlayerGaze < 50f;
        if (frontal && mood == WerewolfStats.CombatMood.Skittish) chance *= 0.4f;
        if (frontal && mood == WerewolfStats.CombatMood.Tense) chance *= 0.7f;
        if (opportunity) chance = Mathf.Max(chance, 0.75f);
        if (Random.value > Mathf.Clamp01(chance)) return false;

        return true;
    }

    private static float MoodAttackChance(WerewolfStats.CombatMood mood)
    {
        switch (mood)
        {
            case WerewolfStats.CombatMood.Rage: return 1f;
            case WerewolfStats.CombatMood.Aggressive: return 0.85f;
            case WerewolfStats.CombatMood.Tense: return 0.55f;
            case WerewolfStats.CombatMood.Skittish: return 0.28f;
            default: return 0f;
        }
    }

    /// <summary>Во сколько стамины обойдётся удар, который выберется на этой дистанции.</summary>
    private float AttackCostFor(float dist)
    {
        if (combat == null) return 0f;
        bool behind = perception != null && perception.AngleFromPlayerGaze >= specialBehindAngle;
        if (behind && dist <= SpecialReach) return combat.special.staminaCost;
        if (dist <= MeleeRange) return combat.swipe.staminaCost;
        return combat.jump.staminaCost;
    }

    // Ближнее кольцо — зона удара. Дальнее — маневр (× outerDistanceMult).
    private float NearDistance => SafeDistance;
    private float ManeuverDistance => NearDistance * outerDistanceMult;

    // Готов атаковать: с дальнего — прыжок к ближнему, с ближнего — подбегает в MeleeRange.
    private void CloseInForAttack(float dt)
    {
        if (locomotion.IsLeaping) return;

        Vector3 p = perception.PlayerPos;
        bool grab = perception.DistanceToPlayer > MeleeRange;
        Vector3 land = grab ? StrikeStandPoint() : CommitLandingPoint();
        locomotion.MoveTo(land + SeparationOffset(), runSpeed, dt);
        if (locomotion.IsBiped) locomotion.FaceTowards(p, dt);
    }

    /// <summary>Край досягаемости, не центр игрока.</summary>
    private Vector3 StrikeStandPoint()
    {
        Vector3 p = perception.PlayerPos;
        Vector3 outward = transform.position - p; outward.y = 0f;
        if (outward.sqrMagnitude < 1e-4f) outward = -perception.PlayerForwardFlat;
        outward.Normalize();
        float reach = MeleeRange;
        if (perception.AngleFromPlayerGaze >= specialBehindAngle)
            reach = Mathf.Min(reach, SpecialReach);
        return p + outward * (reach * 0.92f);
    }

    private Vector3 CommitLandingPoint()
    {
        Vector3 p = perception.PlayerPos;
        if (_avoidFront)
            return p - perception.PlayerForwardFlat * (MeleeRange * 0.85f);

        Vector3 outward = transform.position - p; outward.y = 0f;
        if (outward.sqrMagnitude < 1e-4f) outward = -perception.PlayerForwardFlat;
        outward.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, perception.PlayerForwardFlat);
        float sideSign = Vector3.Dot(outward, side) >= 0f ? 1f : -1f;
        return p + (outward * 0.3f + side * sideSign * 0.55f).normalized * (MeleeRange * 0.75f);
    }

    // Маневр на дальнем кольце: separation + лёгкий обход, без жёсткой точки.
    private void LooseFlank(float dt)
    {
        Vector3 p = perception.PlayerPos;
        Vector3 outward = transform.position - p; outward.y = 0f;
        if (outward.sqrMagnitude < 1e-4f) outward = -perception.PlayerForwardFlat;
        outward.Normalize();

        float hold = ManeuverDistance;
        float dist = perception.DistanceToPlayer;
        Vector3 radial = Vector3.zero;
        if (dist < hold - 1.5f) radial = outward;
        else if (dist > hold + 1.5f) radial = -outward;

        Vector3 side = Vector3.Cross(Vector3.up, outward);
        float rearPull = _avoidFront ? 0.7f : 0.25f;
        Vector3 toRear = -perception.PlayerForwardFlat;
        float sideSign = Vector3.Dot(outward, side) >= 0f ? 1f : -1f;
        if (_avoidFront) sideSign = Vector3.Dot(toRear, side) >= 0f ? 1f : -1f;

        Vector3 drift = (side * sideSign * 0.55f + toRear * rearPull + radial * 0.5f).normalized;
        Vector3 target = transform.position + drift * 3f + SeparationOffset();
        Vector3 fromPlayer = target - p; fromPlayer.y = 0f;
        float d = fromPlayer.magnitude;
        if (d > 0.01f)
        {
            float clamped = Mathf.Clamp(d, hold - 2f, hold + 2.5f);
            target = p + fromPlayer / d * clamped;
        }

        locomotion.MoveTo(target, walkSpeed, dt);
        // Боевой контакт — всегда смотрим на игрока (стрейф на двух лапах).
        if (locomotion.IsBiped) locomotion.FaceTowards(p, dt);
    }

    private void EnterArc()
    {
        _phase = AttackPhase.Arc;
        Vector3 p = perception.PlayerPos;
        Vector3 outward = transform.position - p; outward.y = 0f;
        if (outward.sqrMagnitude < 1e-4f) outward = -perception.PlayerForwardFlat;
        outward.Normalize();
        float sign = Random.value > 0.5f ? 1f : -1f;
        Quaternion rot = Quaternion.AngleAxis(arcDegrees * sign, Vector3.up);
        _arcTarget = p + rot * outward * MeleeRange + SeparationOffset();
    }

    private void TickArc(float dt)
    {
        locomotion.MoveTo(_arcTarget, walkSpeed, dt);
        if (locomotion.IsBiped) locomotion.FaceTowards(perception.PlayerPos, dt);
        Vector3 d = _arcTarget - transform.position; d.y = 0f;
        if (d.magnitude <= waypointArrive)
            _phase = AttackPhase.Engage;
    }

    private bool TryAttackByDistance(float dist)
    {
        // Напуган — не лезет бить вообще, какой бы ни была агрессия.
        if (TooScaredToAttack) return false;

        bool behind = perception.AngleFromPlayerGaze >= specialBehindAngle;

        // Сзади в упоре — укус за ногу. Дальше special не берём: надо подойти.
        if (behind && dist <= SpecialReach && combat.TrySpecial())
            return true;

        if (dist <= MeleeRange) return combat.TrySwipe();
        // Дальше Mid — прыжок с разрешения стаи.
        if (_manager != null && !_manager.RequestJump(perception)) return false;
        return combat.TryJump();
    }

    bool PlayerStrikeIncoming()
    {
        if (perception == null) return false;
        WeaponHitbox zone = perception.PlayerHitbox;
        if (zone != null && zone.IsLive)
        {
            Vector3 from = transform.position + Vector3.up * 0.9f;
            if (zone.PointInFullZone(from)) return true;
            if (zone.RayEnters(from, perception.DirFromPlayerFlat, dodgeMax)) return true;
            if (perception.DistanceToPlayer <= perception.PlayerWeaponRange + threatRangeMargin)
                return true;
        }
        return perception.PlayerIsWindingUp || perception.PlayerIsAttacking;
    }

    bool CanCommitDodge()
    {
        if (locomotion == null) return false;
        if (locomotion.IsLeaping || locomotion.IsChangingStance) return false;
        if (!locomotion.IsGrounded) return false;
        if (locomotion.TimeSinceLand < dodgeLandLock) return false;
        return true;
    }

    // Уворот на живую зону / уже идущий удар. Заряд не триггерит. Нет стамины — пропуск.
    private void TryDodge()
    {
        if (!CanCommitDodge()) return;
        if (Time.time < _nextDodgeTime) return;

        float dist = perception.DistanceToPlayer;
        float threatRange = perception.PlayerWeaponRange + threatRangeMargin;
        WeaponHitbox zone = perception != null ? perception.PlayerHitbox : null;
        bool zoneLive = zone != null && zone.IsLive;
        if (!zoneLive && !perception.PlayerIsWindingUp && !perception.PlayerIsAttacking) return;
        if (!zoneLive && dist > threatRange) return;

        Vector3 from = transform.position + Vector3.up * 0.9f;
        Vector3 dir;
        float travel = dodgeMin;
        bool usedZone = false;

        if (zoneLive)
        {
            bool inside = zone.PointInFullZone(from);
            bool enters = !inside && zone.RayEnters(from, perception.DirFromPlayerFlat, dodgeMax);
            if (!inside && !enters && dist > threatRange) return;

            Vector3 intent = perception.DirFromPlayerFlat;
            Vector3 tangent = Vector3.Cross(Vector3.up, intent);
            if (tangent.sqrMagnitude < 0.01f) tangent = transform.right;
            tangent.Normalize();
            if (Vector3.Dot(transform.right, tangent) < 0f) tangent = -tangent;

            Vector3 best = intent;
            float bestLeave = -1f;
            float bestScore = float.NegativeInfinity;
            for (int i = -2; i <= 2; i++)
            {
                Vector3 d = (intent + tangent * (i * 0.45f)).normalized;
                if (d.sqrMagnitude < 0.01f) continue;
                float leave = zone.RayLeave(from, d, dodgeMax + 0.7f);
                if (inside && leave < 0f) continue;
                if (!inside && zone.RayEnters(from, d, dodgeMax)) continue;
                float score = Vector3.Dot(intent, d) * 1.6f;
                if (inside) score += (dodgeMax - leave) * 0.3f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = d;
                    bestLeave = leave;
                }
            }
            dir = best;
            travel = inside && bestLeave >= 0f
                ? Mathf.Clamp(bestLeave + dodgeMargin, dodgeMin, dodgeMax)
                : dodgeMin;
            usedZone = true;
        }
        else
        {
            Vector3 away = perception.DirFromPlayerFlat;
            Vector3 side = Vector3.Cross(Vector3.up, away);
            if (side.sqrMagnitude < 0.01f) side = transform.right;
            dir = (away + side.normalized * 0.55f).normalized;
            travel = dodgeMin;
        }

        if (stats != null)
        {
            if (!stats.HasEnough(dodgeStaminaCost)) return;
            stats.Spend(dodgeStaminaCost);
        }

        _nextDodgeTime = Time.time + dodgeCooldown;

        float spd = locomotion.PlanarSpeed;
        bool sliding = spd >= dodgeSlideSpeed;
        if (sliding || dist > leapDodgeRange || !usedZone)
        {
            Vector3 carry = locomotion.PlanarVel;
            Vector3 push = dir * sidestepImpulse;
            if (sliding && Vector3.Dot(carry, dir) < 0f)
                push += dir * spd * 0.35f;
            locomotion.AddImpulse(push);
        }
        else
        {
            locomotion.Leap(transform.position + dir * travel, dodgeArc);
        }
    }

    // ===================== Retreat: атаковал — отошёл, обходя соседа =====================

    private void EnterRetreat()
    {
        _phase = AttackPhase.Retreat;
        _retreatSideDir = PickSideStepDir();
    }

    private void TickRetreat(float dt)
    {
        float safe = ManeuverDistance;

        Vector3 outward = transform.position - perception.PlayerPos; outward.y = 0f;
        outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : transform.forward;

        Vector3 target = perception.PlayerPos + outward * safe
                        + _retreatSideDir * 1.5f + SeparationOffset();

        bool tired = stats != null && !stats.HasEnough(dodgeStaminaCost);
        // Длинный отход — Quad/run вперёд по курсу; у кольца — Biped и взгляд на игрока.
        float spd = tired ? walkSpeed : runSpeed;
        locomotion.MoveTo(target, spd, dt);
        if (locomotion.IsBiped)
            locomotion.FaceTowards(perception.PlayerPos, dt);

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

    /// <summary>Настроение Fear+Aggro. Без stats — Aggressive.</summary>
    private WerewolfStats.CombatMood CurrentMood =>
        stats != null ? stats.Mood : WerewolfStats.CombatMood.Aggressive;

    /// <summary>
    /// Дистанция удержания, пока волк НЕ бьёт.
    /// Ярость → minHoldDistance. Страх тянет кольцо к оружию+запас+fearHoldExtra.
    /// </summary>
    private float SafeDistance
    {
        get
        {
            float far = perception.PlayerWeaponRange + safetyMargin + fearHoldExtra * Fear01;
            if (AggroTier == WerewolfStats.AggressionTier.Rage)
                return minHoldDistance;
            float t = Mathf.Clamp01(0.28f + Fear01 * 0.85f - Aggression * 0.4f);
            if (CurrentMood == WerewolfStats.CombatMood.Skittish
                || CurrentMood == WerewolfStats.CombatMood.Fleeish)
                t = Mathf.Max(t, 0.9f);
            else if (FearTier >= WerewolfStats.FearTier.Afraid)
                t = Mathf.Max(t, 0.7f);
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
        _phaseTimer = Mathf.Lerp(disengageTime, disengageTime * 0.45f, Aggression);
        PickManeuverPoint();
    }

    private void PickManeuverPoint()
    {
        Vector3 p = perception.PlayerPos;
        Vector3 away = perception.DirFromPlayerFlat;
        Vector3 side = Vector3.Cross(Vector3.up, away);
        if (side.sqrMagnitude < 1e-4f) side = transform.right;
        side.Normalize();
        float sideSign = Random.value > 0.5f ? 1f : -1f;
        float r = Random.Range(ManeuverDistance, ManeuverDistance + 6f);
        Vector3 dir = (away * Random.Range(0.25f, 0.9f) + side * sideSign).normalized;
        _arcTarget = p + dir * r + SeparationOffset();
        _maneuverHold = Random.Range(maneuverHoldMin, maneuverHoldMax);
        RebuildPath(_arcTarget);
    }

    private void TickOrbit(float dt)
    {
        _phaseTimer -= dt;
        if (_phaseTimer <= 0f) { _phase = AttackPhase.Approach; return; }

        Vector3 wp = NextWaypoint(_arcTarget) + SeparationOffset();
        float distGoal = Flat(_arcTarget);
        float spd = perception.DistanceToPlayer > ManeuverDistance ? runSpeed : walkSpeed;
        bool arrived = locomotion.MoveTo(wp, spd, dt) || distGoal <= waypointArrive + 0.4f;
        if (locomotion.IsBiped) locomotion.FaceTowards(perception.PlayerPos, dt);

        if (!arrived) return;
        locomotion.FaceTowards(perception.PlayerPos, dt);
        _maneuverHold -= dt;
        if (_maneuverHold <= 0f) PickManeuverPoint();
    }

    private float Flat(Vector3 p)
    {
        Vector3 d = p - transform.position; d.y = 0f;
        return d.magnitude;
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
        Vector3 side = Vector3.Cross(Vector3.up, perception.PlayerForwardFlat);
        if (side.sqrMagnitude < 1e-4f) side = transform.right;
        side.Normalize();
        float sideSign = Vector3.Dot(transform.position - p, side) >= 0f ? 1f : -1f;
        if (_avoidFront) sideSign = Random.value > 0.35f ? -sideSign : sideSign;
        return p + side * sideSign * (MeleeRange * 0.85f)
             - perception.PlayerForwardFlat * (_avoidFront ? behindDistance * 0.4f : 0.2f);
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
