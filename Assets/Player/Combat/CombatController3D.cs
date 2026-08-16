using UnityEngine;

public class CombatController3D : MonoBehaviour
{
    public enum CombatStance { Neutral, High, Low }
    public enum AttackForm { SlashLeft, SlashRight, Thrust }

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

    [Header("Комбо / стойки")]
    [Tooltip("Аниматор игрока. Пусто — найдётся на объекте.")]
    public Animator animator;
    [Tooltip("Пауза между ударами, после которой серия сбрасывается (сек).")]
    public float comboWindow = 1f;
    [Tooltip("Длительность High/Low стойки после удара (сек).")]
    public float stanceDuration = 2f;
    [Tooltip("Множитель длительности атаки в подходящей стойке (<1 = быстрее).")]
    [Range(0.5f, 1f)] public float stanceSpeedBonus = 0.85f;
    [Tooltip("Порог ChargePercent, после которого атака считается тяжёлой.")]
    [Range(0.3f, 0.9f)] public float heavyChargeThreshold = 0.55f;
    [Tooltip("Дистанция, выше которой МОЖЕТ выпасть Thrust (и то не всегда). Ниже — только slash.")]
    public float thrustPreferDistance = 3.6f;
    [Tooltip("Шанс укола, когда цель дальше thrustPreferDistance (0–1). Остальное — slash.")]
    [Range(0f, 1f)] public float thrustChanceWhenFar = 0.28f;

    [Header("Метка цели")]
    [Tooltip("Высота красного маркера над таргетом (м).")]
    public float targetMarkerHeight = 2.2f;
    [Tooltip("Размер маркера (м).")]
    public float targetMarkerSize = 0.35f;

    [Header("Управление боем")]
    [Tooltip("Клавиша блока (щит в левой руке обязателен). ПКМ.")]
    public KeyCode blockKey = KeyCode.Mouse1;
    [Tooltip("Клавиша парирования. Одно нажатие открывает короткое окно, удерживать не нужно.")]
    public KeyCode parryKey = KeyCode.F;
    [Tooltip("Убрать / достать оружие (мгновенно по тапу, если нужно).")]
    public KeyCode sheathKey = KeyCode.R;
    [Tooltip("Зажать ≥ peaceHoldDuration → принудительно мирный режим + анимация выхода из боя.")]
    public KeyCode peaceHoldKey = KeyCode.Alpha1;
    [Tooltip("Сколько держать peaceHoldKey (сек), чтобы выйти в мирный режим.")]
    public float peaceHoldDuration = 1.5f;
    [Tooltip("После удара/врага: сколько секунд держать боевой режим, если врагов уже нет. Сброс сразу по удержанию 1.")]
    public float combatLingerSeconds = 15f;

    [Header("Парирование")]
    [Tooltip("Длительность окна (сек). Урон в окне гасится целиком, оборона и стамина не тратятся.")]
    public float parryWindow = 0.25f;
    [Tooltip("Пауза после закрытия окна до следующего парирования (сек).")]
    public float parryCooldown = 0.25f;

    [Header("Удар под блоком")]
    [Tooltip("Замах удара с поднятым щитом (сек). 0 — мгновенный удар.")]
    public float blockAttackWindup = 0.15f;
    [Tooltip("Множитель дистанции атаки из-под щита.")]
    [Range(0.3f, 1f)] public float blockAttackRangeMult = 0.7f;
    [Tooltip("Множитель стоимости стамины атаки из-под щита.")]
    public float blockAttackStaminaMult = 1.35f;

    [Header("Выпад / импульс")]
    [Tooltip("Скорость выпада = текущая скорость движения × это. 0 — выпада нет.")]
    public float lungeSpeedMultiplier = 1.6f;
    [Range(0f, 1f)] public float lungeInputDot = 0.5f;
    [Tooltip("Добавка к урону = скорость(м/с) × масса персонажа × этот коэффициент.")]
    public float movementDamageCoefficient = 0.02f;

    [Header("Комбо удар+уворот")]
    public float dodgeAttackMinWindup = 0.08f;
    public float dodgeAttackBufferAfter = 0.2f;
    public float dodgeAttackPerfectTolerance = 0.08f;

