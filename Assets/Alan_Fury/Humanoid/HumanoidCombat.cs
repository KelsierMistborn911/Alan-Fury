using UnityEngine;

/// <summary>
/// Боевая машина гуманоида. Без Input / Camera.
/// Драйвер (игрок или ИИ) вызывает TryHoldAttack / ReleaseAttack / TryThrust / SetBlocking и т.д.
/// Окно урона всегда через MeleeAction.Play — тот же пайплайн, что у игрока.
/// </summary>
public class HumanoidCombat : MonoBehaviour
{
    public enum AttackForm { SlashLeft, SlashRight, Thrust, Elbow, Pommel, Shoulder, CloseHit }

    [Header("Ссылки")]
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public WeaponHitbox hitbox;
    public PlayerStance stance;
    [Tooltip("Аниматор. Пусто — найдётся на объекте.")]
    public Animator animator;
    [Tooltip("Визуал оружия. Пусто — найдётся на объекте.")]
    public WeaponVisual weaponVisual;
    public MeleeAction melee;

    [Header("Комбо / стойки (атака)")]
    public float comboWindow = 1f;
    [Range(0.5f, 1f)] public float stanceSpeedBonus = 0.85f;
    [Range(0.3f, 0.9f)] public float heavyChargeThreshold = 0.55f;

    [Header("Управление боем")]
    public float sheathVisualDelay = 1f;
    public float combatLingerSeconds = 15f;
    public float combatFaceRange = 10f;

    [Header("Парирование")]
    public float parryWindow = 0.25f;
    public float parryCooldown = 0.25f;

    [Header("Удар под блоком")]
    public float blockAttackWindup = 0.15f;
    [Range(0.3f, 1f)] public float blockAttackRangeMult = 0.7f;
    public float blockAttackStaminaMult = 1.35f;

    [Header("Выпад / импульс")]
    public float lungeSpeedMultiplier = 1.6f;
    [Range(0f, 1f)] public float lungeInputDot = 0.5f;
    public float movementDamageCoefficient = 0.02f;

    [Header("Подшаг к дистанции удара")]
    [Range(0.4f, 1f)] public float spacingIdealFraction = 0.72f;
    [Range(0.5f, 1.2f)] public float spacingThrustFraction = 0.95f;
    public float spacingDeadzone = 0.28f;
    public float spacingMaxStep = 0.86f;
    public float spacingHeavyStep = 1.73f;
    public float spacingDuration = 0.18f;
    public float targetMagnetRange = 4.2f;
    public float targetMagnetSpeed = 5.5f;

    [Header("Клинч")]
    [Tooltip("Держать это расстояние, пока нет умышленного входа.")]
    [Range(0.7f, 1.2f)] public float infightHoldFraction = 0.92f;
    [Range(0.35f, 0.9f)] public float infightElbowWindup = 0.55f;
    [Range(0.35f, 0.9f)] public float infightPommelWindup = 0.48f;
    [Range(0.5f, 1f)] public float infightShoulderWindup = 0.82f;
    [Range(0.4f, 1f)] public float infightElbowDuration = 0.68f;
    [Range(0.4f, 1f)] public float infightPommelDuration = 0.58f;
    [Range(0.5f, 1.1f)] public float infightShoulderDuration = 0.88f;
    public float shieldRamInterval = 0.55f;
    public float shieldRamDamage = 3f;
    public float shieldRamStagger = 7f;
    public float shieldRamForce = 5.5f;
    public float shieldRamAssist = 3.4f;

    [Header("Застревание оружия")]
    public float weaponStuckDuration = 1.6f;
    [Range(0.15f, 0.9f)] public float stuckSpeedMult = 0.45f;
    [Range(0.05f, 0.6f)] public float stuckForwardExtraMult = 0.25f;
    public float stuckPullFreeTime = 0.22f;

    [Header("Комбо удар+уворот")]
    public float dodgeAttackMinWindup = 0.08f;
    public float dodgeAttackBufferAfter = 0.2f;
    public float dodgeAttackPerfectTolerance = 0.08f;

    public bool IsWindingUp { get; protected set; }
    public bool IsAttacking { get; protected set; }
    public bool IsBlocking { get; protected set; }
    public bool IsParrying { get; protected set; }
    public bool IsCharging { get; protected set; }
    public bool IsInAttackPipeline => IsAttacking || IsWindingUp || IsCharging;
    public bool IsArmed { get; protected set; }
    public bool IsShieldArmed { get; protected set; }
    public bool ForcePeace { get; protected set; }
    public bool IsInCombat { get; protected set; }
    public float ChargePercent { get; protected set; }
    public bool IsHeavyReady => IsCharging && ChargePercent >= heavyChargeThreshold;
    public bool IsInfighting { get; protected set; }

    public CombatStance CurrentStance => stance != null ? stance.Current : CombatStance.Neutral;

    /// <summary>Цель, которую выставил драйвер (лок игрока или мозг NPC).</summary>
    public Transform CommandTarget { get; set; }
    public Transform AutoTarget { get; set; }
    /// <summary>Мировое горизонтальное направление прицела, если цели нет.</summary>
    public Vector3 AimDirection { get; set; }
    public LayerMask EnemyLayers;

    public Transform currentTarget => CommandTarget;
    public bool HasTarget => IsUsableTarget(CommandTarget);

    public Transform NearTarget
    {
        get
        {
            if (!IsUsableTarget(CommandTarget)) return null;
            Vector3 to = CommandTarget.position - transform.position;
            to.y = 0f;
            return to.sqrMagnitude <= combatFaceRange * combatFaceRange ? CommandTarget : null;
        }
    }

