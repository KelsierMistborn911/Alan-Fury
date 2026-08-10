using UnityEngine;

public class CombatController3D : MonoBehaviour
{
    [Header("Ссылки")]
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public WeaponHitbox hitbox;

    [Header("Захват цели")]
    public float targetLockRange = 15f;
    [Tooltip("Дальше этой дистанции лок сбрасывается (перехват на ближайшего в targetLockRange). До неё держится метка.")]
    public float targetHoldRange = 30f;
    [Tooltip("Боевая зона: ближе — автолок, доворот корпуса и удары в цель. Дальше — только метка, всё по мыши.")]
    public float combatFaceRange = 10f;
    [Tooltip("Внутри этого радиуса лок перехватывает тот враг, что ближе к курсору.")]
    public float closeSwitchRange = 6f;
    [Tooltip("На сколько градусов кандидат должен выигрывать у текущей цели, чтобы отобрать лок. Больше — реже мигает.")]
    public float closeSwitchAngleMargin = 20f;
    [Tooltip("Сколько юнитов дистанции стоит 1° отклонения от курсора при автолоке. 0 — чисто ближайший.")]
    public float aimAnglePenalty = 0.1f;
    public LayerMask enemyLayers;
    [Tooltip("Дуга перед игроком (град.), в которой ищутся цели для Tab и автонаведения.")]
    [Range(0f, 360f)] public float aimConeAngle = 200f;

    [Header("Комбо")]
    [Tooltip("Аниматор игрока. Пусто — найдётся на объекте.")]
    public Animator animator;
    [Tooltip("Пауза между ударами, после которой серия сбрасывается (сек).")]
    public float comboWindow = 1f;

    [Header("Метка цели")]
    [Tooltip("Высота красного маркера над таргетом (м).")]
    public float targetMarkerHeight = 2.2f;
    [Tooltip("Размер маркера (м).")]
    public float targetMarkerSize = 0.35f;

    [Header("Управление боем")]
    [Tooltip("Клавиша блока (щит в левой руке обязателен). ПКМ.")]
    public KeyCode blockKey = KeyCode.Mouse1;
    [Tooltip("Клавиша вторичной функции оружия. У меча — колющий удар. Параметры самого удара лежат в WeaponData.")]
    public KeyCode secondaryKey = KeyCode.Q;
    [Tooltip("Клавиша парирования. Одно нажатие открывает короткое окно, удерживать не нужно.")]
    public KeyCode parryKey = KeyCode.F;

    [Header("Парирование")]
    [Tooltip("Длительность окна (сек). Урон в окне гасится целиком, оборона и стамина не тратятся.")]
    public float parryWindow = 0.25f;
    [Tooltip("Пауза после закрытия окна до следующего парирования (сек).")]
    public float parryCooldown = 0.25f;

    [Header("Удар под блоком")]
    [Tooltip("Замах удара с поднятым щитом (сек). 0 — мгновенный удар, как было раньше.")]
    public float blockAttackWindup = 0.15f;

    [Header("Выпад (вторичная атака)")]
    [Tooltip("Скорость выпада = текущая скорость движения × это. Шаг 4, бег 8, спринт 13 м/с. 0 — выпада нет.")]
    public float lungeSpeedMultiplier = 1.6f;
    [Tooltip("Насколько точно надо жать в сторону цели: 1 = строго в неё, 0.5 ≈ 60° допуска.")]
    [Range(0f, 1f)] public float lungeInputDot = 0.5f;

    [Header("Импульс движения в удар")]
    [Tooltip("Добавка к урону = скорость(м/с) × масса персонажа × этот коэффициент.")]
    public float movementDamageCoefficient = 0.02f;

    [Header("Комбо удар+уворот")]
    [Tooltip("Минимальная длительность замаха при ударе во время/сразу после уворота.")]
    public float dodgeAttackMinWindup = 0.08f;
    [Tooltip("Сколько секунд после конца уворота ещё считается комбо-окном.")]
    public float dodgeAttackBufferAfter = 0.2f;
    [Tooltip("Допуск (сек) для 'идеального' тайминга — удар точно на конце уворота.")]
    public float dodgeAttackPerfectTolerance = 0.08f;

