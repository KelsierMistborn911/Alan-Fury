using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Мозг АТАКИ оборотня (третий мозг). Живёт на префабе волка рядом с WerewolfBrain.
/// Реализует WerewolfPackManager.IPackAgent — менеджер выдаёт роль и токен, волк решает сам.
///
/// Роли (переключение мозгов — ЗДЕСЬ, менеджер в компоненты не лезет):
///   Attack   → включён этот мозг, WerewolfBrain выключен. Волк подходит и атакует.
///   Surround → включён WerewolfBrain (кружит), этот мозг выключен.
/// Два мозга разом никогда не работают.
///
/// В роли Attack — 4 фазы (Approach → Engage → Retreat → Orbit → снова Approach):
///   • Approach — подход. Цель считает сам: обычный — на игрока; avoidFront (второй) — НЕ спереди.
///     Патфайндер как раньше (не изменяется, только вызывается) + расталкивание от соседей.
///   • Engage — в зоне удара. Фронтовик без токена не бьёт (ждёт очереди); второй токен игнорирует.
///     Дистанция решает атаку: ближко → свип/серия, средне → особая, дальше → прыжок.
///   • Retreat — сразу после удара отход + шаг в сторону мимо ближайшего соседа.
///   • Orbit — кружит на дистанции; aggression сокращает время до нового захода.
///
/// Отложено: прыжок для рельефа, уход с воды −4, «вцепиться/тащить» у второго.
/// Реализовано: здоровье (WerewolfStats, IDamageable), опаска замаха и отскок от удара игрока.
/// </summary>
[RequireComponent(typeof(WerewolfPerception))]
[RequireComponent(typeof(WerewolfLocomotion))]
[RequireComponent(typeof(WerewolfCombat))]
public class WerewolfPackBrain : MonoBehaviour, WerewolfPackManager.IPackAgent
{
    [Header("Ссылки (авто-подхват, если пусто)")]
    public WerewolfPerception perception;
    public WerewolfLocomotion locomotion;
    public WerewolfCombat combat;
    [Tooltip("Мозг окружения. Включается в роли Surround, гасится в Attack.")]
    public WerewolfBrain surroundBrain;
    [Tooltip("Сетка пути. Пусто — найдётся на сцене.")]
    public Pathfinder pathfinder;
    [Tooltip("Параметры оборотня (агрессия). Пусто — найдётся на волке.")]
    public WerewolfStats stats;

    [Header("Скорость")]
    [Tooltip("Скорость бега к игроку (м/с). ~галоп локомоции.")]
    public float runSpeed = 8f;

    [Header("Дистанции атак (м)")]
    [Tooltip("Ближе этого — обычный удар/серия.")]
    public float meleeRange = 2f;
    [Tooltip("До этого достаёт особая атака.")]
    public float specialReach = 3.5f;
    [Tooltip("С этой дистанции можно прыгать в игрока.")]
    public float jumpRange = 6f;

    [Header("Заход не спереди (для второго)")]
    [Tooltip("На сколько метров позади игрока целиться второму волку.")]
    public float behindDistance = 2f;

    [Header("Фронт по лучу альфа→игрок")]
    [Tooltip("0 = целиться прямо на игрока; 1 = держаться на линии между игроком и альфой. Только для фронтовиков (не второго).")]
    [Range(0f, 1f)] public float frontLineBias = 0.6f;

    [Header("Агрессия от событий")]
    [Tooltip("Сколько агрессии (0..100) добавляется за одно своё попадание.")]
    public float aggroPerHit = 5f;

    [Header("Путь")]
    [Tooltip("Как часто перестраивать путь (сек). Реже = дешевле, но менее отзывчиво.")]
    public float repathInterval = 0.4f;
    [Tooltip("Насколько близко подойти к вейпоинту, чтобы взять следующий (м).")]
    public float waypointArrive = 1.2f;

    [Header("После атаки: отход и облёт")]
    [Tooltip("Дистанция отхода от игрока сразу после атаки (м).")]
    public float retreatDistance = 3.5f;
    [Tooltip("Базовое время облёта перед новым заходом (сек); aggression сокращает его (см. EnterOrbit).")]
    public float disengageTime = 1.5f;
    [Tooltip("Угловая скорость облёта вокруг игрока (град/сек).")]
    public float orbitAngularSpeed = 45f;

    [Header("Дистанция удержания по агрессии")]
    [Tooltip("Радиус облёта при максимальной агрессии (м) — почти дистанция удара.")]
    public float holdDistanceAggressive = 1.5f;
    [Tooltip("Радиус облёта при нулевой агрессии (м) — безопасный манёвр.")]
    public float holdDistanceCautious = 4.5f;
    [Tooltip("Случайный разброс радиуса облёта (±м), чтобы не липнуть к окружности.")]
    public float orbitRadiusJitter = 0.8f;
    [Tooltip("Как часто менять разброс радиуса (сек).")]
    public float jitterInterval = 0.7f;