    public Transform ActiveAimTarget
    {
        get
        {
            if (!(IsCharging || IsBlocking)) return null;
            if (NearTarget != null) return NearTarget;
            return IsUsableTarget(AutoTarget) ? AutoTarget : null;
        }
    }

    public virtual void ClearTarget()
    {
        CommandTarget = null;
        AutoTarget = null;
    }

    protected float stateTimer;
    protected WeaponData currentWeapon;
    protected float chargeStartTime;
    protected bool isHoldingAttack;
    protected int _combo;
    protected float _comboExpire;
    protected HumanoidLocomotion movement;

    protected bool _dodgeAttackPerfectFlag;
    protected bool _fromLowStance;
    protected bool _isHeavyAttack;
    protected AttackForm _lastForm = AttackForm.SlashRight;

    protected float _parryEndTime;
    protected float _parryReadyTime;

    protected bool _canStickThisAttack;
    protected Vector3 _stuckAttackDir;

    protected struct PreparedAttack
    {
        public float range, radius, height, damage, stagger;
        public float cone, dur, tick, charge;
        public Vector3 offset, dir;
        public LayerMask layers;
        public int combo;
        public HitZoneShape shape;
        public float innerRadius;
        public float yawOffset;
        public HitInfo info;
    }
    protected PreparedAttack _prep;
    protected bool _hitPrepared;

    protected enum AttackMoveMode { None, Stop, TurnStrike }
    protected AttackMoveMode _attackMoveMode;

    protected float _combatLingerUntil;
    protected float _nextShieldRamTime;
    protected float _noRadialPushUntil;
    protected float _shockUntil;

    [Header("Шок от удара")]
    public float shockLight = 0.22f;
    public float shockHeavy = 0.26f;

    public bool IsInShock => Time.time < _shockUntil;

    protected struct StepSample
    {
        public Vector3 dir;
        public float time;
    }
    protected StepSample _step0;
    protected StepSample _step1;
    protected int _stepCount;
    protected float _nextStepSampleTime;
    protected const float StepMinInterval = 0.12f;
    protected const float StepInputThreshold = 0.35f;

    protected HitIntent _pendingIntent = HitIntent.Neutral;
    protected bool _pendingStepBoost;
    protected BodyZone _pendingZone = BodyZone.Torso;

    protected System.Collections.Generic.HashSet<string> _animParams;

    protected virtual void Awake()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (animator == null) animator = GetComponent<Animator>();
        if (movement == null) movement = GetComponent<HumanoidLocomotion>();
        if (stance == null) stance = GetComponent<PlayerStance>();
        if (weaponVisual == null) weaponVisual = GetComponent<WeaponVisual>();
        if (melee == null) melee = GetComponent<MeleeAction>();
        if (melee == null) melee = gameObject.AddComponent<MeleeAction>();
        if (melee.hitbox == null) melee.hitbox = hitbox;

        CacheAnimParams();
        SetB("Armed", IsArmed);
        SetB("ShieldArmed", IsShieldArmed);
        ApplyWeaponVisualsImmediate();

        if (hitbox != null)
            hitbox.onHit += OnWeaponHit;

        if (AimDirection.sqrMagnitude < 0.01f)
            AimDirection = transform.forward;

        if (Mathf.Abs(targetMagnetRange - 3f) < 0.02f)
            targetMagnetRange = 4.2f;
        if (Mathf.Abs(spacingMaxStep - 1.15f) < 0.02f
            || Mathf.Abs(spacingMaxStep - 3.45f) < 0.02f)
            spacingMaxStep = 0.86f;
        if (spacingHeavyStep <= 0.01f || Mathf.Abs(spacingHeavyStep - 3.45f) < 0.02f)
            spacingHeavyStep = 1.73f;
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

