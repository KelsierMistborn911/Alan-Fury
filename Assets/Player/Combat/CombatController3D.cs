using UnityEngine;

public class CombatController3D : MonoBehaviour
{
    public enum AttackForm { SlashLeft, SlashRight, Thrust }

    [Header("Ссылки")]
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public WeaponHitbox hitbox;
    public PlayerTargeting targeting;
    public PlayerStance stance;
    [Tooltip("Аниматор игрока. Пусто — найдётся на объекте.")]
    public Animator animator;

    [Header("Комбо / стойки (атака)")]
    [Tooltip("Пауза между ударами, после которой серия сбрасывается (сек).")]
    public float comboWindow = 1f;
    [Tooltip("Множитель длительности атаки в подходящей стойке (<1 = быстрее). Вне стойки = 1 (полный замах).")]
    [Range(0.5f, 1f)] public float stanceSpeedBonus = 0.85f;
    [Tooltip("Порог ChargePercent, после которого атака считается тяжёлой.")]
    [Range(0.3f, 0.9f)] public float heavyChargeThreshold = 0.55f;
    [Tooltip("Дистанция, выше которой МОЖЕТ выпасть Thrust (и то не всегда). Ниже — только slash.")]
    public float thrustPreferDistance = 3.6f;
    [Tooltip("Шанс укола, когда цель дальше thrustPreferDistance (0–1). Остальное — slash.")]
    [Range(0f, 1f)] public float thrustChanceWhenFar = 0.28f;

    [Header("Управление боем")]
    [Tooltip("Клавиша блока (щит в левой руке обязателен). ПКМ.")]
    public KeyCode blockKey = KeyCode.Mouse1;
    [Tooltip("Клавиша парирования. Одно нажатие открывает короткое окно, удерживать не нужно.")]
    public KeyCode parryKey = KeyCode.F;
    [Tooltip("Укол (Thrust).")]
    public KeyCode thrustKey = KeyCode.Q;
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

    [Header("Застревание оружия")]
    [Tooltip("После успешного попадания (укол и рубка): ограничение движения на это время (сек). 0 = выкл.")]
    public float weaponStuckDuration = 1.6f;
    [Tooltip("Базовый множитель скорости, пока оружие «застряло».")]
    [Range(0.15f, 0.9f)] public float stuckSpeedMult = 0.45f;
    [Tooltip("Доп. множитель, если давить вперёд по направлению удара (ещё сильнее «не отпускает»).")]
    [Range(0.05f, 0.6f)] public float stuckForwardExtraMult = 0.25f;
    [Tooltip("Сколько секунд активного отхода (назад/бок) нужно, чтобы выдернуть раньше таймера.")]
    public float stuckPullFreeTime = 0.22f;

    [Header("Комбо удар+уворот")]
    public float dodgeAttackMinWindup = 0.08f;
    public float dodgeAttackBufferAfter = 0.2f;
    public float dodgeAttackPerfectTolerance = 0.08f;

    // --- Публичное состояние (API для Movement / HUD / Perception) ---
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
    public bool HasTarget => targeting != null && targeting.HasTarget;
    public float ChargePercent { get; private set; }
    public bool IsHeavyReady => IsCharging && ChargePercent >= heavyChargeThreshold;

    /// <summary>Прокси на PlayerStance.Current — внешний код не ломается.</summary>
    public CombatStance CurrentStance => stance != null ? stance.Current : CombatStance.Neutral;

    /// <summary>Прокси на PlayerTargeting.CurrentTarget.</summary>
    public Transform currentTarget => targeting != null ? targeting.CurrentTarget : null;

    public Transform NearTarget => targeting != null ? targeting.NearTarget : null;

    /// <summary>Сброс лока. Прокси на PlayerTargeting — нужен PlayerMovement (перекат/телепорт).</summary>
    public void ClearTarget()
    {
        if (targeting != null) targeting.ClearTarget();
    }

    public Transform ActiveAimTarget
    {
        get
        {
            if (!(IsCharging || IsBlocking) || targeting == null) return null;
            if (NearTarget != null) return NearTarget;
            return targeting.IsValidEnemy(targeting.AutoTarget) ? targeting.AutoTarget : null;
        }
    }