    [Header("Осторожность: удар после атаки игрока")]
    [Tooltip("Ниже этой агрессии (0..1) волк не бьёт, пока игрок замахивается/атакует.")]
    [Range(0f, 1f)] public float cautionThreshold = 0.5f;
    [Tooltip("Окно после конца атаки игрока, когда осторожный волк охотно бьёт (сек).")]
    public float opportunityWindow = 0.8f;

    [Header("Реакция на игрока (опаска замаха / уворот)")]
    [Tooltip("Ближе этой дистанции волк реагирует на замах/удар игрока (м).")]
    public float playerThreatRange = 4f;
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
    [Tooltip("Слой(и) других волков стаи — на нём ищутся соседи рядом. Назначь слой волка (0 = выключено).")]
    public LayerMask packAgentLayers;
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

    private WerewolfPackManager _manager;

    // ===================== IPackAgent =====================
    public Transform Transform => transform;
    public bool IsAlive => stats == null || stats.IsAlive;

    public void SetRole(WerewolfPackManager.PackRole role, bool avoidFront)
    {
        _role = role;
        _avoidFront = avoidFront;

        if (role == WerewolfPackManager.PackRole.Attack)
        {
            if (surroundBrain != null) surroundBrain.enabled = false;
            _phase = AttackPhase.Approach;   // начинаем заход заново
            enabled = true;                 // этот мозг работает
        }
        else // Surround
        {
            if (surroundBrain != null) surroundBrain.enabled = true;
            _path.Clear();
            enabled = false;                // отдаём управление WerewolfBrain
        }
    }

    public void SetAttackToken(bool hasToken) => _hasToken = hasToken;

    // ===================== Жизненный цикл =====================