    protected void CacheAnimParams()
    {
        _animParams = new System.Collections.Generic.HashSet<string>();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters) _animParams.Add(p.name);
    }

    protected void SetTrig(string name)
    {
        if (animator == null || _animParams == null || !_animParams.Contains(name)) return;
        animator.ResetTrigger(name);
        animator.SetTrigger(name);
    }

    public void FireSpellTrigger(string name) => SetTrig(name);

    protected void SetB(string name, bool value)
    {
        if (animator != null && _animParams != null && _animParams.Contains(name))
            animator.SetBool(name, value);
    }

    protected virtual void Update()
    {
        if (resources != null && resources.IsDead)
        {
            IsAttacking = false;
            IsWindingUp = false;
            IsCharging = false;
            IsBlocking = false;
            IsParrying = false;
            isHoldingAttack = false;
            _hitPrepared = false;
            IsInfighting = false;
            return;
        }

        if (stance != null)
            stance.Tick(IsInCombat, IsArmed, IsInAttackPipeline);

        TickCombatState();

        if (IsParrying && Time.time >= _parryEndTime) IsParrying = false;

        TickTargetMagnet();
        TickShieldRam();

        if (IsAttacking)
        {
            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0f)
            {
                if (IsWindingUp)
                {
                    IsWindingUp = false;
                    ActivatePreparedHitbox();
                    stateTimer = _prep.dur > 0f ? _prep.dur : 0.2f;
                }
                else
                {
                    EndAttack();
                }
            }
            return;
        }

        if (IsCharging)
        {
            IsWindingUp = true;
            SampleChargeSteps();

            if (NearTarget == null)
            {
                Transform reTarget = FindClosestToAim(combatFaceRange);
                if (reTarget != null) AutoTarget = reTarget;
            }

            if (currentWeapon != null)
                ChargePercent = Mathf.Clamp01((Time.time - chargeStartTime) / currentWeapon.chargeDuration);

            if (currentWeapon != null && currentWeapon.maxHoldTime > 0
                && Time.time - chargeStartTime >= currentWeapon.maxHoldTime)
                ReleaseAttack();
        }
    }

    public static bool IsUsableTarget(Transform t)
    {
        if (t == null) return false;
        var stats = t.GetComponent<IDamageable>();
        return stats == null || stats.IsAlive;
    }

    public Transform FindClosestToAim(float radius)
    {
        Vector3 dir = GetAttackDirection();
        Collider[] hits = Physics.OverlapSphere(transform.position, radius, EnemyLayers);
        Transform best = null;
        float bestScore = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            Transform t = hits[i].transform;
            if (t == transform || !IsUsableTarget(t)) continue;
            Vector3 to = t.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.01f) continue;
            float ang = Vector3.Angle(dir, to);
            float score = to.magnitude + ang * 0.1f;
            if (score < bestScore)
            {
                bestScore = score;
                best = t;
            }
        }
        return best;
    }

    public Transform FindNearestInRadius(float radius)
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, radius, EnemyLayers);
        Transform best = null;
        float bestSq = radius * radius;
        for (int i = 0; i < hits.Length; i++)
        {
            Transform t = hits[i].transform;
            if (t == transform || !IsUsableTarget(t)) continue;
            Vector3 d = t.position - transform.position;
            d.y = 0f;
            if (d.sqrMagnitude < bestSq)
            {
                bestSq = d.sqrMagnitude;
                best = t;
            }
        }
        return best;
    }

    public bool TryParry()
    {
        if (!IsArmed || IsAttacking || Time.time < _parryReadyTime) return false;
        IsParrying = true;
        _parryEndTime = Time.time + parryWindow;
        _parryReadyTime = _parryEndTime + parryCooldown;
        SetTrig("Parry");
        return true;
    }

    public void SetBlocking(bool wants)
    {
        bool can = wants && IsShieldArmed && loadout != null && loadout.HasShield();
        IsBlocking = can;
        SetB("ShieldBlock", can);
    }

    public bool TryHoldAttack()
    {
        if (IsInShock) return false;
        if (IsAttacking || IsCharging) return false;
        if (!IsArmed)
        {
            DrawAll();
            return false;
        }
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return false;
        if (currentWeapon.isRanged) return false;
        if (resources != null && !resources.HasStamina(currentWeapon.staminaCost * 0.5f))
            return false;
        StartHoldAttack();
        return true;
    }

    public bool ReleaseAttack()
    {
        if (!isHoldingAttack) return false;
        ReleaseHeldAttack();
        return true;
    }

    public void CancelCharge()
    {
        IsCharging = false;
        IsWindingUp = false;
        isHoldingAttack = false;
        ChargePercent = 0f;
        AutoTarget = null;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.HideWindup();
    }

    public bool TryThrust()
    {
        if (IsInShock) return false;
        if (IsAttacking || IsCharging) return false;
        if (!IsArmed)
        {
            DrawAll();
            return false;
        }
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return false;
        if (currentWeapon.isRanged) return false;
        if (!TrySpendStamina(currentWeapon.staminaCost * 0.5f)) return false;

        ChargePercent = currentWeapon.minChargePercent;
        _isHeavyAttack = false;
        _fromLowStance = CurrentStance == CombatStance.Low;
        SampleAttackMoveMode();
        BeginWindupThenAttack(fromBlock: false, forcedForm: AttackForm.Thrust);
        return true;
    }

    public bool TryBlockAttack()
    {
        if (IsInShock) return false;
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return false;
        if (!TrySpendStamina(currentWeapon.staminaCost * 0.5f * blockAttackStaminaMult)) return false;
        PrepareLightAttack();
        BeginWindupThenAttack(fromBlock: true, windupOverride: blockAttackWindup);
        return true;
    }

    public bool TryDodgeAttack()
    {
        if (IsInShock) return false;
        currentWeapon = loadout != null ? loadout.GetMainWeapon() : null;
        if (currentWeapon == null) return false;
        if (!TrySpendStamina(currentWeapon.staminaCost)) return false;

        PrepareLightAttack();
        float progress = movement != null && movement.IsDodging ? movement.DodgeProgress01 : 1f;
        float windup = Mathf.Max(dodgeAttackMinWindup,
            Mathf.Lerp(currentWeapon.chargeDuration, dodgeAttackMinWindup, progress));

        _dodgeAttackPerfectFlag = movement != null &&
            ((movement.IsDodging && movement.DodgeTimeRemaining <= dodgeAttackPerfectTolerance) ||
             (!movement.IsDodging && movement.TimeSinceDodgeEnd <= dodgeAttackPerfectTolerance));

        BeginWindupThenAttack(fromBlock: false, windupOverride: windup);
        return true;
    }

    protected bool TrySpendStamina(float cost)
    {
        if (resources == null) return true;
        if (!resources.HasStamina(cost)) return false;
        resources.SpendStamina(cost);
        return true;
    }

    void PrepareLightAttack()
    {
        DrawWeapon();
        ChargePercent = currentWeapon.minChargePercent;
        _isHeavyAttack = false;
        _fromLowStance = CurrentStance == CombatStance.Low;
        SampleAttackMoveMode();
    }

    protected virtual void SampleAttackMoveMode()
    {
        _attackMoveMode = AttackMoveMode.None;
    }

    protected virtual float ExtraMomentumSpeed()
    {
        if (_dodgeAttackPerfectFlag && movement != null) return movement.DodgeSpeedValue;
        return 0f;
    }

    protected virtual bool AllowTargetMagnet() => true;

    protected virtual bool HasManualMoveInput()
    {
        return movement != null && movement.DesiredMoveDir.sqrMagnitude > 0.02f;
    }

    void BeginWindupThenAttack(bool fromBlock, AttackForm? forcedForm = null, float? windupOverride = null)
    {
        float windup = windupOverride ?? (currentWeapon != null ? currentWeapon.windupDuration : 0.15f);
        if (!windupOverride.HasValue)
        {
            bool stanceMatch = (CurrentStance == CombatStance.High && !_isHeavyAttack)
                            || (CurrentStance == CombatStance.Low && (_isHeavyAttack || _fromLowStance));
            if (stanceMatch) windup *= stanceSpeedBonus;
        }
        windup *= WeaponData.WindupScale;
        AttackForm preview = PreviewInfightForm(forcedForm);
        if (preview == AttackForm.CloseHit) windup *= infightElbowWindup * 0.9f;
        else if (preview == AttackForm.Elbow) windup *= infightElbowWindup;
        else if (preview == AttackForm.Pommel) windup *= infightPommelWindup;
        else if (preview == AttackForm.Shoulder) windup *= infightShoulderWindup;
        windup *= RangeTempo();

        CommitSwing(fromBlock, forcedForm);
        _comboExpire = Time.time + windup + _prep.dur + comboWindow;

        if (windup <= 0f)
        {
            IsWindingUp = false;
            ActivatePreparedHitbox();
            stateTimer = _prep.dur > 0f ? _prep.dur : 0.2f;
            return;
        }

        IsWindingUp = true;
        stateTimer = windup;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.ShowWindup();
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
        AutoTarget = null;
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

    void SampleChargeSteps()
    {
        if (movement == null) return;
        if (Time.time < _nextStepSampleTime) return;

        Vector3 input = movement.DesiredMoveDir;
        if (input.sqrMagnitude < StepInputThreshold * StepInputThreshold) return;

        input.y = 0f;
        input.Normalize();

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

        if (_stepCount >= 2)
        {
            float align = Vector3.Dot(_step0.dir, _step1.dir);
            if (align > 0.25f)
                _pendingStepBoost = true;
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

        Transform t = NearTarget != null ? NearTarget : CommandTarget;
        if (t == null) return;

        Vector3 toTarget = t.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.01f) return;
        toTarget.Normalize();

        Vector3 input = movement.DesiredMoveDir;
        if (input.sqrMagnitude < 0.01f) return;
        if (Vector3.Dot(input, toTarget) < lungeInputDot) return;

        movement.SnapRotationToTarget(t.position);
        movement.AddLungeSpeed(toTarget, movement.CurrentSpeed * lungeSpeedMultiplier);
    }

    float ComputeMomentumBonus()
    {
        if (movement == null || resources == null) return 0f;
        float speed = ExtraMomentumSpeed();
        if (speed <= 0f) return 0f;
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

        BeginWindupThenAttack(fromBlock: false);
    }

    void CommitSwing(bool fromBlock, AttackForm? forcedForm = null)
    {
        IsAttacking = true;
        _noRadialPushUntil = Time.time + 0.45f;

        float dur = currentWeapon != null ? currentWeapon.attackDuration : 0.2f;
        bool stanceMatch = (CurrentStance == CombatStance.High && !_isHeavyAttack)
                        || (CurrentStance == CombatStance.Low && (_isHeavyAttack || _fromLowStance));
        if (stanceMatch) dur *= stanceSpeedBonus;

        bool wasInCombo = Time.time <= _comboExpire;
        _combo = wasInCombo ? _combo + 1 : 0;
        _comboExpire = Time.time + dur + comboWindow;

        AttackForm form;
        if (forcedForm.HasValue)
            form = forcedForm.Value;
        else if (_isHeavyAttack && _pendingIntent == HitIntent.ThrustLine)
            form = AttackForm.Thrust;
        else
            form = ChooseAttackForm(wasInCombo);

        if (_isHeavyAttack && _pendingIntent == HitIntent.Bypass && _stepCount > 0)
        {
            StepSample last = _stepCount >= 2 ? _step1 : _step0;
            float side = Vector3.Dot(last.dir, transform.right);
            form = side >= 0f ? AttackForm.SlashRight : AttackForm.SlashLeft;
        }

        if (!fromBlock)
            form = RemapInfightForm(form);

        if (form == AttackForm.CloseHit) dur *= infightElbowDuration * 0.9f;
        else if (form == AttackForm.Elbow) dur *= infightElbowDuration;
        else if (form == AttackForm.Pommel) dur *= infightPommelDuration;
        else if (form == AttackForm.Shoulder) dur *= infightShoulderDuration;

        _lastForm = form;
        IsInfighting = IsInfightForm(form);
        SetB("Infight", IsInfighting);
        string trig = form switch
        {
            AttackForm.Thrust => "Thrust",
            AttackForm.Pommel => "Thrust",
            AttackForm.SlashLeft => "AttackLeft",
            AttackForm.Elbow => "AttackLeft",
            AttackForm.CloseHit => "AttackLeft",
            _ => "AttackRight"
        };
        SetTrig(trig);

        _canStickThisAttack = weaponStuckDuration > 0f;
        _stuckAttackDir = GetAttackDirection();

        _prep = new PreparedAttack
        {
            dur = dur,
            charge = ChargePercent,
            combo = _combo
        };
        _hitPrepared = true;

        if (currentWeapon != null)
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

            float range = currentWeapon.ScaledRange;
            float radius = currentWeapon.attackRadius;
            float cone = -1f;
            HitZoneShape shape = HitZoneShape.Sector;
            float inner = range * 0.28f;
            float yaw = 0f;

            if (form == AttackForm.Thrust)
            {
                range *= 1.25f;
                radius *= 0.4f;
                cone = 18f;
                shape = HitZoneShape.Capsule;
                inner = 0f;
            }
            else if (form == AttackForm.Pommel)
            {
                range = Mathf.Min(range, CombatRangeTable.Default.Outer(CombatRange.Clinch));
                radius *= 0.35f;
                cone = 16f;
                shape = HitZoneShape.Capsule;
                inner = 0f;
            }
            else if (form == AttackForm.CloseHit)
            {
                range = Mathf.Min(range, CombatRangeTable.Default.Outer(CombatRange.PointBlank) + 0.35f);
                radius *= 0.85f;
                cone = 55f;
                shape = HitZoneShape.Sector;
                inner = 0f;
                yaw = 0f;
            }
            else if (form == AttackForm.Elbow)
            {
                range = Mathf.Min(range, CombatRangeTable.Default.Outer(CombatRange.Clinch));
                radius *= 0.7f;
                cone = 40f;
                shape = HitZoneShape.Sector;
                inner = 0f;
                yaw = 0f;
            }
            else if (form == AttackForm.Shoulder)
            {
                range = Mathf.Min(range, CombatRangeTable.Default.Outer(CombatRange.Clinch) + 0.15f);
                radius *= 0.9f;
                cone = 70f;
                shape = HitZoneShape.Sector;
                inner = 0f;
            }
            else
            {
                cone = 48f;
                inner = range * 0.28f;
                yaw = form == AttackForm.SlashLeft ? -42f : 42f;
            }

            if (fromBlock)
                range *= blockAttackRangeMult;

            if (form == AttackForm.CloseHit) { damageMult *= 0.88f; staggerMult *= 1.15f; }
            else if (form == AttackForm.Elbow) { damageMult *= 0.78f; staggerMult *= 0.85f; }
            else if (form == AttackForm.Pommel) { damageMult *= 0.7f; staggerMult *= 1.05f; }
            else if (form == AttackForm.Shoulder) { damageMult *= 0.9f; staggerMult *= 1.35f; }

            float damage = currentWeapon.damage * damageMult;
            damage += ComputeMomentumBonus();
            if (_isHeavyAttack && _stepCount > 0 && movement != null)
                damage += movement.CurrentSpeed * (resources != null ? resources.mass : 80f) * movementDamageCoefficient * (_pendingStepBoost ? 1.5f : 1f);

            ApplyAttackMoveMode();
            bool stepped = ApplyFootworkStep(form);

            if (_isHeavyAttack && !fromBlock && !stepped)
                TryLunge();

            BodyZone zone = _pendingZone;
            if (!_isHeavyAttack)
            {
                zone = BodyZone.Torso;
                if (form == AttackForm.CloseHit) zone = BodyZone.Torso;
                else if (form == AttackForm.SlashLeft || form == AttackForm.Elbow) zone = BodyZone.LeftArm;
                else if (form == AttackForm.SlashRight) zone = BodyZone.RightArm;
                else if (form == AttackForm.Thrust || form == AttackForm.Pommel) zone = BodyZone.Torso;
                else if (form == AttackForm.Shoulder) zone = BodyZone.Torso;
            }

            float pen = currentWeapon.penetration;
            float penScore = pen * (0.55f + ChargePercent * 0.9f);
            if (_isHeavyAttack) penScore *= 1.2f;
            if (_pendingStepBoost) penScore *= 1.15f;
            if (form == AttackForm.Thrust) penScore *= 1.2f;

            _prep.range = range;
            _prep.radius = radius;
            _prep.height = currentWeapon.attackHeight;
            _prep.offset = currentWeapon.hitboxOffset;
            _prep.dir = GetAttackDirection();
            _prep.damage = damage;
            _prep.stagger = currentWeapon.staggerForce * staggerMult;
            _prep.layers = currentWeapon.targetLayers;
            if (_prep.layers.value == 0 && EnemyLayers.value != 0)
                _prep.layers = EnemyLayers;
            _prep.tick = currentWeapon.tickInterval;
            _prep.cone = cone;
            _prep.shape = shape;
            _prep.innerRadius = inner;
            _prep.yawOffset = yaw;

            CombatRange hitBand = ResolveBandToFocus();
            _prep.info = new HitInfo
            {
                rawDamage = damage,
                finalDamage = damage,
                stagger = _prep.stagger,
                sourcePosition = transform.position,
                hitDirection = _prep.dir,
                zone = zone,
                intent = _isHeavyAttack ? _pendingIntent : HitIntent.Neutral,
                isHeavy = _isHeavyAttack,
                isInfight = IsInfightForm(form),
                band = IsInfightForm(form) && hitBand > CombatRange.Clinch ? CombatRange.Clinch : hitBand,
                stepBoost = _pendingStepBoost,
                chargePercent = ChargePercent,
                penetrationScore = penScore,
                weaponPenetration = pen
            };
        }

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

    void ActivatePreparedHitbox()
    {
        if (!_hitPrepared) return;
        _hitPrepared = false;

        if (hitbox != null && hitbox.visual != null)
            hitbox.visual.HideWindup();

        if (currentWeapon == null) return;

        _prep.dir = GetAttackDirection();
        _prep.info.sourcePosition = transform.position;
        _prep.info.hitDirection = _prep.dir;

        CombatRange band = _prep.info.band;
        if (band == CombatRange.PointBlank && !IsInfighting)
            band = _lastForm == AttackForm.Thrust ? CombatRange.Close : CombatRange.Mid;
        Transform aim = NearTarget != null ? NearTarget
            : (IsUsableTarget(AutoTarget) ? AutoTarget : CommandTarget);
        if (aim != null)
        {
            Vector3 d = aim.position - transform.position;
            d.y = 0f;
            CombatRange live = CombatRangeTable.Default.Band(d.magnitude);
            if (IsInfighting && live > CombatRange.Clinch) live = CombatRange.Clinch;
            band = live;
            _prep.info.band = live;
            _prep.info.isInfight = IsInfighting;
        }

        if (melee == null)
        {
            if (hitbox == null) return;
            hitbox.SetHitInfo(_prep.info);
            hitbox.Activate(
                _prep.range, _prep.radius, _prep.height, _prep.offset, _prep.dir,
                _prep.damage, _prep.stagger, _prep.layers, _prep.dur, _prep.tick,
                _prep.charge, _prep.combo, _prep.cone,
                _prep.shape, _prep.innerRadius, _prep.yawOffset);
            return;
        }

        melee.Play(new MeleeAction.Request
        {
            band = band,
            range = _prep.range,
            radius = _prep.radius,
            height = _prep.height,
            offset = _prep.offset,
            direction = _prep.dir,
            damage = _prep.damage,
            stagger = _prep.stagger,
            layers = _prep.layers,
            duration = _prep.dur,
            tick = _prep.tick,
            charge = _prep.charge,
            cone = _prep.cone,
            combo = _prep.combo,
            shape = _prep.shape,
            innerRadius = _prep.innerRadius,
            yawOffset = _prep.yawOffset,
            info = _prep.info,
            weapon = currentWeapon,
            target = aim
        });
    }

    AttackForm ChooseAttackForm(bool wasInCombo)
    {
        Transform aim = NearTarget != null ? NearTarget
            : (IsUsableTarget(AutoTarget) ? AutoTarget : CommandTarget);

        if (wasInCombo && _lastForm != AttackForm.Thrust && aim != null)
        {
            Vector3 to = aim.position - transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            float range = currentWeapon != null ? currentWeapon.Reach() : CombatRangeTable.Default.Outer(CombatRange.Mid);
            float thrustDist = Mathf.Max(CombatRangeTable.Default.Outer(CombatRange.Mid), range * 1.15f);
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

    void TickTargetMagnet()
    {
        if (movement == null) return;
        if (!IsArmed || ForcePeace) return;
        if (targetMagnetRange <= 0f || targetMagnetSpeed <= 0f) return;
        if (movement.IsDodging || movement.IsWeaponStuck) return;
        if (!AllowTargetMagnet()) return;

        Transform marked = CommandTarget;
        if (!IsUsableTarget(marked)) return;

        Vector3 to = marked.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist < 0.05f || dist > targetMagnetRange) return;

        Vector3 radial = to / dist;
        Vector3 input = movement.DesiredMoveDir;
        if (input.sqrMagnitude > 0.04f && Vector3.Dot(input.normalized, radial) < -0.25f)
            return;

        float ideal = CurrentIdealDistance();
        bool closeIn = WantsCloseIn(marked);
        float desired = closeIn
            ? CombatRangeTable.Default.Outer(CombatRange.Clinch) * infightHoldFraction
            : ideal;
        float error = dist - desired;
        if (Mathf.Abs(error) <= spacingDeadzone) return;
        if (error < 0f && (IsAttacking || Time.time < _noRadialPushUntil))
            return;

        float span = Mathf.Max(0.25f, targetMagnetRange - desired);
        float t = Mathf.Clamp01(Mathf.Abs(error) / span);
        Vector3 dir = error > 0f ? radial : -radial;
        movement.AddPlanarAssist(dir * (targetMagnetSpeed * t));
    }

    void TickShieldRam()
    {
        if (movement == null || !IsBlocking || !IsShieldArmed) return;
        if (ForcePeace || movement.IsDodging || movement.IsWeaponStuck) return;
        if (IsAttacking) return;

        Transform focus = ResolveAttackFocus();
        if (focus == null || !WantsCloseIn(focus)) return;

        Vector3 to = focus.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist < 0.05f) return;

        float clinch = CombatRangeTable.Default.Outer(CombatRange.Clinch);
        if (dist > CurrentIdealDistance() + 0.2f) return;

        Vector3 radial = to / dist;
        movement.AddPlanarAssist(radial * shieldRamAssist);

        if (dist > clinch + 0.2f) return;
        if (Time.time < _nextShieldRamTime) return;
        _nextShieldRamTime = Time.time + Mathf.Max(0.2f, shieldRamInterval);

        var dmg = focus.GetComponent<IDamageable>();
        if (dmg == null || !dmg.IsAlive) return;

        HitInfo ram = new HitInfo
        {
            rawDamage = shieldRamDamage,
            finalDamage = shieldRamDamage,
            stagger = shieldRamStagger,
            sourcePosition = transform.position,
            hitDirection = radial,
            zone = BodyZone.Torso,
            intent = HitIntent.ThrustLine,
            isInfight = true,
            band = CombatRangeTable.Default.Band(dist),
            weaponPenetration = 0.4f,
            penetrationScore = 0.4f
        };
        dmg.TakeHit(ram);
        dmg.ApplyKnockback(radial * shieldRamForce);
        SetTrig("ShieldBash");
    }

    Transform ResolveAttackFocus()
    {
        var latched = WerewolfCombat.FindClingingTo(transform);
        if (latched != null && IsUsableTarget(latched.transform))
            return latched.transform;

        float face = combatFaceRange;
        Transform marked = CommandTarget;
        if (IsUsableTarget(marked))
        {
            Vector3 toMarked = marked.position - transform.position;
            toMarked.y = 0f;
            if (toMarked.sqrMagnitude <= face * face)
                return marked;
        }
        return FindNearestInRadius(face);
    }

    bool ApplyFootworkStep(AttackForm form)
    {
        if (movement == null) return false;

        Transform focus = ResolveAttackFocus();
        if (focus != null)
        {
            AutoTarget = focus;
            movement.SnapRotationToTarget(focus.position);

            Vector3 to = focus.position - transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist < 0.05f) return false;

            bool closeIn = WantsCloseIn(focus);
            if (!_isHeavyAttack && HasManualMoveInput() && !closeIn) return false;

            float reach = currentWeapon != null
                ? currentWeapon.ScaledRange
                : 2f * WeaponData.RangeScale;
            float ideal = reach * (form == AttackForm.Thrust || form == AttackForm.Pommel
                ? spacingThrustFraction
                : spacingIdealFraction);
            float desired = closeIn
                ? CombatRangeTable.Default.Outer(CombatRange.Clinch) * infightHoldFraction
                : ideal;
            float error = dist - desired;
            Vector3 dir = to / dist;
            float dur = spacingDuration > 0.05f ? spacingDuration : 0.18f;
            float cap = FootworkCap();
            if (error <= spacingDeadzone)
            {
                if (!_isHeavyAttack) return false;
                float hop = cap * 0.35f;
                movement.AddLungeSpeed(dir, hop / dur, dur);
                return true;
            }

            float travel = Mathf.Min(error, cap);
            movement.AddLungeSpeed(dir, travel / dur, dur);
            return true;
        }

        if (!_isHeavyAttack && HasManualMoveInput()) return false;
        Vector3 attackDir = GetAttackDirection();
        if (attackDir.sqrMagnitude < 0.01f) return false;
        float emptyDur = spacingDuration > 0.05f ? spacingDuration : 0.18f;
        float emptyCap = FootworkCap();
        movement.AddLungeSpeed(attackDir, emptyCap / emptyDur, emptyDur);
        return true;
    }

    float FootworkCap()
    {
        return _isHeavyAttack ? spacingHeavyStep : spacingMaxStep;
    }

    float CurrentIdealDistance()
    {
        WeaponData weapon = currentWeapon != null ? currentWeapon
            : (loadout != null ? loadout.GetMainWeapon() : null);
        float reach = weapon != null ? weapon.ScaledRange : 2f * WeaponData.RangeScale;
        return reach * spacingIdealFraction;
    }

    bool WantsCloseIn(Transform focus)
    {
        var latched = WerewolfCombat.FindClingingTo(transform);
        if (latched != null && focus == latched.transform) return true;
        if (focus == null || !HasManualMoveInput()) return false;
        Vector3 to = focus.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return false;
        Vector3 input = movement != null ? movement.DesiredMoveDir : Vector3.zero;
        if (input.sqrMagnitude < 0.04f) return false;
        if (Vector3.Dot(input.normalized, to.normalized) < 0.35f) return false;
        return IsCharging || IsInAttackPipeline || IsBlocking;
    }

    float RangeTempo()
    {
        Transform focus = ResolveAttackFocus();
        if (focus == null) return 1f;
        Vector3 to = focus.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        float reach = currentWeapon != null
            ? currentWeapon.ScaledRange
            : 2f * WeaponData.RangeScale;
        if (dist > reach + 0.2f) return 1.25f;
        if (InInfightRange(focus) || dist <= CombatRangeTable.Default.Outer(CombatRange.Clinch))
            return 0.85f;
        if (dist <= reach) return 0.72f;
        return 1f;
    }

    bool InInfightRange(Transform focus)
    {
        if (focus == null) return false;
        Vector3 to = focus.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        return dist < CurrentIdealDistance() - spacingDeadzone * 0.35f;
    }

    static bool IsInfightForm(AttackForm form)
    {
        return form == AttackForm.CloseHit
            || form == AttackForm.Elbow
            || form == AttackForm.Pommel
            || form == AttackForm.Shoulder;
    }

    AttackForm PreviewInfightForm(AttackForm? forcedForm)
    {
        AttackForm form;
        if (forcedForm.HasValue) form = forcedForm.Value;
        else if (_isHeavyAttack && _pendingIntent == HitIntent.ThrustLine) form = AttackForm.Thrust;
        else form = ChooseAttackForm(Time.time <= _comboExpire);
        return RemapInfightForm(form);
    }

    AttackForm RemapInfightForm(AttackForm form)
    {
        Transform focus = ResolveAttackFocus();
        var latched = WerewolfCombat.FindClingingTo(transform);
        if (latched != null && focus == latched.transform)
            return AttackForm.CloseHit;
        if (!InInfightRange(focus)) return form;
        if (form == AttackForm.CloseHit || IsInfightForm(form)) return form;
        if (_isHeavyAttack || form == AttackForm.Shoulder) return AttackForm.Shoulder;
        if (form == AttackForm.Thrust || form == AttackForm.Pommel) return AttackForm.Pommel;
        return AttackForm.Elbow;
    }

    CombatRange ResolveBandToFocus()
    {
        Transform focus = ResolveAttackFocus();
        if (focus == null) return CombatRange.Mid;
        Vector3 to = focus.position - transform.position;
        to.y = 0f;
        return CombatRangeTable.Default.Band(to.magnitude);
    }

    public void ReceiveHitShock(bool heavy)
    {
        if (IsBlocking || IsParrying) return;

        if (IsCharging) CancelCharge();
        if (IsAttacking)
        {
            if (melee != null) melee.Stop();
            if (hitbox != null)
            {
                if (hitbox.visual != null) hitbox.visual.HideWindup();
                hitbox.Deactivate();
            }
            EndAttack();
        }

        _shockUntil = Time.time + (heavy ? shockHeavy : shockLight);
        SetTrig("HitReact");
    }

    void EndAttack()
    {
        IsAttacking = false;
        IsWindingUp = false;
        _hitPrepared = false;
        IsInfighting = false;
        SetB("Infight", false);
        AutoTarget = null;
        _canStickThisAttack = false;
        if (hitbox != null && hitbox.visual != null) hitbox.visual.HideWindup();
        if (stance != null) stance.PulseCurrent();
    }

    void TickCombatState()
    {
        if (ForcePeace)
        {
            IsInCombat = false;
            _combatLingerUntil = 0f;
            return;
        }

        bool active = IsArmed && (NearTarget != null || IsInAttackPipeline || IsBlocking);
        if (active)
            _combatLingerUntil = Time.time + combatLingerSeconds;

        IsInCombat = IsArmed && Time.time < _combatLingerUntil;
    }

    public void EnterForcePeace()
    {
        ForcePeace = true;
        IsInCombat = false;
        _combatLingerUntil = 0f;
        if (IsCharging) CancelCharge();
        SheathAll();
        if (stance != null) stance.ResetToNeutral();
        ClearTarget();
        SetB("Combat", false);
        SetTrig("ToPeace");
    }

    public void ToggleSword()
    {
        if (IsArmed) SheathSword();
        else DrawSword();
    }

    public void ToggleShield()
    {
        if (IsShieldArmed) SheathShield();
        else DrawShield();
    }

    public void DrawSword()
    {
        ForcePeace = false;
        if (IsArmed) return;
        CancelInvoke(nameof(ApplySheathSwordVisual));
        IsArmed = true;
        SetB("Armed", true);
        SetTrig("Draw");
        if (weaponVisual != null) weaponVisual.SetSwordDrawn();
    }

    public void SheathSword()
    {
        if (!IsArmed) return;
        if (IsCharging) CancelCharge();
        IsArmed = false;
        if (stance != null) stance.ResetToNeutral();
        ClearTarget();
        SetB("Armed", false);
        SetTrig("Sheath");
        CancelInvoke(nameof(ApplySheathSwordVisual));
        Invoke(nameof(ApplySheathSwordVisual), sheathVisualDelay);
    }

    void ApplySheathSwordVisual()
    {
        if (weaponVisual != null) weaponVisual.SetSwordSheathed();
    }

    public void DrawShield()
    {
        if (IsShieldArmed) return;
        CancelInvoke(nameof(ApplySheathShieldVisual));
        IsShieldArmed = true;
        SetB("ShieldArmed", true);
        SetTrig("DrawShield");
        if (weaponVisual != null) weaponVisual.SetShieldDrawn();
    }

    public void SheathShield()
    {
        if (!IsShieldArmed) return;
        IsShieldArmed = false;
        IsBlocking = false;
        SetB("ShieldBlock", false);
        SetB("ShieldArmed", false);
        SetTrig("SheathShield");
        CancelInvoke(nameof(ApplySheathShieldVisual));
        Invoke(nameof(ApplySheathShieldVisual), sheathVisualDelay);
    }

    void ApplySheathShieldVisual()
    {
        if (weaponVisual != null) weaponVisual.SetShieldSheathed();
    }

    public void DrawAll()
    {
        DrawShield();
        DrawSword();
    }

    public void SheathAll()
    {
        SheathSword();
        SheathShield();
    }

    public void ToggleArmed() => ToggleSword();
    public void DrawWeapon() => DrawSword();
    public void SheathWeapon() => SheathSword();

    void ApplyWeaponVisualsImmediate()
    {
        if (weaponVisual == null) return;
        if (IsArmed) weaponVisual.SetSwordDrawn();
        else weaponVisual.SetSwordSheathed();
        if (IsShieldArmed) weaponVisual.SetShieldDrawn();
        else weaponVisual.SetShieldSheathed();
    }

    protected Vector3 GetAttackDirection()
    {
        Transform aim = NearTarget != null ? NearTarget
            : (IsUsableTarget(AutoTarget) ? AutoTarget : null);
        if (aim != null)
        {
            float effectiveRange = currentWeapon != null ? currentWeapon.Reach() : 15f;
            Vector3 diff = aim.position - transform.position;
            diff.y = 0f;
            if (diff.magnitude > effectiveRange)
            {
                Transform nearest = FindNearestInRadius(combatFaceRange);
                if (nearest != null) aim = nearest;
            }

            Vector3 dir = aim.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }

        Vector3 aimDir = AimDirection;
        aimDir.y = 0f;
        if (aimDir.sqrMagnitude > 0.01f) return aimDir.normalized;
        return transform.forward;
    }
}