    // --- Внутреннее ---
    private float stateTimer;
    private WeaponData currentWeapon;
    private float chargeStartTime;
    private bool isHoldingAttack;
    private int _combo;
    private float _comboExpire;
    private PlayerMovement3D movement;

    private bool _pendingAttack;
    private float _pendingFireTime;
    private bool _pendingIsBlockAttack;
    private bool _dodgeAttackPerfectFlag;
    private bool _fromLowStance;
    private bool _isHeavyAttack;
    private AttackForm _lastForm = AttackForm.SlashRight;

    private float _parryEndTime;
    private float _parryReadyTime;

    private bool _canStickThisAttack;
    private Vector3 _stuckAttackDir;

    enum AttackMoveMode { None, Stop, TurnStrike, SideStrike }
    private AttackMoveMode _attackMoveMode;

    private float _peaceHoldTimer;
    private float _combatLingerUntil;

    // --- Буфер шагов во время charge (последние 1–2) ---
    private struct StepSample
    {
        public Vector3 dir;   // мир, горизонталь, нормализован
        public float time;
    }
    private StepSample _step0; // старше
    private StepSample _step1; // новее (последний)
    private int _stepCount;
    private float _nextStepSampleTime;
    private const float StepMinInterval = 0.12f;
    private const float StepInputThreshold = 0.35f;

    private HitIntent _pendingIntent = HitIntent.Neutral;
    private bool _pendingStepBoost;
    private BodyZone _pendingZone = BodyZone.Torso;

    private System.Collections.Generic.HashSet<string> _animParams;

    void Awake()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (animator == null) animator = GetComponent<Animator>();
        if (movement == null) movement = GetComponent<PlayerMovement3D>();
        if (targeting == null) targeting = GetComponent<PlayerTargeting>();
        if (stance == null) stance = GetComponent<PlayerStance>();

        if (targeting == null)
            Debug.LogError("[CombatController3D] Нет PlayerTargeting на объекте. Добавь компонент.");
        if (stance == null)
            Debug.LogError("[CombatController3D] Нет PlayerStance на объекте. Добавь компонент.");

        CacheAnimParams();
        SetB("Armed", IsArmed);