    void Awake()
    {
        if (perception == null) perception = GetComponent<WerewolfPerception>();
        if (locomotion == null) locomotion = GetComponent<WerewolfLocomotion>();
        if (combat == null) combat = GetComponent<WerewolfCombat>();
        if (surroundBrain == null) surroundBrain = GetComponent<WerewolfBrain>();
        if (pathfinder == null) pathfinder = FindObjectOfType<Pathfinder>();
        if (stats == null) stats = GetComponent<WerewolfStats>();
        if (stats == null)
            Debug.LogWarning("WerewolfPackBrain: не найден WerewolfStats — агрессия считается как 0.5.");

        if (combat != null) combat.OnHitLanded += HandleHitLanded;
        if (stats != null) stats.OnDeath += HandleDeath;

        _manager = WerewolfPackManager.Instance;
        if (_manager != null)
        {
            _manager.Register(this);
        }
        else
        {
            Debug.LogWarning("WerewolfPackBrain: нет WerewolfPackManager на сцене — остаюсь в окружении.");
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

    // Смерть: гасим мозги и бой, тело остаётся лежать (заглушка — наклон набок).
    // Локомоция остаётся включённой — она держит гравитацию и дотормаживает тело.
    private void HandleDeath()
    {
        if (_manager != null) { _manager.Unregister(this); _manager = null; }
        if (combat != null) combat.enabled = false;
        if (surroundBrain != null) surroundBrain.enabled = false;
        var stalker = GetComponent<AlphaStalker>();
        if (stalker != null) stalker.enabled = false;
        enabled = false;
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 90f); // тело набок
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

        // Детект начала/конца удара игрока (фронты перехода IsAttacking).
        bool playerAttacking = perception.PlayerIsAttacking;
        bool attackStarted = playerAttacking && !_playerWasAttacking;
        bool attackEnded = !playerAttacking && _playerWasAttacking;
        _playerWasAttacking = playerAttacking;
        if (attackEnded) _opportunityUntil = Time.time + opportunityWindow;

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
        if (perception.DistanceToPlayer <= jumpRange) { _phase = AttackPhase.Engage; return; }
        Approach(dt);
    }

    // ===================== Engage: в зоне удара =====================

    private void TickEngage(float dt)
    {
        float dist = perception.DistanceToPlayer;
        if (dist > jumpRange) { _phase = AttackPhase.Approach; return; } // игрок ушёл — снова подходим

        locomotion.FaceTowards(perception.PlayerPos, dt);

        // Опаска замаха: игрок заряжает удар, мы близко — пятимся, не атакуем.
        if (perception.PlayerIsCharging && dist < playerThreatRange)
        {
            Vector3 away = perception.DirFromPlayerFlat;
            Vector3 backTarget = perception.PlayerPos + away * (playerThreatRange + 1f) + SeparationOffset();
            locomotion.MoveTo(backTarget, runSpeed, dt);
            return;
        }

        // Осторожный не бьёт, пока игрок машет/заряжает — ждёт окно сразу после его удара.
        bool playerBusy = perception.PlayerIsAttacking || perception.PlayerIsCharging;
        bool opportunity = Time.time < _opportunityUntil;
        if (!(playerBusy && Aggression < cautionThreshold && !opportunity))
        {
            bool mayAttack = _avoidFront || _hasToken;
            if (mayAttack && TryAttackByDistance(dist)) { EnterRetreat(); return; }
        }

        // Удар сейчас не вышел (нет токена/стамины/кулдаун или не достаём) — не стоим:
        // подрезаем по дуге к дистанции мили, маневрируя (удар выйдет "мимоходом").
        Vector3 p = perception.PlayerPos;
        Vector3 dir = transform.position - p; dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;

        float targetRadius = Mathf.Max(meleeRange * 0.9f, dist - 1.5f); // радиус стягивается к миле
        Vector3 rotated = Quaternion.AngleAxis(30f * _orbitDir, Vector3.up) * dir;
        Vector3 target = p + rotated * targetRadius + SeparationOffset();
        locomotion.MoveTo(target, runSpeed, dt);
    }

    private bool TryAttackByDistance(float dist)
    {
        if (dist <= meleeRange) return combat.TrySwipe();
        if (dist <= specialReach) return combat.TrySpecial();
        return combat.TryJump(); // между specialReach и jumpRange
    }

    // Уворот от замаха/удара игрока: вплотную — прыжок вбок/назад, чуть дальше — быстрый отшаг.
    private void TryDodge()
    {
        float dist = perception.DistanceToPlayer;
        if (Time.time < _nextDodgeTime) return;
        if (dist > playerThreatRange) return;

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
        Vector3 outward = transform.position - perception.PlayerPos; outward.y = 0f;
        outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : transform.forward;

        Vector3 target = perception.PlayerPos + outward * retreatDistance
                        + _retreatSideDir * 1.5f + SeparationOffset();

        locomotion.FaceTowards(perception.PlayerPos, dt); // пятится, но смотрит на игрока
        locomotion.MoveTo(target, runSpeed, dt);

        if (perception.DistanceToPlayer >= retreatDistance * 0.9f) EnterOrbit();
    }

    // Сторона обхода — мимо ближайшего соседа (если есть), иначе случайно.
    private Vector3 PickSideStepDir()
    {
        Vector3 right = transform.right;
        if (packAgentLayers.value != 0)
        {
            Collider[] hits = Physics.OverlapSphere(transform.position, separationRadius * 1.5f,
                                                    packAgentLayers, QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (h.transform == transform) continue;
                float side = Vector3.Dot(h.transform.position - transform.position, right);
                return side > 0f ? -right : right; // сосед справа — уходим влево, и наоборот
            }
        }
        return Random.value > 0.5f ? right : -right;
    }

    // ===================== Orbit: кружит перед новым заходом =====================

    private float Aggression => stats != null ? stats.Aggression01 : 0.5f;

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
        float radius = Mathf.Lerp(holdDistanceCautious, holdDistanceAggressive, Aggression) + _radiusJitter;
        Vector3 dir = toSelf.sqrMagnitude > 1e-4f ? toSelf.normalized : transform.forward;
        Vector3 rotated = Quaternion.AngleAxis(orbitAngularSpeed * _orbitDir * dt, Vector3.up) * dir;

        Vector3 target = perception.PlayerPos + rotated * radius + SeparationOffset();
        locomotion.FaceTowards(perception.PlayerPos, dt);
        locomotion.MoveTo(target, runSpeed, dt);

        if (_phaseTimer <= 0f) _phase = AttackPhase.Approach;
    }

    // ===================== Расталкивание (не бежать кучей) =====================

    private Vector3 SeparationOffset()
    {
        if (packAgentLayers.value == 0) return Vector3.zero;
        Collider[] hits = Physics.OverlapSphere(transform.position, separationRadius,
                                                packAgentLayers, QueryTriggerInteraction.Ignore);
        Vector3 push = Vector3.zero;
        foreach (var h in hits)
        {
            if (h.transform == transform) continue;
            Vector3 away = transform.position - h.transform.position; away.y = 0f;
            float dist = away.magnitude;
            if (dist < 0.05f) continue; // почти в той же точке — пропускаем
            push += away.normalized * Mathf.Max(0f, separationRadius - dist);
        }
        return push * separationStrength;
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
                Vector3 frontPoint = p + toAlpha.normalized * meleeRange; // точка на стороне альфы
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