    public bool IsWindingUp { get; private set; }
    public bool IsAttacking { get; private set; }
    public bool IsBlocking { get; private set; }
    public bool IsParrying { get; private set; }
    public bool IsCharging { get; private set; }
    public bool HasTarget => currentTarget != null;
    public float ChargePercent { get; private set; }
    public Transform currentTarget { get; private set; }

    // Цель, с которой реально можно взаимодействовать: ближе combatFaceRange.
    // Дальше — лок живёт только как метка, всё остальное работает по мыши.
    public Transform NearTarget
    {
        get
        {
            if (currentTarget == null) return null;
            Vector3 to = currentTarget.position - transform.position;
            to.y = 0f;
            return to.sqrMagnitude <= combatFaceRange * combatFaceRange ? currentTarget : null;
        }
    }

    // Цель для доворота корпуса: только во время действия (замах/удар/блок) и только внутри боевой зоны.
    public Transform ActiveAimTarget =>
        (IsCharging || IsAttacking || IsBlocking) ? (NearTarget != null ? NearTarget : _autoTarget) : null;

    private float stateTimer;
    private WeaponData currentWeapon;
    private float chargeStartTime;
    private bool isHoldingAttack;
    private int _combo;
    private float _comboExpire;
    private Transform _autoTarget; // авто-цель текущего замаха, сбрасывается после удара
    private PlayerMovement3D movement;

    private bool _isSecondaryAttack;    // текущая атака — вторичная функция оружия
    private bool _pendingAttack;        // удар ждёт истечения замаха
    private float _pendingFireTime;
    private bool _pendingIsBlockAttack; // отложенный удар начат с поднятым щитом
    private bool _dodgeAttackPerfectFlag;

    private float _parryEndTime;
    private float _parryReadyTime;

    enum AttackMoveMode { None, Stop, TurnStrike, SideStrike }
    private AttackMoveMode _attackMoveMode;

    private Transform _shiftSavedTarget; // цель на момент нажатия Shift — восстанавливается по отпусканию
    private Transform _targetMarker;     // красный ромб над головой текущей цели

    void Awake()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (animator == null) animator = GetComponent<Animator>();
        if (movement == null) movement = GetComponent<PlayerMovement3D>();