        if (hitbox != null)
            hitbox.onHit += OnWeaponHit;
    }

    void OnDestroy()
    {
        if (hitbox != null)
            hitbox.onHit -= OnWeaponHit;
    }

    void OnWeaponHit()
    {
        if (!_canStickThisAttack || movement == null || weaponStuckDuration <= 0f)
            return;

        _canStickThisAttack = false;
        movement.EnterWeaponStuck(
            weaponStuckDuration,
            _stuckAttackDir,
            stuckSpeedMult,
            stuckForwardExtraMult,
            stuckPullFreeTime);
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
        if (stance != null)
            stance.Tick(IsArmed, ForcePeace);

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

        if (targeting != null)
        {
            targeting.TickLock();
            if (IsCharging || IsBlocking)
                targeting.TryAcquireIfNeeded();

            if (Input.GetKeyDown(KeyCode.LeftShift))
                targeting.SaveAndClearForShift();
            if (Input.GetKeyUp(KeyCode.LeftShift))
                targeting.RestoreAfterShift();

            targeting.UpdateMarker();

            if (Input.GetKeyDown(KeyCode.Tab))
                targeting.ToggleOrAcquireByTab();
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
            SampleChargeSteps();

            if (NearTarget == null && targeting != null)
            {
                Transform reTarget = targeting.FindClosestToDirection(
                    targeting.GetPreferredAimDirection(),
                    targeting.combatFaceRange);
                targeting.SetAutoTarget(reTarget);
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

        // Укол по Q (без заряда)
        if (Input.GetKeyDown(thrustKey))
        {
            TryStartThrust();
            return;
        }

        // Обычная атака (ЛКМ — hold/charge)
        if (Input.GetMouseButtonDown(0))
        {
            currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
            if (currentWeapon == null) return;
            if (resources != null && resources.HasStamina(currentWeapon.staminaCost * 0.5f))
                StartHoldAttack();
        }
    }

    void TryStartThrust()
    {
        if (!IsArmed || IsAttacking || IsCharging || _pendingAttack) return;
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return;
        if (resources != null && !resources.HasStamina(currentWeapon.staminaCost * 0.5f)) return;
        if (resources != null) resources.SpendStamina(currentWeapon.staminaCost * 0.5f);

        DrawWeapon();
        ChargePercent = currentWeapon.minChargePercent;
        _isHeavyAttack = false;
        _fromLowStance = CurrentStance == CombatStance.Low;
        SampleAttackMoveMode();
        ExecuteAttack(fromBlock: false, forcedForm: AttackForm.Thrust);
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
        if (targeting != null) targeting.ClearAutoTarget();
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
        ClearStepBuffer();
        if (targeting != null)
        {
            targeting.ClearAutoTarget();
            targeting.TryAcquireIfNeeded();
        }
        SampleAttackMoveMode();
        if (hitbox != null && hitbox.visual != null)
            hitbox.visual.ShowWindup();
    }

    void ClearStepBuffer()
    {
        _stepCount = 0;
        _nextStepSampleTime = 0f;
        _pendingIntent = HitIntent.Neutral;
        _pendingStepBoost = false;
        _pendingZone = BodyZone.Torso;
    }

    /// <summary>
    /// Во время charge пишем до 2 значимых шагов. Один хватает; два в одном духе → boost.
    /// </summary>
    void SampleChargeSteps()
    {
        if (movement == null) return;
        if (Time.time < _nextStepSampleTime) return;

        Vector3 input = movement.InputDirection;
        if (input.sqrMagnitude < StepInputThreshold * StepInputThreshold) return;

        input.y = 0f;
        input.Normalize();

        // не дублируем почти то же направление подряд
        if (_stepCount > 0)
        {
            Vector3 last = _stepCount >= 2 ? _step1.dir : _step0.dir;
            if (Vector3.Dot(last, input) > 0.92f) return;
        }

        if (_stepCount == 0)
        {
            _step0 = new StepSample { dir = input, time = Time.time };
            _stepCount = 1;
        }
        else if (_stepCount == 1)
        {
            _step1 = new StepSample { dir = input, time = Time.time };
            _stepCount = 2;
        }
        else
        {
            _step0 = _step1;
            _step1 = new StepSample { dir = input, time = Time.time };
        }

        _nextStepSampleTime = Time.time + StepMinInterval;
    }

    /// <summary>
    /// На release heavy: последний шаг → intent/зона; два согласованных → boost.
    /// </summary>
    void ResolveStepsForHeavy()
    {
        _pendingIntent = HitIntent.Neutral;
        _pendingStepBoost = false;
        _pendingZone = BodyZone.Torso;

        if (_stepCount <= 0) return;

        StepSample last = _stepCount >= 2 ? _step1 : _step0;
        Vector3 f = transform.forward; f.y = 0f; f.Normalize();
        Vector3 r = transform.right; r.y = 0f; r.Normalize();
        float fwd = Vector3.Dot(last.dir, f);
        float side = Vector3.Dot(last.dir, r);

        if (fwd > 0.45f)
        {
            _pendingIntent = HitIntent.ThrustLine;
            // сильный charge + вперёд → голова, иначе торс
            _pendingZone = ChargePercent >= 0.85f ? BodyZone.Head : BodyZone.Torso;
        }
        else if (fwd < -0.45f)
        {
            _pendingIntent = HitIntent.Limb;
            _pendingZone = side >= 0f ? BodyZone.RightLeg : BodyZone.LeftLeg;
        }
        else if (Mathf.Abs(side) > 0.4f)
        {
            _pendingIntent = HitIntent.Bypass;
            _pendingZone = side >= 0f ? BodyZone.RightArm : BodyZone.LeftArm;
        }
        else
        {
            _pendingIntent = HitIntent.Neutral;
            _pendingZone = BodyZone.Torso;
        }

        // два шага: оба roughly в ту же полусферу → boost
        if (_stepCount >= 2)
        {
            float align = Vector3.Dot(_step0.dir, _step1.dir);
            if (align > 0.25f)
                _pendingStepBoost = true;
        }
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

        // Heavy: форма/зона от шагов. Light: intent остаётся Neutral, авто-форма как раньше.
        if (_isHeavyAttack)
            ResolveStepsForHeavy();
        else
        {
            _pendingIntent = HitIntent.Neutral;
            _pendingStepBoost = false;
            _pendingZone = BodyZone.Torso;
        }

        float cost = Mathf.Lerp(currentWeapon.staminaCost * 0.5f, currentWeapon.staminaCost, ChargePercent);
        if (resources != null) resources.SpendStamina(cost);

        ExecuteAttack(fromBlock: false);
    }

    void ExecuteAttack(bool fromBlock, AttackForm? forcedForm = null)
    {
        IsWindingUp = false;
        IsAttacking = true;

        float dur = currentWeapon != null ? currentWeapon.attackDuration : 0.2f;
        bool stanceMatch = (CurrentStance == CombatStance.High && !_isHeavyAttack)
                        || (CurrentStance == CombatStance.Low && (_isHeavyAttack || _fromLowStance));
        if (stanceMatch) dur *= stanceSpeedBonus;
        stateTimer = dur;

        bool wasInCombo = Time.time <= _comboExpire;
        _combo = wasInCombo ? _combo + 1 : 0;
        _comboExpire = Time.time + dur + comboWindow;

        // Heavy + ThrustLine без forced → укол; иначе обычный ChooseAttackForm
        AttackForm form;
        if (forcedForm.HasValue)
            form = forcedForm.Value;
        else if (_isHeavyAttack && _pendingIntent == HitIntent.ThrustLine)
            form = AttackForm.Thrust;
        else
            form = ChooseAttackForm(wasInCombo, null);

        // Bypass: сторона по последнему шагу, если есть
        if (_isHeavyAttack && _pendingIntent == HitIntent.Bypass && _stepCount > 0)
        {
            StepSample last = _stepCount >= 2 ? _step1 : _step0;
            float side = Vector3.Dot(last.dir, transform.right);
            form = side >= 0f ? AttackForm.SlashRight : AttackForm.SlashLeft;
        }

        _lastForm = form;
        string trig = form switch
        {
            AttackForm.Thrust => "Thrust",
            AttackForm.SlashLeft => "AttackLeft",
            _ => "AttackRight"
        };
        SetTrig(trig);

        _canStickThisAttack = weaponStuckDuration > 0f;
        _stuckAttackDir = GetAttackDirection();

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
                if (_pendingStepBoost) damageMult *= 1.15f;
            }
            else if (_fromLowStance)
            {
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

            // Обход — длиннее и чуть шире
            if (_isHeavyAttack && _pendingIntent == HitIntent.Bypass)
            {
                range *= 1.2f;
                radius *= 1.15f;
            }

            if (fromBlock)
                range *= blockAttackRangeMult;

            float damage = currentWeapon.damage * damageMult;
            damage += ComputeMomentumBonus();
            // скорость шага в силу: если есть шаги и движение — доп. кусок momentum
            if (_isHeavyAttack && _stepCount > 0 && movement != null)
                damage += movement.CurrentSpeed * (resources != null ? resources.mass : 80f) * movementDamageCoefficient * (_pendingStepBoost ? 1.5f : 1f);

            ApplyAttackMoveMode();
            ApplyFootworkStep(form);

            if (_isHeavyAttack && !fromBlock && _pendingIntent == HitIntent.ThrustLine)
                TryLunge();
            else if (_isHeavyAttack && !fromBlock)
                TryLunge();

            // Зона для light: торс; slash по стороне → рука
            BodyZone zone = _pendingZone;
            if (!_isHeavyAttack)
            {
                zone = BodyZone.Torso;
                if (form == AttackForm.SlashLeft) zone = BodyZone.LeftArm;
                else if (form == AttackForm.SlashRight) zone = BodyZone.RightArm;
                else if (form == AttackForm.Thrust) zone = BodyZone.Torso;
            }

            float pen = currentWeapon.penetration;
            float penScore = pen * (0.55f + ChargePercent * 0.9f);
            if (_isHeavyAttack) penScore *= 1.2f;
            if (_pendingStepBoost) penScore *= 1.15f;
            if (form == AttackForm.Thrust) penScore *= 1.2f;

            var info = new HitInfo
            {
                rawDamage = damage,
                finalDamage = damage,
                stagger = currentWeapon.staggerForce * staggerMult,
                sourcePosition = transform.position,
                hitDirection = GetAttackDirection(),
                zone = zone,
                intent = _isHeavyAttack ? _pendingIntent : HitIntent.Neutral,
                isHeavy = _isHeavyAttack,
                stepBoost = _pendingStepBoost,
                chargePercent = ChargePercent,
                penetrationScore = penScore,
                weaponPenetration = pen
            };
            hitbox.SetHitInfo(info);

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

        // Переход стойки после удара — те же триггеры EnterHigh / EnterLow
        if (stance != null)
        {
            if (_isHeavyAttack)
                stance.Enter(CombatStance.Low);
            else
                stance.Enter(CombatStance.High);
        }

        _dodgeAttackPerfectFlag = false;
        ChargePercent = 0f;
        _isHeavyAttack = false;
        _fromLowStance = false;
        ClearStepBuffer();
    }

    AttackForm ChooseAttackForm(bool wasInCombo, AttackForm? forced = null)
    {
        if (forced.HasValue)
            return forced.Value;

        Transform aim = NearTarget != null ? NearTarget
            : (targeting != null && targeting.AutoTarget != null ? targeting.AutoTarget : currentTarget);

        if (wasInCombo && _lastForm != AttackForm.Thrust && aim != null)
        {
            Vector3 to = aim.position - transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            float range = currentWeapon != null ? currentWeapon.attackRange : 2f;
            float thrustDist = Mathf.Max(thrustPreferDistance, range * 1.35f);
            if (dist >= thrustDist)
                return AttackForm.Thrust;
        }

        if (wasInCombo && (_lastForm == AttackForm.SlashLeft || _lastForm == AttackForm.SlashRight))
        {
            return _lastForm == AttackForm.SlashRight
                ? AttackForm.SlashLeft
                : AttackForm.SlashRight;
        }

        return ChooseFormBySide(aim);
    }

    AttackForm ChooseFormBySide(Transform aim)
    {
        if (aim == null)
            return AttackForm.SlashRight;

        Vector3 to = aim.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f)
            return AttackForm.SlashRight;

        float side = Vector3.Dot(transform.right, to.normalized);
        return side >= 0f ? AttackForm.SlashRight : AttackForm.SlashLeft;
    }

    void ApplyFootworkStep(AttackForm form)
    {
        if (movement == null) return;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        bool hasInput = Mathf.Abs(h) > 0.15f || Mathf.Abs(v) > 0.15f;

        if (hasInput)
            return;

        Vector3 attackDir = GetAttackDirection();
        if (attackDir.sqrMagnitude < 0.01f) return;

        movement.AddLungeSpeed(attackDir, 2.5f);
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
        if (targeting != null) targeting.ClearAutoTarget();
        _canStickThisAttack = false;
    }

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
        if (stance != null) stance.ResetToNeutral();
        if (targeting != null)
        {
            targeting.ClearTarget();
            targeting.ClearAutoTarget();
        }
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
        if (stance != null) stance.ResetToNeutral();
        if (targeting != null)
        {
            targeting.ClearTarget();
            targeting.ClearAutoTarget();
        }
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

    Vector3 GetAttackDirection()
    {
        Transform aim = NearTarget != null ? NearTarget
            : (targeting != null ? targeting.AutoTarget : null);
        if (aim != null)
        {
            float effectiveRange = currentWeapon != null ? currentWeapon.attackRange
                : (targeting != null ? targeting.targetLockRange : 15f);
            Vector3 diff = aim.position - transform.position;
            diff.y = 0f;
            if (diff.magnitude > effectiveRange && targeting != null)
            {
                Transform nearest = targeting.FindNearestInRadius(targeting.combatFaceRange);
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