    // --- Публичное состояние ---
    public bool IsWindingUp { get; private set; }
    public bool IsAttacking { get; private set; }
    public bool IsBlocking { get; private set; }
    public bool IsParrying { get; private set; }
    public bool IsCharging { get; private set; }
    /// <summary>Оружие в руках. false = убрано, мирная стойка/анимки. Атака/блок автоматически достают.</summary>
    public bool IsArmed { get; private set; } = true;
    /// <summary>Принудительный мирный режим (удержание 1 ≥ 1.5с). Снимается атакой/Draw.</summary>
    public bool ForcePeace { get; private set; }
    /// <summary>Боевой режим для анимаций: удар/блок/заряд/враг рядом, либо linger после этого. False при ForcePeace.</summary>
    public bool IsInCombat { get; private set; }
    public bool HasTarget => currentTarget != null;
    public float ChargePercent { get; private set; }
    public bool IsHeavyReady => IsCharging && ChargePercent >= heavyChargeThreshold;
    public CombatStance CurrentStance { get; private set; } = CombatStance.Neutral;
    public Transform currentTarget { get; private set; }

    public Transform NearTarget
    {
        get
        {
            if (!IsValidEnemy(currentTarget)) return null;
            Vector3 to = currentTarget.position - transform.position;
            to.y = 0f;
            return to.sqrMagnitude <= combatFaceRange * combatFaceRange ? currentTarget : null;
        }
    }

    public Transform ActiveAimTarget
    {
        get
        {
            if (!(IsCharging || IsBlocking)) return null;
            if (NearTarget != null) return NearTarget;
            return IsValidEnemy(_autoTarget) ? _autoTarget : null;
        }
    }

    // --- Внутреннее ---
    private float stateTimer;
    private WeaponData currentWeapon;
    private float chargeStartTime;
    private bool isHoldingAttack;
    private int _combo;
    private float _comboExpire;
    private Transform _autoTarget;
    private PlayerMovement3D movement;

    private bool _pendingAttack;
    private float _pendingFireTime;
    private bool _pendingIsBlockAttack;
    private bool _dodgeAttackPerfectFlag;
    private bool _fromLowStance;          // текущая атака начата из Low
    private bool _isHeavyAttack;          // текущая атака — тяжёлая (по заряду)
    private AttackForm _lastForm = AttackForm.SlashRight;
    private float _stanceTimer;

    private float _parryEndTime;
    private float _parryReadyTime;

    enum AttackMoveMode { None, Stop, TurnStrike, SideStrike }
    private AttackMoveMode _attackMoveMode;

    private Transform _shiftSavedTarget;
    private Transform _targetMarker;

    private System.Collections.Generic.HashSet<string> _animParams;