        CacheAnimParams();
    }

    // Какие параметры реально есть в контроллере. Аниматор ругается в консоль на каждую
    // запись несуществующего параметра, поэтому проверяем — клипы добавляются постепенно.
    private System.Collections.Generic.HashSet<string> _animParams;

    private void CacheAnimParams()
    {
        _animParams = new System.Collections.Generic.HashSet<string>();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters) _animParams.Add(p.name);
    }

    private void SetTrig(string name)
    {
        if (animator != null && _animParams != null && _animParams.Contains(name))
            animator.SetTrigger(name);
    }

    private void SetB(string name, bool value)
    {
        if (animator != null && _animParams != null && _animParams.Contains(name))
            animator.SetBool(name, value);
    }

    void Update()
    {
        // Парирование: одно нажатие открывает окно, стамина не тратится.
        if (IsParrying && Time.time >= _parryEndTime) IsParrying = false;
        if (Input.GetKeyDown(parryKey) && Time.time >= _parryReadyTime)
        {
            IsParrying = true;
            _parryEndTime = Time.time + parryWindow;
            _parryReadyTime = _parryEndTime + parryCooldown;
            SetTrig("Parry");
        }

        // Удержание таргета: цель ушла дальше targetHoldRange — перехват/сброс.
        if (HasTarget) MaintainLockTarget();

        // Любое действие (замах/удар/блок) само лочится на ближайшего в боевой зоне.
        if (IsCharging || IsAttacking || IsBlocking) TryAcquireCombatTarget();

        // Спринт сбрасывает таргет; на отпускании — восстановление старой цели или ближайшей.
        if (Input.GetKeyDown(KeyCode.LeftShift))
        {
            _shiftSavedTarget = currentTarget;
            ClearTarget();
        }
        if (Input.GetKeyUp(KeyCode.LeftShift))
        {
            RestoreOrAcquireTarget();
        }

        UpdateTargetMarker();

        // Блокировка цели (Tab)
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (HasTarget) ClearTarget();
            else if (!Input.GetKey(KeyCode.LeftShift)) currentTarget = FindNearestInCone();
        }

        // Отмена замаха пробелом — до отработки переката/уворота, без списания стамины.
        if (IsCharging && Input.GetKeyDown(KeyCode.Space))
        {
            CancelCharge();
            return;
        }

        // Активная атака — ждём конца
        if (IsAttacking)
        {
            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0f) EndAttack();
            return;
        }

        // Отложенный удар ждёт истечения замаха: комбо с уворотом или удар под блоком
        if (_pendingAttack)
        {
            IsWindingUp = true;
            if (Time.time >= _pendingFireTime) FirePendingAttack();
            return;
        }

        // Идёт заряд
        if (IsCharging)
        {
            IsWindingUp = true; // телеграф для ИИ: замах/удержание = угроза
            // Без цели в боевой зоне — поворот (мышь/WASD) может подобрать её прямо во время замаха.
            if (NearTarget == null)
            {
                Transform reTarget = FindClosestToDirection(GetPreferredAimDirection(), combatFaceRange);
                if (reTarget != null) _autoTarget = reTarget;
            }

            // Держим ту же кнопку, с которой начали замах: ЛКМ или вторичную функцию.
            bool held = _isSecondaryAttack ? Input.GetKey(secondaryKey) : Input.GetMouseButton(0);
            bool released = _isSecondaryAttack ? Input.GetKeyUp(secondaryKey) : Input.GetMouseButtonUp(0);

            if (held)
            {
                ChargePercent = Mathf.Clamp01((Time.time - chargeStartTime) / currentWeapon.chargeDuration);
            }

            if (released && isHoldingAttack)
            {
                ReleaseHeldAttack();
            }

            // Авто-атака при максимальном удержании
            if (currentWeapon.maxHoldTime > 0 && Time.time - chargeStartTime >= currentWeapon.maxHoldTime)
            {
                ReleaseHeldAttack();
            }
            return;
        }

        // Блок (ПКМ). Держит IsBlocking, но удары под блоком бьют, не снимая его.
        bool wantsBlock = Input.GetKey(blockKey) && loadout.HasShield();
        IsBlocking = wantsBlock;
        SetB("ShieldBlock", wantsBlock);

        if (wantsBlock)
        {
            if (Input.GetMouseButtonDown(0)) StartBlockAttack(false);
            else if (Input.GetKeyDown(secondaryKey)) StartBlockAttack(true);
            return;
        }

        // Удар во время/сразу после уворота (Alt) — отдельное укороченное комбо.
        if (Input.GetMouseButtonDown(0) && movement != null &&
            (movement.IsDodging || movement.TimeSinceDodgeEnd <= dodgeAttackBufferAfter))
        {
            StartDodgeAttack();
            return;
        }

        // Вторичная функция оружия (у меча — укол): замах удержанием, как у ЛКМ.
        if (Input.GetKeyDown(secondaryKey))
        {
            currentWeapon = loadout.GetMainWeapon();
            if (currentWeapon == null || !currentWeapon.hasSecondary) return;

            if (resources.HasStamina(currentWeapon.staminaCost * currentWeapon.secondaryStaminaMult))
            {
                _isSecondaryAttack = true;
                StartHoldAttack();
            }
            return;
        }

        // Начало атаки (ЛКМ)
        if (Input.GetMouseButtonDown(0))
        {
            currentWeapon = loadout.GetMainWeapon();
            if (currentWeapon == null) return;

            if (resources.HasStamina(currentWeapon.staminaCost))
            {
                _isSecondaryAttack = false;
                StartHoldAttack();
            }
        }
    }

    // Удар с поднятым щитом: короткий фиксированный замах вместо полноценного заряда.
    // secondary = вторичная функция оружия (у меча укол), иначе обычный удар.
    void StartBlockAttack(bool secondary)
    {
        currentWeapon = loadout.GetMainWeapon();
        if (currentWeapon == null) return;
        if (secondary && !currentWeapon.hasSecondary) return;

        float cost = currentWeapon.staminaCost * 0.5f * (secondary ? currentWeapon.secondaryStaminaMult : 1f);
        if (!resources.HasStamina(cost)) return;
        resources.SpendStamina(cost);

        _isSecondaryAttack = secondary;
        ChargePercent = currentWeapon.minChargePercent;
        SampleAttackMoveMode();

        if (blockAttackWindup <= 0f)
        {
            ExecuteAttack();
            return;
        }

        _pendingAttack = true;
        _pendingIsBlockAttack = true;
        _pendingFireTime = Time.time + blockAttackWindup;

        if (hitbox != null && hitbox.visual != null) hitbox.visual.ShowWindup();
    }

    // ЛКМ во время/сразу после уворота: замах короче, чем ближе к концу уворота, но не короче минимума.
    void StartDodgeAttack()
    {
        currentWeapon = loadout.GetMainWeapon();
        if (currentWeapon == null) return;
        if (!resources.HasStamina(currentWeapon.staminaCost)) return;
        resources.SpendStamina(currentWeapon.staminaCost);

        float progress = movement.IsDodging ? movement.DodgeProgress01 : 1f;
        float windup = Mathf.Max(dodgeAttackMinWindup,
            Mathf.Lerp(currentWeapon.chargeDuration, dodgeAttackMinWindup, progress));

        _pendingAttack = true;
        _pendingIsBlockAttack = false;
        _pendingFireTime = Time.time + windup;
        _isSecondaryAttack = false;
        SampleAttackMoveMode();
    }

    void FirePendingAttack()
    {
        _pendingAttack = false;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.HideWindup();

        // Удар под блоком: замах фиксированный, тайминг уворота не при чём.
        if (_pendingIsBlockAttack)
        {
            _pendingIsBlockAttack = false;
            ExecuteAttack();
            return;
        }

        // Идеальный тайминг — замах закончился точно на конце уворота (или сразу после).
        _dodgeAttackPerfectFlag = movement != null &&
            ((movement.IsDodging && movement.DodgeTimeRemaining <= dodgeAttackPerfectTolerance) ||
             (!movement.IsDodging && movement.TimeSinceDodgeEnd <= dodgeAttackPerfectTolerance));

        ChargePercent = currentWeapon.minChargePercent;
        ExecuteAttack();
    }

    void CancelCharge()
    {
        IsCharging = false;
        IsWindingUp = false;
        isHoldingAttack = false;
        ChargePercent = 0f;
        _autoTarget = null;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.HideWindup();
    }

    void StartHoldAttack()
    {
        IsCharging = true;
        isHoldingAttack = true;
        chargeStartTime = Time.time;
        ChargePercent = 0f;

        // Автонаведение: замах сам лочится на ближайшего в боевой зоне, если цели там нет.
        _autoTarget = null;
        TryAcquireCombatTarget();
        SampleAttackMoveMode();

        if (hitbox != null && hitbox.visual != null)
            hitbox.visual.ShowWindup();
    }

    // При зажатом Shift определяет режим удара по WASD: вперёд — стоп, назад — разворот+удар,
    // вбок — удар на ходу без остановки. Считывается один раз в момент начала атаки.
    void SampleAttackMoveMode()
    {
        _attackMoveMode = AttackMoveMode.None;
        if (!Input.GetKey(KeyCode.LeftShift)) return;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        if (Mathf.Abs(v) >= Mathf.Abs(h))
        {
            if (v > 0.1f) _attackMoveMode = AttackMoveMode.Stop;
            else if (v < -0.1f) _attackMoveMode = AttackMoveMode.TurnStrike;
        }
        else if (Mathf.Abs(h) > 0.1f)
        {
            _attackMoveMode = AttackMoveMode.SideStrike;
        }
    }

    void ApplyAttackMoveMode()
    {
        if (movement == null) return;
        switch (_attackMoveMode)
        {
            case AttackMoveMode.Stop:
                movement.StopHorizontalVelocity();
                break;
            case AttackMoveMode.TurnStrike:
                movement.StopHorizontalVelocity();
                movement.SnapRotationToTarget(transform.position + GetAttackDirection());
                break;
                // SideStrike: скорость не трогаем — удар на ходу.
        }
        _attackMoveMode = AttackMoveMode.None;
    }

    // Выпад вторичной атакой: если игрок жмёт в сторону цели — доворот корпуса на неё
    // и добавка скорости от текущей. Стоя (скорость ≈ 0) выпада нет.
    void TryLunge()
    {
        if (movement == null || lungeSpeedMultiplier <= 0f) return;

        Transform t = ActiveAimTarget;
        if (t == null) return;

        Vector3 toTarget = t.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.01f) return;
        toTarget.Normalize();

        Vector3 input = movement.InputDirection;
        if (input.sqrMagnitude < 0.01f) return;
        if (Vector3.Dot(input, toTarget) < lungeInputDot) return;

        movement.SnapRotationToTarget(t.position);
        movement.AddLungeSpeed(toTarget, movement.CurrentSpeed * lungeSpeedMultiplier);
    }

    // Добавка к урону от скорости движения: скорость × масса × коэффициент.
    // Идеальный тайминг комбо-удара с уворотом — берёт скорость уворота, иначе — Shift + текущая скорость.
    float ComputeMomentumBonus()
    {
        if (movement == null || resources == null) return 0f;

        float speed;
        if (_dodgeAttackPerfectFlag) speed = movement.DodgeSpeedValue;
        else if (Input.GetKey(KeyCode.LeftShift)) speed = movement.CurrentSpeed;
        else return 0f;

        return speed * resources.mass * movementDamageCoefficient;
    }

    void ReleaseHeldAttack()
    {
        if (!isHoldingAttack) return;

        IsCharging = false;
        isHoldingAttack = false;

        // Быстрый клик — атака с минимальным зарядом
        if (ChargePercent < currentWeapon.minChargePercent)
            ChargePercent = currentWeapon.minChargePercent;

        // Стамина масштабируется от заряда
        float cost = Mathf.Lerp(currentWeapon.staminaCost * 0.5f, currentWeapon.staminaCost, ChargePercent);
        if (_isSecondaryAttack) cost *= currentWeapon.secondaryStaminaMult;
        resources.SpendStamina(cost);

        ExecuteAttack();
    }

    void ExecuteAttack()
    {
        IsWindingUp = false;
        IsAttacking = true;
        stateTimer = currentWeapon.attackDuration;

        // Серия: удар в окне comboWindow продолжает комбо, иначе — сброс.
        _combo = Time.time <= _comboExpire ? _combo + 1 : 0;
        _comboExpire = Time.time + currentWeapon.attackDuration + comboWindow;

        if (_isSecondaryAttack && !string.IsNullOrEmpty(currentWeapon.secondaryTrigger))
            SetTrig(currentWeapon.secondaryTrigger);
        else
            SetTrig(_combo % 2 != 0 ? "AttackLeft" : "AttackRight");

        if (currentWeapon.isRanged)
        {
            ExecuteRangedAttack(ChargePercent > 0 ? ChargePercent : 1f);
        }
        else
        {
            if (hitbox != null)
            {
                float damageMult = Mathf.Lerp(0.7f, 1.5f, ChargePercent);
                float staggerMult = Mathf.Lerp(0.5f, 1.5f, ChargePercent);

                // Вторичная атака: под блоком длина обычная, без блока — удлинённая.
                bool sec = _isSecondaryAttack;
                float range = currentWeapon.attackRange * (sec && !IsBlocking ? currentWeapon.secondaryRangeMult : 1f);
                float radius = currentWeapon.attackRadius * (sec ? currentWeapon.secondaryRadiusMult : 1f);
                float cone = sec ? currentWeapon.secondaryConeHalfAngle : -1f;

                float damage = currentWeapon.damage * damageMult * (sec ? currentWeapon.secondaryDamageMult : 1f);
                damage += ComputeMomentumBonus();

                ApplyAttackMoveMode();
                if (sec && !IsBlocking) TryLunge();

                hitbox.Activate(
                    range,
                    radius,
                    currentWeapon.attackHeight,
                    currentWeapon.hitboxOffset,
                    GetAttackDirection(),
                    damage,
                    currentWeapon.staggerForce * staggerMult,
                    currentWeapon.targetLayers,
                    currentWeapon.attackDuration,
                    currentWeapon.tickInterval,
                    ChargePercent,
                    _combo,
                    cone
                );
            }
        }

        _isSecondaryAttack = false;
        _dodgeAttackPerfectFlag = false;
        ChargePercent = 0f;
    }

    void ExecuteRangedAttack(float chargePercent)
    {
        Vector3 spawnPos = transform.position + Vector3.up * 1.5f + GetAttackDirection() * 0.5f;
        Quaternion baseRotation = Quaternion.LookRotation(GetAttackDirection());
        // Исправлено: 0.5f как нижняя граница, не minChargePercent
        float dmgMult = Mathf.Lerp(0.5f, 1f, chargePercent);
        float spdMult = Mathf.Lerp(0.5f, 1f, chargePercent);

        for (int i = 0; i < currentWeapon.projectilesPerShot; i++)
        {
            float spread = currentWeapon.projectilesPerShot > 1
                ? Random.Range(-currentWeapon.spreadAngle, currentWeapon.spreadAngle) : 0f;
            Quaternion rot = baseRotation * Quaternion.Euler(0, spread, 0);
            GameObject proj = Instantiate(currentWeapon.projectilePrefab, spawnPos, rot);
            Projectile projScript = proj.GetComponent<Projectile>();
            if (projScript != null)
            {
                projScript.Initialize(
                    currentWeapon.damage * dmgMult,
                    currentWeapon.staggerForce * dmgMult,
                    currentWeapon.projectileSpeed * spdMult,
                    currentWeapon.projectileLifetime,
                    currentWeapon.targetLayers
                );
            }
        }
    }

    void EndAttack()
    {
        IsAttacking = false;
        IsWindingUp = false;
        _autoTarget = null; // направление замаха сбрасывается после удара
    }

    // Цель дальше радиуса удержания — берём ближайшего врага в targetLockRange, никого нет — сброс.
    // Внутри closeSwitchRange лок при этом может перехватить враг, который ближе к курсору.
    void MaintainLockTarget()
    {
        TryCloseSwitch();

        Vector3 to = currentTarget.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude <= targetHoldRange * targetHoldRange) return;

        currentTarget = FindNearestInRadius(targetLockRange);
    }

    // Началось/держится действие: цели в боевой зоне нет — лочимся на ближайшего внутри неё.
    void TryAcquireCombatTarget()
    {
        if (NearTarget != null) return;

        Transform near = FindPreferredTarget(combatFaceRange);
        if (near != null) currentTarget = near;
    }

    // Вблизи лок отбирает тот, кто ближе к курсору по углу. Запас closeSwitchAngleMargin
    // не даёт метке мигать между двумя врагами, стоящими рядом.
    void TryCloseSwitch()
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, closeSwitchRange, enemyLayers);
        if (enemies.Length == 0) return;

        Vector3 mouse = MouseDirection();
        Transform best = null;
        float bestAngle = float.MaxValue;

        foreach (Collider col in enemies)
        {
            if (col.transform == currentTarget) continue;

            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float angle = Vector3.Angle(mouse, to);
            if (angle < bestAngle) { bestAngle = angle; best = col.transform; }
        }
        if (best == null) return;

        Vector3 cur = currentTarget.position - transform.position;
        cur.y = 0f;
        if (bestAngle + closeSwitchAngleMargin < Vector3.Angle(mouse, cur))
            currentTarget = best;
    }

    Transform FindNearestInRadius(float radius)
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, radius, enemyLayers);
        Transform closest = null;
        float minDist = float.MaxValue;
        foreach (Collider col in enemies)
        {
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float dist = to.sqrMagnitude;
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        return closest;
    }

    // Направление наводки без Tab-таргета: WASD, если зажат — приоритет той стороны,
    // иначе мышь (используется и для смены цели поворотом во время замаха).
    Vector3 GetPreferredAimDirection()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        if ((Mathf.Abs(h) > 0.1f || Mathf.Abs(v) > 0.1f) && Camera.main != null)
        {
            Vector3 forward = Camera.main.transform.forward; forward.y = 0f; forward.Normalize();
            Vector3 right = Camera.main.transform.right; right.y = 0f; right.Normalize();
            Vector3 dir = forward * v + right * h;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }

        return MouseDirection();
    }

    // Направление на курсор в плоскости игрока.
    Vector3 MouseDirection()
    {
        if (Camera.main == null) return transform.forward;

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane ground = new Plane(Vector3.up, transform.position);
        if (ground.Raycast(ray, out float dist))
        {
            Vector3 dir = ray.GetPoint(dist) - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }
        return transform.forward;
    }

    // Смена цели поворотом во время замаха: приоритет стороне поворота, не дистанции.
    Transform FindClosestToDirection(Vector3 preferred, float radius)
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, radius, enemyLayers);
        Transform best = null;
        float bestAngle = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        foreach (Collider col in enemies)
        {
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;

            float angle = Vector3.Angle(preferred, to);
            if (angle < bestAngle) { bestAngle = angle; best = col.transform; }
        }
        return best;
    }

    // Ближайший враг в радиусе, 360°, с приоритетом тому, кто ближе к направлению наводки (WASD/мышь).
    // Вес стороны задаётся aimAnglePenalty: 1° отклонения = столько «юнитов» штрафа к дистанции.
    Transform FindPreferredTarget(float radius)
    {
        Vector3 preferred = GetPreferredAimDirection();
        Collider[] enemies = Physics.OverlapSphere(transform.position, radius, enemyLayers);
        Transform best = null;
        float bestScore = float.MaxValue;

        foreach (Collider col in enemies)
        {
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;

            float score = to.magnitude + Vector3.Angle(preferred, to) * aimAnglePenalty;
            if (score < bestScore) { bestScore = score; best = col.transform; }
        }
        return best;
    }

    // Ближайший враг в конусе aimConeAngle перед игроком (для Tab — без учёта стороны наводки).
    Transform FindNearestInCone()
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, targetLockRange, enemyLayers);
        Transform closest = null;
        float minDist = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        foreach (Collider col in enemies)
        {
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;

            float dist = to.sqrMagnitude;
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        return closest;
    }

    public void ClearTarget() => currentTarget = null;

    // Отпустили Shift: старая цель жива и в радиусе — возвращаем её, иначе берём ближайшую.
    void RestoreOrAcquireTarget()
    {
        currentTarget = IsValidRestoreTarget(_shiftSavedTarget) ? _shiftSavedTarget : FindNearestInCone();
        _shiftSavedTarget = null;
    }

    bool IsValidRestoreTarget(Transform t)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return false;
        Vector3 to = t.position - transform.position;
        to.y = 0f;
        return to.sqrMagnitude <= targetLockRange * targetLockRange;
    }

    // Красный ромб над головой цели: во время спринта таргет подавлен, но метка держится
    // на сохранённой цели, чтобы было видно, что захват восстановится после спринта.
    void UpdateTargetMarker()
    {
        Transform shown = currentTarget != null ? currentTarget : _shiftSavedTarget;
        if (shown == null)
        {
            if (_targetMarker != null) _targetMarker.gameObject.SetActive(false);
            return;
        }

        EnsureTargetMarker();
        _targetMarker.gameObject.SetActive(true);
        _targetMarker.position = shown.position + Vector3.up * targetMarkerHeight;

        if (Camera.main != null)
            _targetMarker.rotation = Quaternion.LookRotation(Camera.main.transform.forward) * Quaternion.Euler(0f, 0f, 45f);
    }

    void EnsureTargetMarker()
    {
        if (_targetMarker != null) return;

        var go = new GameObject("TargetLockMarker");
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        float half = targetMarkerSize * 0.5f;
        var mesh = new Mesh();
        mesh.vertices = new Vector3[] {
            new Vector3(-half, -half, 0f), new Vector3(half, -half, 0f),
            new Vector3(half, half, 0f), new Vector3(-half, half, 0f)
        };
        mesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 }; // двусторонний
        mesh.RecalculateNormals();
        mf.mesh = mesh;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.color = Color.red;
        mr.material = mat;

        _targetMarker = go.transform;
    }

    void OnDestroy()
    {
        if (_targetMarker != null) Destroy(_targetMarker.gameObject);
    }

    Vector3 GetAttackDirection()
    {
        Transform aim = NearTarget != null ? NearTarget : _autoTarget;
        if (aim != null)
        {
            // Таргет дальше, чем бьёт оружие, — целимся в ближайшего вместо него.
            float effectiveRange = currentWeapon != null
                ? currentWeapon.attackRange *
                  (_isSecondaryAttack && !IsBlocking ? currentWeapon.secondaryRangeMult : 1f)
                : targetLockRange;

            Vector3 diff = aim.position - transform.position;
            diff.y = 0f;
            if (diff.magnitude > effectiveRange)
            {
                Transform nearest = FindNearestInRadius(combatFaceRange);
                if (nearest != null) aim = nearest;
            }

            Vector3 dir = aim.position - transform.position;
            dir.y = 0f;
            return dir.normalized;
        }
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane ground = new Plane(Vector3.up, transform.position);
        if (ground.Raycast(ray, out float dist))
        {
            Vector3 point = ray.GetPoint(dist);
            Vector3 dir = point - transform.position;
            dir.y = 0f;
            return dir.normalized;
        }
        return transform.forward;
    }
}