    void Awake()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (animator == null) animator = GetComponent<Animator>();
        if (movement == null) movement = GetComponent<PlayerMovement3D>();
        CacheAnimParams();
        SetB("Armed", IsArmed);
    }

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
        // Стойки: тикаем таймер
        if (CurrentStance != CombatStance.Neutral)
        {
            _stanceTimer -= Time.deltaTime;
            if (_stanceTimer <= 0f)
            {
                CurrentStance = CombatStance.Neutral;
                // В нейтрали можно будет ускорять реген (пока через ресурсы позже)
            }
        }

        // Удержание 1 ≥ 1.5с → мирный. Боевой режим + linger 15с без врагов.
        TickPeaceHold();
        TickCombatState();

        // Парирование — только с оружием в руках
        if (IsParrying && Time.time >= _parryEndTime) IsParrying = false;
        if (IsArmed && Input.GetKeyDown(parryKey) && Time.time >= _parryReadyTime)
        {
            IsParrying = true;
            _parryEndTime = Time.time + parryWindow;
            _parryReadyTime = _parryEndTime + parryCooldown;
            SetTrig("Parry");
        }

        if (HasTarget) MaintainLockTarget();
        if (IsCharging || IsBlocking) TryAcquireCombatTarget();

        if (Input.GetKeyDown(KeyCode.LeftShift))
        {
            _shiftSavedTarget = currentTarget;
            ClearTarget();
        }
        if (Input.GetKeyUp(KeyCode.LeftShift))
            RestoreOrAcquireTarget();

        UpdateTargetMarker();

        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (HasTarget) ClearTarget();
            else if (!Input.GetKey(KeyCode.LeftShift)) currentTarget = FindNearestInCone();
        }

        if (IsCharging && Input.GetKeyDown(KeyCode.Space))
        {
            CancelCharge();
            return;
        }

        if (IsAttacking)
        {
            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0f) EndAttack();
            return;
        }

        if (_pendingAttack)
        {
            IsWindingUp = true;
            if (Time.time >= _pendingFireTime) FirePendingAttack();
            return;
        }

        if (IsCharging)
        {
            IsWindingUp = true;
            if (NearTarget == null)
            {
                Transform reTarget = FindClosestToDirection(GetPreferredAimDirection(), combatFaceRange);
                _autoTarget = reTarget; // null, если живых нет
            }

            bool held = Input.GetMouseButton(0);
            bool released = Input.GetMouseButtonUp(0);

            if (held && currentWeapon != null)
                ChargePercent = Mathf.Clamp01((Time.time - chargeStartTime) / currentWeapon.chargeDuration);

            if (released && isHoldingAttack)
                ReleaseHeldAttack();

            if (currentWeapon != null && currentWeapon.maxHoldTime > 0
                && Time.time - chargeStartTime >= currentWeapon.maxHoldTime)
                ReleaseHeldAttack();
            return;
        }

        // Блок — только с оружием в руках
        bool wantsBlock = IsArmed && Input.GetKey(blockKey) && loadout != null && loadout.HasShield();
        IsBlocking = wantsBlock;
        SetB("ShieldBlock", wantsBlock);

        if (wantsBlock)
        {
            if (Input.GetMouseButtonDown(0)) StartBlockAttack();
            return;
        }

        // Удар после уворота
        if (Input.GetMouseButtonDown(0) && movement != null &&
            (movement.IsDodging || movement.TimeSinceDodgeEnd <= dodgeAttackBufferAfter))
        {
            StartDodgeAttack();
            return;
        }

        // Обычная атака (только ЛКМ, Q убран)
        if (Input.GetMouseButtonDown(0))
        {
            currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
            if (currentWeapon == null) return;
            if (resources != null && resources.HasStamina(currentWeapon.staminaCost * 0.5f))
                StartHoldAttack();
        }
    }

    void StartBlockAttack()
    {
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return;

        float cost = currentWeapon.staminaCost * 0.5f * blockAttackStaminaMult;
        if (resources == null || !resources.HasStamina(cost)) return;
        resources.SpendStamina(cost);

        DrawWeapon();
        ChargePercent = currentWeapon.minChargePercent;
        _isHeavyAttack = false;
        _fromLowStance = CurrentStance == CombatStance.Low;
        SampleAttackMoveMode();

        if (blockAttackWindup <= 0f)
        {
            ExecuteAttack(fromBlock: true);
            return;
        }

        _pendingAttack = true;
        _pendingIsBlockAttack = true;
        _pendingFireTime = Time.time + blockAttackWindup;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.ShowWindup();
    }

    void StartDodgeAttack()
    {
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return;
        if (resources == null || !resources.HasStamina(currentWeapon.staminaCost)) return;
        resources.SpendStamina(currentWeapon.staminaCost);

        DrawWeapon();
        float progress = movement.IsDodging ? movement.DodgeProgress01 : 1f;
        float windup = Mathf.Max(dodgeAttackMinWindup,
            Mathf.Lerp(currentWeapon.chargeDuration, dodgeAttackMinWindup, progress));

        _pendingAttack = true;
        _pendingIsBlockAttack = false;
        _pendingFireTime = Time.time + windup;
        _isHeavyAttack = false;
        _fromLowStance = CurrentStance == CombatStance.Low;
        SampleAttackMoveMode();
    }

    void FirePendingAttack()
    {
        _pendingAttack = false;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.HideWindup();

        if (_pendingIsBlockAttack)
        {
            _pendingIsBlockAttack = false;
            ExecuteAttack(fromBlock: true);
            return;
        }

        _dodgeAttackPerfectFlag = movement != null &&
            ((movement.IsDodging && movement.DodgeTimeRemaining <= dodgeAttackPerfectTolerance) ||
             (!movement.IsDodging && movement.TimeSinceDodgeEnd <= dodgeAttackPerfectTolerance));

        ChargePercent = currentWeapon != null ? currentWeapon.minChargePercent : 0.3f;
        ExecuteAttack(fromBlock: false);
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
        DrawWeapon();
        IsCharging = true;
        isHoldingAttack = true;
        chargeStartTime = Time.time;
        ChargePercent = 0f;
        _fromLowStance = CurrentStance == CombatStance.Low;
        _autoTarget = null;
        TryAcquireCombatTarget();
        SampleAttackMoveMode();
        if (hitbox != null && hitbox.visual != null)
            hitbox.visual.ShowWindup();
    }

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
        }
        _attackMoveMode = AttackMoveMode.None;
    }

    void TryLunge()
    {
        if (movement == null || lungeSpeedMultiplier <= 0f) return;

        Transform t = NearTarget != null ? NearTarget : currentTarget;
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

        if (currentWeapon == null) return;

        if (ChargePercent < currentWeapon.minChargePercent)
            ChargePercent = currentWeapon.minChargePercent;

        _isHeavyAttack = ChargePercent >= heavyChargeThreshold;

        float cost = Mathf.Lerp(currentWeapon.staminaCost * 0.5f, currentWeapon.staminaCost, ChargePercent);
        if (resources != null) resources.SpendStamina(cost);

        ExecuteAttack(fromBlock: false);
    }

    void ExecuteAttack(bool fromBlock)
    {
        IsWindingUp = false;
        IsAttacking = true;

        // Длительность с учётом стойки
        float dur = currentWeapon != null ? currentWeapon.attackDuration : 0.2f;
        bool stanceMatch = (CurrentStance == CombatStance.High && !_isHeavyAttack)
                        || (CurrentStance == CombatStance.Low && (_isHeavyAttack || _fromLowStance));
        if (stanceMatch) dur *= stanceSpeedBonus;
        stateTimer = dur;

        _combo = Time.time <= _comboExpire ? _combo + 1 : 0;
        _comboExpire = Time.time + dur + comboWindow;

        // Выбор формы атаки
        AttackForm form = ChooseAttackForm();
        _lastForm = form;
        string trig = form switch
        {
            AttackForm.Thrust => "Thrust",
            AttackForm.SlashLeft => "AttackLeft",
            _ => "AttackRight"
        };
        SetTrig(trig);

        if (currentWeapon != null && currentWeapon.isRanged)
        {
            ExecuteRangedAttack(ChargePercent > 0 ? ChargePercent : 1f);
        }
        else if (hitbox != null && currentWeapon != null)
        {
            float damageMult;
            float staggerMult;

            if (_isHeavyAttack)
            {
                damageMult = Mathf.Lerp(1.1f, 1.5f, (ChargePercent - heavyChargeThreshold) / (1f - heavyChargeThreshold));
                staggerMult = Mathf.Lerp(1.0f, 1.5f, ChargePercent);
            }
            else if (_fromLowStance)
            {
                // Быстрый удар из Low: сильнее обычного light, слабее heavy
                damageMult = 1.05f;
                staggerMult = 0.9f;
            }
            else
            {
                damageMult = Mathf.Lerp(0.7f, 1.0f, ChargePercent);
                staggerMult = Mathf.Lerp(0.5f, 1.0f, ChargePercent);
            }

            float range = currentWeapon.attackRange;
            float radius = currentWeapon.attackRadius;
            float cone = -1f;

            if (form == AttackForm.Thrust)
            {
                range *= 1.25f;
                radius *= 0.4f;
                cone = 18f;
            }

            if (fromBlock)
                range *= blockAttackRangeMult;

            float damage = currentWeapon.damage * damageMult;
            damage += ComputeMomentumBonus();

            ApplyAttackMoveMode();

            // Подшаг: если нет сильного ввода — лёгкий импульс в сторону удара
            ApplyFootworkStep(form);

            if (_isHeavyAttack && !fromBlock)
                TryLunge();

            hitbox.Activate(
                range,
                radius,
                currentWeapon.attackHeight,
                currentWeapon.hitboxOffset,
                GetAttackDirection(),
                damage,
                currentWeapon.staggerForce * staggerMult,
                currentWeapon.targetLayers,
                dur,
                currentWeapon.tickInterval,
                ChargePercent,
                _combo,
                cone
            );
        }

        // Переход стойки после удара
        if (_isHeavyAttack)
            EnterStance(CombatStance.Low);
        else
            EnterStance(CombatStance.High);

        _dodgeAttackPerfectFlag = false;
        ChargePercent = 0f;
        _isHeavyAttack = false;
        _fromLowStance = false;
    }

    AttackForm ChooseAttackForm()
    {
        // Укол — редкий: только если цель заметно дальше обычной дистанции рубящего
        // и не два укола подряд. Даже тогда — с шансом thrustChanceWhenFar.
        Transform aim = NearTarget != null ? NearTarget : _autoTarget;
        if (aim != null && _lastForm != AttackForm.Thrust)
        {
            Vector3 to = aim.position - transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            float range = currentWeapon != null ? currentWeapon.attackRange : 2f;
            // Дальше max(порог, range * 1.35) — кандидат на укол
            float thrustDist = Mathf.Max(thrustPreferDistance, range * 1.35f);
            if (dist >= thrustDist && Random.value < thrustChanceWhenFar)
                return AttackForm.Thrust;
        }

        // Основное — чередование Left/Right
        if (_lastForm == AttackForm.SlashRight)
            return AttackForm.SlashLeft;
        if (_lastForm == AttackForm.SlashLeft)
            return AttackForm.SlashRight;

        // После Thrust или старта — Right
        return AttackForm.SlashRight;
    }

    void ApplyFootworkStep(AttackForm form)
    {
        if (movement == null) return;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        bool hasInput = Mathf.Abs(h) > 0.15f || Mathf.Abs(v) > 0.15f;

        if (hasInput)
        {
            // Игрок сам задаёт направление ног — ничего не форсим
            return;
        }

        // Нет ввода → небольшой шаг в сторону удара
        Vector3 attackDir = GetAttackDirection();
        if (attackDir.sqrMagnitude < 0.01f) return;

        // Лёгкий импульс вперёд по направлению атаки (подшаг)
        if (movement != null)
            movement.AddLungeSpeed(attackDir, 2.5f); // маленькая фиксированная скорость
    }

    void EnterStance(CombatStance s)
    {
        CurrentStance = s;
        _stanceTimer = stanceDuration;
    }

    void ExecuteRangedAttack(float chargePercent)
    {
        Vector3 spawnPos = transform.position + Vector3.up * 1.5f + GetAttackDirection() * 0.5f;
        Quaternion baseRotation = Quaternion.LookRotation(GetAttackDirection());
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
        _autoTarget = null;
    }

    // ---------- Таргетинг (без изменений логики) ----------

    void MaintainLockTarget()
    {
        if (!IsValidEnemy(currentTarget))
        {
            currentTarget = FindNearestInRadius(targetLockRange);
            return;
        }
        TryCloseSwitch();
        Vector3 to = currentTarget.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude <= targetHoldRange * targetHoldRange) return;
        currentTarget = FindNearestInRadius(targetLockRange);
    }

    void TryAcquireCombatTarget()
    {
        if (NearTarget != null) return;
        Transform near = FindPreferredTarget(combatFaceRange);
        if (near != null) currentTarget = near;
    }

    void TryCloseSwitch()
    {
        if (!IsValidEnemy(currentTarget)) return;
        Collider[] enemies = Physics.OverlapSphere(transform.position, closeSwitchRange, enemyLayers);
        if (enemies.Length == 0) return;

        Vector3 mouse = MouseDirection();
        Transform best = null;
        float bestAngle = float.MaxValue;

        foreach (Collider col in enemies)
        {
            if (!IsValidEnemy(col.transform)) continue;
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
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float dist = to.sqrMagnitude;
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        return closest;
    }

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

    Transform FindClosestToDirection(Vector3 preferred, float radius)
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, radius, enemyLayers);
        Transform best = null;
        float bestAngle = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        foreach (Collider col in enemies)
        {
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;
            float angle = Vector3.Angle(preferred, to);
            if (angle < bestAngle) { bestAngle = angle; best = col.transform; }
        }
        return best;
    }

    Transform FindPreferredTarget(float radius)
    {
        Vector3 preferred = GetPreferredAimDirection();
        Collider[] enemies = Physics.OverlapSphere(transform.position, radius, enemyLayers);
        Transform best = null;
        float bestScore = float.MaxValue;

        foreach (Collider col in enemies)
        {
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            float score = to.magnitude + Vector3.Angle(preferred, to) * aimAnglePenalty;
            if (score < bestScore) { bestScore = score; best = col.transform; }
        }
        return best;
    }

    Transform FindNearestInCone()
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, targetLockRange, enemyLayers);
        Transform closest = null;
        float minDist = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        foreach (Collider col in enemies)
        {
            if (!IsValidEnemy(col.transform)) continue;
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;
            float dist = to.sqrMagnitude;
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        return closest;
    }

    public void ClearTarget() => currentTarget = null;

    private float _peaceHoldTimer;
    private float _combatLingerUntil;

    void TickPeaceHold()
    {
        if (IsAttacking || _pendingAttack)
        {
            _peaceHoldTimer = 0f;
            return;
        }

        if (Input.GetKey(peaceHoldKey))
        {
            _peaceHoldTimer += Time.deltaTime;
            if (_peaceHoldTimer >= peaceHoldDuration && (IsArmed || !ForcePeace))
                EnterForcePeace();
        }
        else
            _peaceHoldTimer = 0f;
    }

    /// <summary>Обновляет IsInCombat: активный бой продлевает linger; без врагов/действий — держим combatLingerSeconds; 1 — сброс.</summary>
    void TickCombatState()
    {
        if (ForcePeace)
        {
            IsInCombat = false;
            _combatLingerUntil = 0f;
            return;
        }

        bool active = IsArmed &&
                      (NearTarget != null || IsCharging || IsAttacking || IsBlocking || IsWindingUp || _pendingAttack);
        if (active)
            _combatLingerUntil = Time.time + combatLingerSeconds;

        IsInCombat = IsArmed && Time.time < _combatLingerUntil;
    }

    /// <summary>Удержание 1 ≥ 1.5с: убрать оружие, сброс лока, мирный режим до следующей атаки.</summary>
    public void EnterForcePeace()
    {
        _peaceHoldTimer = 0f;
        ForcePeace = true;
        IsInCombat = false;
        _combatLingerUntil = 0f;
        if (IsCharging) CancelCharge();
        IsArmed = false;
        CurrentStance = CombatStance.Neutral;
        _stanceTimer = 0f;
        ClearTarget();
        _autoTarget = null;
        _shiftSavedTarget = null;
        IsBlocking = false;
        SetB("ShieldBlock", false);
        SetB("Armed", false);
        SetB("Combat", false);
        SetTrig("Sheath");
        SetTrig("ToPeace");
    }

    public void ToggleArmed()
    {
        if (IsArmed) SheathWeapon();
        else DrawWeapon();
    }

    /// <summary>Убрать оружие: сброс лока, стойки, заряда. Combat-анимки гаснут через IsArmed.</summary>
    public void SheathWeapon()
    {
        if (!IsArmed) return;
        if (IsCharging) CancelCharge();
        IsArmed = false;
        CurrentStance = CombatStance.Neutral;
        _stanceTimer = 0f;
        ClearTarget();
        _autoTarget = null;
        _shiftSavedTarget = null;
        IsBlocking = false;
        SetB("ShieldBlock", false);
        SetB("Armed", false);
        SetTrig("Sheath");
    }

    /// <summary>Достать оружие. Атака/блок сами вызывают, если было убрано. Снимает ForcePeace.</summary>
    public void DrawWeapon()
    {
        ForcePeace = false;
        if (IsArmed) return;
        IsArmed = true;
        SetB("Armed", true);
        SetTrig("Draw");
    }

    void RestoreOrAcquireTarget()
    {
        currentTarget = IsValidRestoreTarget(_shiftSavedTarget) ? _shiftSavedTarget : FindNearestInCone();
        _shiftSavedTarget = null;
    }

    /// <summary>Живой враг на enemyLayers (труп / мёртвый WerewolfStats не берём).</summary>
    bool IsValidEnemy(Transform t)
    {
        if (t == null || !t.gameObject.activeInHierarchy) return false;
        var stats = t.GetComponentInParent<WerewolfStats>();
        if (stats != null) return stats.IsAlive;
        return true; // не оборотень — считаем валидным (другие типы врагов)
    }

    bool IsValidRestoreTarget(Transform t)
    {
        if (!IsValidEnemy(t)) return false;
        Vector3 to = t.position - transform.position;
        to.y = 0f;
        return to.sqrMagnitude <= targetLockRange * targetLockRange;
    }

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
        mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
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
            float effectiveRange = currentWeapon != null ? currentWeapon.attackRange : targetLockRange;
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
        if (Camera.main == null) return transform.forward;
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
