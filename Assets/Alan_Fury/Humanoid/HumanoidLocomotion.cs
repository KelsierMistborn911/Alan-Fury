using UnityEngine;

[System.Serializable]
public struct GaitConfig
{
    public float speed;
    public float acceleration;
    public float deceleration;
    public float stepDistance;
    public float stepDuration;
    public float stepFrequency;
    public float stepHop;
}

/// <summary>
/// Мотор гуманоида (человек, скелет, игрок). Без Input / Camera.
/// Драйвер каждый кадр задаёт DesiredMoveDir + GaitLevel + FaceDir.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class HumanoidLocomotion : MonoBehaviour
{
    [Header("Режимы движения")]
    public GaitConfig walk = new GaitConfig
    {
        speed = 12.2f,
        acceleration = 90f,
        deceleration = 80f,
        stepDistance = 2.1f,
        stepDuration = 0.18f,
        stepFrequency = 5.0f,
        stepHop = 0f
    };

    public GaitConfig run = new GaitConfig
    {
        speed = 10f,
        acceleration = 28f,
        deceleration = 20f,
        stepDistance = 1.75f,
        stepDuration = 0.16f,
        stepFrequency = 3.6f,
        stepHop = 0f
    };

    public GaitConfig sprint = new GaitConfig
    {
        speed = 12.5f,
        acceleration = 95f,
        deceleration = 55f,
        stepDistance = 1.85f,
        stepDuration = 0.18f,
        stepFrequency = 3.8f,
        stepHop = 0f
    };

    [Header("Боевые режимы движения")]
    public GaitConfig combatWalk = new GaitConfig
    {
        speed = 14.3f,
        acceleration = 80f,
        deceleration = 70f,
        stepDistance = 3.74f,
        stepDuration = 0.23f,
        stepFrequency = 4.3f,
        stepHop = 0f
    };

    public GaitConfig combatRun = new GaitConfig
    {
        speed = 14.9f,
        acceleration = 85f,
        deceleration = 70f,
        stepDistance = 2.25f,
        stepDuration = 0.18f,
        stepFrequency = 5.2f,
        stepHop = 0f
    };

    public GaitConfig combatSprint = new GaitConfig
    {
        speed = 12.5f,
        acceleration = 95f,
        deceleration = 55f,
        stepDistance = 2.05f,
        stepDuration = 0.18f,
        stepFrequency = 3.7f,
        stepHop = 0f
    };

    [Header("Скрытность")]
    public GaitConfig sneakWalk = new GaitConfig
    {
        speed = 2.2f,
        acceleration = 20f,
        deceleration = 30f,
        stepDistance = 0.55f,
        stepDuration = 0.40f,
        stepFrequency = 1.8f,
        stepHop = 0f
    };
    public GaitConfig sneakRun = new GaitConfig
    {
        speed = 5.0f,
        acceleration = 18f,
        deceleration = 22f,
        stepDistance = 0.85f,
        stepDuration = 0.28f,
        stepFrequency = 2.6f,
        stepHop = 0f
    };
    public GaitConfig sneakSprint = new GaitConfig
    {
        speed = 7.5f,
        acceleration = 14f,
        deceleration = 20f,
        stepDistance = 1.05f,
        stepDuration = 0.22f,
        stepFrequency = 3.2f,
        stepHop = 0f
    };

    [Header("Штраф за движение боком и спиной")]
    public float strafeAngle = 45f;
    public float backAngle = 135f;
    public float strafeSpeedMultiplier = 1f;
    public float backSpeedMultiplier = 1f;
    public float combatStrafeMultiplier = 1f;
    public float combatBackMultiplier = 1f;

    [Header("Уворот")]
    public float dodgeSpeed = 56f;
    public float dodgeDuration = 0.18f;
    public float dodgeCooldown = 0.5f;
    [Tooltip("Максимальный доворот WASD за секунду во время уворота.")]
    public float dodgeSteerDegrees = 180f;
    [Range(0f, 1f)]
    [Tooltip("Доля пиковой скорости, которая остаётся после уворота.")]
    public float dodgeExitCarry = 0.35f;

    [Header("Перекат")]
    public float rollSpeed = 11f;
    public float rollDuration = 0.4f;
    public float rollCooldown = 1.2f;

    [Header("Вичфаер-телепорт")]
    public WitchLight witchLight;
    public float teleportDistance = 5f;

    [Header("Паркур (перепрыгивание)")]
    public LayerMask vaultLayers;
    public float vaultMaxHeight = 1.2f;
    public float vaultCheckDistance = 0.8f;
    public float vaultDuration = 0.45f;
    public float vaultForwardSpeed = 7f;
    public float vaultRise = 1.5f;
    public float vaultCooldown = 0.8f;

    [Header("Прочее")]
    [Tooltip("Старый Slerp-коэффициент. Не используется для лица — см. faceTurn*.")]
    public float rotationSpeed = 15f;
    [Tooltip("Старый Slerp-коэффициент спринта. Не используется для лица — см. sprintFaceTurn*.")]
    public float sprintTurnSpeed = 9f;
    [Tooltip("Время догона корпуса к цели (сек). 0.05–0.08 как TLOU / Max Payne.")]
    public float faceTurnSmooth = 0.055f;
    [Tooltip("Потолок поворота deg/s. 180° ≈ 0.25с.")]
    public float faceTurnRate = 720f;
    [Tooltip("Спринт: чуть больше инерции, чтобы не щёлкать на месте.")]
    public float sprintFaceTurnSmooth = 0.08f;
    public float sprintFaceTurnRate = 520f;
    public float sprintSteerDegrees = 170f;
    public float sprintAirSteer = 4f;
    public float stepAirSteer = 18f;
    [Tooltip("Боевой шаг в полёте почти не рулится.")]
    public float combatStepAirSteer = 6f;
    public float combatRedirectAngle = 32f;
    [Range(0f, 1f)] public float stepPlantFloor = 0.70f;
    public float stepRedirectAngle = 38f;
    public float turnSideKill = 90f;
    [Range(0.2f, 1f)] public float turnRecoverStart = 0.92f;
    [Range(0.2f, 1f)] public float sprintTurnRecoverStart = 0.66f;
    public float gravity = -20f;

    [Header("Анимация")]
    public Animator animator;
    public float moveThreshold = 0.15f;
    public float moveDirForwardAngle = 45f;
    public float turnInPlaceThreshold = 15f;
    public float walkStartDelay = 0f;
    [Range(0f, 1f)] public float walkStartSpeedFactor = 1f;
    public float mouseLookTimeout = 2f;

    [Header("Граница карты (опционально)")]
    public MapBoundary boundary;

    protected CharacterController Controller;
    protected HumanoidCombat Combat;
    protected PlayerResources Resources;

    private Vector3 _velocity;
    private float _verticalVelocity;
    private GaitConfig _currentGait;
    private int _currentGaitLevel = 2;
    private bool _inCombat;
    private GaitConfig _stepSlowRef;
    private GaitConfig _stepFastRef;
    private float _walkStartTimer;
    private float _startTurnAngle;
    private bool _hadMoveInput;

    private bool _isDodging;
    private bool _isRolling;
    private float _maneuverTimer;
    private float _maneuverSpeed;
    private Vector3 _maneuverDir;
    private float _lastDodgeTime;
    private float _lastRollTime;
    private int _arcSign;
    private Vector3 _arcCenter;
    private float _lastDodgeEndTime = -99f;

    public bool IsDodging => _isDodging;
    public float DodgeTimeRemaining => _isDodging ? _maneuverTimer : 0f;
    public float DodgeProgress01 => _isDodging ? 1f - Mathf.Clamp01(_maneuverTimer / dodgeDuration) : 1f;
    public float TimeSinceDodgeEnd => Time.time - _lastDodgeEndTime;
    public float DodgeSpeedValue => dodgeSpeed;
    public float CurrentSpeed => _velocity.magnitude;
    public bool IsSneaking { get; private set; }
    public int CurrentGaitLevel => _currentGaitLevel;
    public bool IsDead { get; private set; }

    public void StopHorizontalVelocity() => _velocity = Vector3.zero;

    private float _stuckTimer;
    private Vector3 _stuckDir;
    private float _stuckPullAccum;
    private float _stuckSpeedMult = 0.45f;
    private float _stuckForwardExtraMult = 0.25f;
    private float _stuckPullFreeTime = 0.22f;

    public bool IsWeaponStuck => _stuckTimer > 0f;

    public void EnterWeaponStuck(float duration, Vector3 embedDir,
        float speedMult = 0.45f, float forwardExtraMult = 0.25f, float pullFreeTime = 0.22f)
    {
        embedDir.y = 0f;
        if (embedDir.sqrMagnitude < 0.01f) embedDir = transform.forward;
        _stuckDir = embedDir.normalized;
        _stuckTimer = Mathf.Max(0.1f, duration);
        _stuckPullAccum = 0f;
        _stuckSpeedMult = Mathf.Clamp(speedMult, 0.1f, 1f);
        _stuckForwardExtraMult = Mathf.Clamp(forwardExtraMult, 0.05f, 1f);
        _stuckPullFreeTime = Mathf.Max(0.05f, pullFreeTime);
    }

    public void ClearWeaponStuck()
    {
        _stuckTimer = 0f;
        _stuckPullAccum = 0f;
    }

    public void AddLungeSpeed(Vector3 dir, float speed)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f || speed <= 0f) return;
        _velocity = dir.normalized * speed;
    }

    private Vector3 _planarAssist;

    public void AddPlanarAssist(Vector3 velocity)
    {
        velocity.y = 0f;
        if (velocity.sqrMagnitude < 0.0001f) return;
        _planarAssist += velocity;
    }

    /// <summary>Последняя команда движения (мир, горизонталь). Ноль — стоим.</summary>
    public Vector3 DesiredMoveDir { get; private set; }
    public Vector3 FaceDir { get; private set; }
    public Vector3 InputDirection => DesiredMoveDir;

    public void SnapRotationToTarget(Vector3 targetPosition)
    {
        Vector3 look = targetPosition - transform.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            transform.rotation = Quaternion.LookRotation(look);
            _yawVel = 0f;
        }
    }

    private bool _isVaulting;
    private float _vaultTimer;
    private float _lastVaultTime;
    private Vector3 _vaultDir;

    private readonly StepController _step = new StepController();
    private Vector3 _stepDir;
    private float _stepTravel;
    private float _stepDur = 0.0001f;
    private float _stepHopAmt;
    private float _turnRecover = 1f;
    private float _yawVel;

    private Transform LockTarget => Combat != null ? Combat.NearTarget : null;

    private bool _cmdSet;
    private bool _allowVault;

    public void SetMove(Vector3 worldDir, int gaitLevel, bool sneaking)
    {
        worldDir.y = 0f;
        DesiredMoveDir = worldDir.sqrMagnitude > 0.01f ? worldDir.normalized : Vector3.zero;
        _currentGaitLevel = Mathf.Clamp(gaitLevel, 1, 3);
        IsSneaking = sneaking;
        _allowVault = _currentGaitLevel == 3;
        _cmdSet = true;
    }

    public void SetFace(Vector3 worldDir)
    {
        worldDir.y = 0f;
        FaceDir = worldDir.sqrMagnitude > 0.01f ? worldDir.normalized : Vector3.zero;
    }

    public bool TryDodge(Vector3 worldDir)
    {
        if (IsDead || _isVaulting || _isDodging || _isRolling) return false;
        if (Time.time - _lastDodgeTime <= dodgeCooldown) return false;
        if (Combat != null && Combat.IsCharging) return false;
        worldDir.y = 0f;
        if (worldDir.sqrMagnitude < 0.01f) return false;
        _maneuverDir = worldDir.normalized;
        SetupLockManeuver(true);
        StartManeuver(dodgeSpeed, dodgeDuration, ref _isDodging, ref _lastDodgeTime);
        return true;
    }

    public bool TryRoll(Vector3 worldDir)
    {
        if (IsDead || _isVaulting || _isDodging || _isRolling) return false;
        if (Time.time - _lastRollTime <= rollCooldown) return false;
        if (Combat != null && Combat.IsCharging) return false;
        worldDir.y = 0f;
        if (worldDir.sqrMagnitude < 0.01f) worldDir = transform.forward;
        _maneuverDir = worldDir.normalized;
        SetupLockManeuver(false);
        StartManeuver(rollSpeed, rollDuration, ref _isRolling, ref _lastRollTime);
        if (Combat != null) Combat.ClearTarget();
        if (_maneuverDir.sqrMagnitude > 0.01f)
        {
            transform.rotation = Quaternion.LookRotation(_maneuverDir);
            _yawVel = 0f;
        }
        ApplyManeuverAnimDirection(_maneuverDir);
        if (animator != null)
        {
            if (HasParam("Roll")) animator.SetTrigger("Roll");
            if (HasParam("Rolling")) animator.SetBool("Rolling", true);
        }
        return true;
    }

    public void Teleport(Vector3 worldDir)
    {
        worldDir.y = 0f;
        if (worldDir.sqrMagnitude < 0.01f) worldDir = transform.forward;
        _maneuverDir = worldDir.normalized;
        _lastRollTime = Time.time;
        _step.Cancel();
        if (Combat != null) Combat.ClearTarget();
        MoveHorizontal(_maneuverDir * teleportDistance);
    }

    protected virtual void Start()
    {
        Controller = GetComponent<CharacterController>();
        _currentGait = run;
        Combat = GetComponent<HumanoidCombat>();
        Resources = GetComponent<PlayerResources>();
        if (Resources != null) Resources.onDeath += HandleDeath;

        if (boundary == null) boundary = GetComponent<MapBoundary>();
        if (witchLight == null) witchLight = GetComponentInChildren<WitchLight>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        CacheAnimParams();
    }

    private System.Collections.Generic.HashSet<string> _animParams;

    private void CacheAnimParams()
    {
        _animParams = new System.Collections.Generic.HashSet<string>();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters) _animParams.Add(p.name);
    }

    protected bool HasParam(string name) =>
        animator != null && _animParams != null && _animParams.Contains(name);

    void OnDestroy()
    {
        if (Resources != null) Resources.onDeath -= HandleDeath;
    }

    void HandleDeath()
    {
        IsDead = true;
        _isDodging = false;
        _isRolling = false;
        if (HasParam("Death")) animator.SetTrigger("Death");
        if (HasParam("Moving")) animator.SetBool("Moving", false);
    }

    void LateUpdate()
    {
        if (IsDead)
        {
            ApplyGravity();
            return;
        }

        if (_isVaulting)
        {
            TickVault();
            return;
        }

        TickManeuver();

        if (_isDodging || _isRolling)
        {
            ApplyGravity();
            return;
        }

        ApplyGait();
        TickWeaponStuck();
        TickMovement();
        ApplyGravity();

        if (!_cmdSet)
            DesiredMoveDir = Vector3.zero;
        _cmdSet = false;
    }

    void TickWeaponStuck()
    {
        if (_stuckTimer <= 0f) return;
        _stuckTimer -= Time.deltaTime;
        if (_stuckTimer <= 0f)
            ClearWeaponStuck();
    }

    protected void MoveHorizontal(Vector3 delta)
    {
        if (boundary != null && boundary.IsReady)
            delta = boundary.Constrain(transform.position, delta);

        Controller.Move(delta);
    }

    void TickManeuver()
    {
        if (!_isDodging && !_isRolling) return;

        _maneuverTimer -= Time.deltaTime;
        if (_maneuverTimer <= 0f)
        {
            if (_isDodging)
            {
                _lastDodgeEndTime = Time.time;
                _velocity = _maneuverDir * dodgeSpeed * dodgeExitCarry;
            }
            if (_isRolling && animator != null && HasParam("Rolling"))
                animator.SetBool("Rolling", false);
            _isDodging = false;
            _isRolling = false;
            _arcSign = 0;
            return;
        }

        if (_isDodging && DesiredMoveDir.sqrMagnitude > 0.01f)
        {
            _maneuverDir = Vector3.RotateTowards(
                _maneuverDir,
                DesiredMoveDir,
                dodgeSteerDegrees * Mathf.Deg2Rad * Time.deltaTime,
                0f);
            _arcSign = 0;
        }
        else if (_arcSign != 0)
        {
            Vector3 toCenter = _arcCenter - transform.position;
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude > 0.01f)
                _maneuverDir = Vector3.Cross(Vector3.up, toCenter.normalized) * _arcSign;
        }

        float speed = _maneuverSpeed;
        if (_isDodging)
        {
            float u = 1f - Mathf.Clamp01(_maneuverTimer / dodgeDuration);
            speed *= Mathf.Lerp(1.05f, 0.22f, u * u);
        }

        _velocity = _maneuverDir * speed;
        MoveHorizontal(_maneuverDir * speed * Time.deltaTime);
    }

    void ApplyManeuverAnimDirection(Vector3 worldDir)
    {
        if (animator == null || worldDir.sqrMagnitude < 0.01f) return;
        worldDir.y = 0f;
        worldDir.Normalize();

        float moveAngle = Vector3.SignedAngle(transform.forward, worldDir, Vector3.up);
        Vector3 local = transform.InverseTransformDirection(worldDir);

        if (HasParam("MoveAngle")) animator.SetFloat("MoveAngle", moveAngle);
        if (HasParam("MoveDir")) animator.SetInteger("MoveDir", ComputeMoveDir(moveAngle));
        if (HasParam("MoveX")) animator.SetFloat("MoveX", local.x);
        if (HasParam("MoveZ")) animator.SetFloat("MoveZ", local.z);
        if (HasParam("Moving")) animator.SetBool("Moving", true);
    }

    void SetupLockManeuver(bool allowArc)
    {
        _arcSign = 0;
        Transform lockT = LockTarget;
        if (lockT == null) return;

        Vector3 toEnemy = lockT.position - transform.position;
        toEnemy.y = 0f;
        if (toEnemy.sqrMagnitude < 0.01f) return;
        toEnemy.Normalize();
        Vector3 tangent = Vector3.Cross(Vector3.up, toEnemy);

        float side = Vector3.Dot(_maneuverDir, tangent);
        float axial = Vector3.Dot(_maneuverDir, toEnemy);

        if (allowArc && Mathf.Abs(side) > Mathf.Abs(axial))
        {
            _arcSign = side >= 0f ? 1 : -1;
            _arcCenter = lockT.position;
        }
        else if (!allowArc)
            _maneuverDir = (axial >= 0f ? toEnemy : -toEnemy);
    }

    void StartManeuver(float speed, float duration, ref bool flag, ref float lastTime)
    {
        flag = true;
        lastTime = Time.time;
        _maneuverTimer = duration;
        _maneuverSpeed = speed;
        _velocity = _maneuverDir * speed;
        _step.Cancel();
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (_isVaulting || _isDodging || _isRolling) return;
        if (!_allowVault) return;
        if (Time.time - _lastVaultTime < vaultCooldown) return;
        if ((vaultLayers.value & (1 << hit.gameObject.layer)) == 0) return;
        if (Mathf.Abs(hit.normal.y) > 0.3f) return;

        Vector3 flatVel = _velocity; flatVel.y = 0f;
        if (flatVel.sqrMagnitude < 0.1f) return;
        Vector3 into = -hit.normal; into.y = 0f; into.Normalize();
        if (Vector3.Dot(flatVel.normalized, into) < 0.5f) return;

        float feetY = transform.position.y - Controller.height * 0.5f + Controller.center.y;
        Vector3 origin = new Vector3(transform.position.x, feetY + vaultMaxHeight, transform.position.z);
        if (Physics.Raycast(origin, into, vaultCheckDistance, vaultLayers)) return;

        StartVault(into);
    }

    void StartVault(Vector3 dir)
    {
        _isVaulting = true;
        _lastVaultTime = Time.time;
        _vaultTimer = vaultDuration;
        _vaultDir = dir;
        _verticalVelocity = 0f;
        _step.Cancel();
    }

    void TickVault()
    {
        _vaultTimer -= Time.deltaTime;
        float t = 1f - Mathf.Clamp01(_vaultTimer / vaultDuration);
        float vUp = vaultRise * (Mathf.PI / vaultDuration) * Mathf.Cos(t * Mathf.PI);
        Vector3 step = _vaultDir * vaultForwardSpeed + Vector3.up * vUp;
        Controller.Move(step * Time.deltaTime);

        if (_vaultTimer <= 0f)
            _isVaulting = false;
    }

    void ApplyGait()
    {
        bool armed = Combat != null && Combat.IsArmed;
        bool blocking = Combat != null && Combat.IsBlocking;
        bool combatMode = Combat != null && Combat.IsInCombat;

        int gait = _currentGaitLevel;
        if (blocking && gait == 3) gait = 2;
        _currentGaitLevel = gait;

        if (IsSneaking)
        {
            _stepSlowRef = sneakWalk;
            _stepFastRef = gait == 3 ? sneakSprint : sneakRun;
            _currentGait = gait == 1 ? sneakWalk : _stepFastRef;
        }
        else
        {
            _stepSlowRef = combatMode ? combatWalk : walk;
            _stepFastRef = gait == 3
                ? (combatMode ? combatSprint : sprint)
                : (combatMode ? combatRun : run);
            _currentGait = gait == 1 ? _stepSlowRef : _stepFastRef;
        }

        if (animator != null)
        {
            bool hasInput = _hadMoveInput;
            bool isMoving = hasInput || _velocity.magnitude > moveThreshold;
            if (HasParam("Gait")) animator.SetInteger("Gait", gait);
            if (HasParam("Moving")) animator.SetBool("Moving", isMoving);
            if (HasParam("Combat")) animator.SetBool("Combat", combatMode);
            if (HasParam("Armed")) animator.SetBool("Armed", armed);
            if (HasParam("Sneaking")) animator.SetBool("Sneaking", IsSneaking);
            if (combatMode && !_inCombat && HasParam("CombatEnter")) animator.SetTrigger("CombatEnter");

            int battleStepLayer = animator.GetLayerIndex("Battle Step");
            if (battleStepLayer >= 0)
                animator.SetLayerWeight(battleStepLayer, combatMode ? 1f : 0f);

            if (HasParam("Turn"))
            {
                float turn = 0f;
                if (_walkStartTimer > 0f && gait == 1)
                    turn = _startTurnAngle;
                else if (!isMoving)
                    turn = ComputeStandingTurnAngle();
                animator.SetFloat("Turn", turn);
            }

            float moveAngle = 0f;
            float moveX = 0f;
            float moveZ = 0f;
            if (_velocity.sqrMagnitude > 0.01f)
            {
                Vector3 vel = _velocity.normalized;
                moveAngle = Vector3.SignedAngle(transform.forward, vel, Vector3.up);
                Vector3 local = transform.InverseTransformDirection(vel);
                moveX = local.x;
                moveZ = local.z;
            }
            if (float.IsNaN(moveX) || float.IsInfinity(moveX)) moveX = 0f;
            if (float.IsNaN(moveZ) || float.IsInfinity(moveZ)) moveZ = 0f;

            if (HasParam("MoveAngle")) animator.SetFloat("MoveAngle", moveAngle);
            if (HasParam("MoveDir"))
                animator.SetInteger("MoveDir", isMoving ? ComputeMoveDir(moveAngle) : 0);

            if (!HasParam("MoveX") || !HasParam("MoveZ"))
                CacheAnimParams();
            if (HasParam("MoveX")) animator.SetFloat("MoveX", moveX);
            if (HasParam("MoveZ")) animator.SetFloat("MoveZ", moveZ);
        }

        _inCombat = combatMode;
    }

    int ComputeMoveDir(float signedAngle)
    {
        float a = signedAngle;
        float abs = Mathf.Abs(a);
        if (abs <= moveDirForwardAngle) return 0;
        if (abs <= backAngle) return a < 0f ? 1 : 2;
        return 3;
    }

    float ComputeStandingTurnAngle()
    {
        Vector3 look = FaceDir;
        Transform aim = Combat != null ? Combat.ActiveAimTarget : null;
        if (aim != null)
            look = aim.position - transform.position;
        look.y = 0f;
        if (look.sqrMagnitude < 0.01f) return 0f;
        float angle = Vector3.SignedAngle(transform.forward, look.normalized, Vector3.up);
        return Mathf.Abs(angle) >= turnInPlaceThreshold ? angle : 0f;
    }

    void TickMovement()
    {
        Vector3 targetDir = DesiredMoveDir;
        bool hasInput = targetDir.sqrMagnitude > 0.01f;
        bool isSprinting = _currentGaitLevel == 3;
        bool wantMove = hasInput || isSprinting;

        bool startingWalk = hasInput && !_hadMoveInput && _velocity.magnitude <= moveThreshold
            && !isSprinting && _currentGaitLevel == 1 && !_inCombat;
        if (startingWalk)
        {
            _startTurnAngle = Vector3.SignedAngle(transform.forward, targetDir, Vector3.up);
            if (Mathf.Abs(_startTurnAngle) < turnInPlaceThreshold)
                _startTurnAngle = 0f;
            _walkStartTimer = walkStartDelay;
        }
        if (!hasInput || isSprinting || _currentGaitLevel != 1)
        {
            _walkStartTimer = 0f;
            _startTurnAngle = 0f;
        }
        else if (_walkStartTimer > 0f)
            _walkStartTimer -= Time.deltaTime;

        _hadMoveInput = wantMove;

        if (wantMove)
        {
            if (targetDir.sqrMagnitude < 0.01f)
                targetDir = transform.forward;

            _maneuverDir = targetDir;

            Vector3 headingRef = _velocity.sqrMagnitude > 0.25f ? _velocity : _stepDir;
            float headingErr = headingRef.sqrMagnitude > 0.01f
                ? Vector3.Angle(headingRef, targetDir)
                : 0f;
            float redirectAngle = _inCombat ? combatRedirectAngle : stepRedirectAngle;
            bool turning = headingErr >= redirectAngle;

            if (_inCombat && _step.IsActive)
            {
            }
            else if (turning)
            {
                _step.Cancel();
                BeginStep(targetDir);
                if (isSprinting)
                {
                    _turnRecover = sprintTurnRecoverStart;
                    float keptSpeed = Mathf.Max(0f, Vector3.Dot(_velocity, targetDir));
                    _velocity = targetDir * keptSpeed * sprintTurnRecoverStart;
                }
                else
                {
                    _turnRecover = 1f;
                    _velocity = targetDir * _velocity.magnitude;
                }
            }
            else if (!_step.IsActive)
            {
                BeginStep(targetDir);
                _turnRecover = isSprinting ? 1f : Mathf.MoveTowards(_turnRecover, 1f, 0.55f);
            }

            float speedMult = DirectionSpeedMultiplier(targetDir);
            if (_walkStartTimer > 0f)
                speedMult *= walkStartSpeedFactor;

            if (_stuckTimer > 0f)
            {
                float relativeDot = Vector3.Dot(targetDir.normalized, _stuckDir);

                speedMult *= _stuckSpeedMult;
                if (relativeDot > 0.25f)
                    speedMult *= _stuckForwardExtraMult;

                if (relativeDot < -0.2f || Mathf.Abs(relativeDot) < 0.35f)
                    _stuckPullAccum += Time.deltaTime;
                else
                    _stuckPullAccum = Mathf.Max(0f, _stuckPullAccum - Time.deltaTime * 0.5f);

                if (_stuckPullAccum >= _stuckPullFreeTime)
                    ClearWeaponStuck();
            }

            Vector3 moveDir = _stepDir.sqrMagnitude > 0.01f ? _stepDir : targetDir;
            if (_step.IsActive && !(_inCombat && !isSprinting))
            {
                float steer = isSprinting ? sprintAirSteer : stepAirSteer;
                moveDir = Vector3.Slerp(_stepDir, targetDir, steer * Time.deltaTime);
                moveDir.y = 0f;
                if (moveDir.sqrMagnitude > 0.0001f)
                    moveDir.Normalize();
                _stepDir = moveDir;
            }

            float pulse = stepPlantFloor + (1f - stepPlantFloor) * _step.Curve;
            float targetSpeed = _currentGait.speed * speedMult * pulse * _turnRecover;
            Vector3 targetVelocity = moveDir * targetSpeed;

            Vector3 along = moveDir * Vector3.Dot(_velocity, moveDir);
            Vector3 side = _velocity - along;
            side = Vector3.MoveTowards(side, Vector3.zero, turnSideKill * Time.deltaTime);

            float rate = isSprinting || targetVelocity.sqrMagnitude < along.sqrMagnitude
                ? (isSprinting && targetVelocity.sqrMagnitude >= along.sqrMagnitude
                    ? _currentGait.acceleration
                    : _currentGait.deceleration)
                : _currentGait.acceleration;
            if (isSprinting || targetVelocity.sqrMagnitude < along.sqrMagnitude)
                along = Vector3.MoveTowards(along, targetVelocity, rate * Time.deltaTime);
            else
                along = targetVelocity;
            _velocity = along + side;
            MoveHorizontal(_velocity * Time.deltaTime);
            _step.Tick(Time.deltaTime);

            HandleRotation(targetDir);
        }
        else if (_step.IsActive)
        {
            if (_inCombat)
            {
                float pulse = stepPlantFloor + (1f - stepPlantFloor) * _step.Curve;
                Vector3 targetVelocity = _stepDir * (_currentGait.speed * pulse);
                _velocity = Vector3.MoveTowards(
                    _velocity,
                    targetVelocity,
                    _currentGait.deceleration * Time.deltaTime);
                MoveHorizontal(_velocity * Time.deltaTime);
                _step.Tick(Time.deltaTime);
                HandleRotation(_stepDir);
            }
            else
            {
                _step.Cancel();
                HandleRotation();
                if (_velocity.magnitude > 0.05f)
                {
                    _velocity = Vector3.MoveTowards(
                        _velocity,
                        Vector3.zero,
                        _currentGait.deceleration * Time.deltaTime);
                    MoveHorizontal(_velocity * Time.deltaTime);
                }
                else
                    _velocity = Vector3.zero;
            }
        }
        else
        {
            _step.Tick(Time.deltaTime);
            HandleRotation();

            if (_velocity.magnitude > 0.05f)
            {
                _velocity = Vector3.MoveTowards(
                    _velocity,
                    Vector3.zero,
                    _currentGait.deceleration * Time.deltaTime);
                MoveHorizontal(_velocity * Time.deltaTime);
            }
            else
            {
                _velocity = Vector3.zero;
            }
        }

        ConsumePlanarAssist();
    }

    void ConsumePlanarAssist()
    {
        if (_planarAssist.sqrMagnitude < 0.0001f) return;
        MoveHorizontal(_planarAssist * Time.deltaTime);
        _planarAssist = Vector3.zero;
    }

    void BeginStep(Vector3 dir)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f)
            dir = transform.forward;
        dir.Normalize();

        _step.TryStart(_currentGait.speed, _stepSlowRef, _stepFastRef);
        if (!_step.IsActive) return;

        _stepDir = dir;
        _stepTravel = _currentGait.stepDistance;
        _stepDur = Mathf.Max(0.0001f, _currentGait.stepDuration);
        _stepHopAmt = 0f;
    }

    float DirectionSpeedMultiplier(Vector3 moveDir)
    {
        float angle = Vector3.Angle(transform.forward, moveDir);
        if (angle <= strafeAngle) return 1f;
        if (_inCombat)
            return angle <= backAngle ? combatStrafeMultiplier : combatBackMultiplier;
        return angle <= backAngle ? strafeSpeedMultiplier : backSpeedMultiplier;
    }

    void HandleRotation(Vector3 moveDir = default)
    {
        if (Combat != null && Combat.IsAttacking)
            return;

        bool isSprinting = _currentGaitLevel == 3;
        if (isSprinting && moveDir.sqrMagnitude > 0.01f)
        {
            ApplyFaceYaw(moveDir, sprintFaceTurnSmooth, sprintFaceTurnRate);
            return;
        }

        Transform aim = Combat != null ? Combat.ActiveAimTarget : null;
        if (aim != null)
        {
            Vector3 aimLook = aim.position - transform.position;
            aimLook.y = 0f;
            if (aimLook.sqrMagnitude > 0.01f)
                ApplyFaceYaw(aimLook, faceTurnSmooth, faceTurnRate);
            return;
        }

        if (FaceDir.sqrMagnitude > 0.01f)
        {
            ApplyFaceYaw(
                FaceDir,
                isSprinting ? sprintFaceTurnSmooth : faceTurnSmooth,
                isSprinting ? sprintFaceTurnRate : faceTurnRate);
        }
    }

    void ApplyFaceYaw(Vector3 lookDir, float smooth, float maxRate)
    {
        lookDir.y = 0f;
        if (lookDir.sqrMagnitude < 0.01f) return;

        float current = transform.eulerAngles.y;
        float target = Quaternion.LookRotation(lookDir).eulerAngles.y;
        if (smooth <= 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(lookDir);
            _yawVel = 0f;
            return;
        }

        float yaw = Mathf.SmoothDampAngle(
            current,
            target,
            ref _yawVel,
            smooth,
            maxRate);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    void ApplyGravity()
    {
        if (Controller.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
        else
            _verticalVelocity += gravity * Time.deltaTime;

        Controller.Move(new Vector3(0f, _verticalVelocity, 0f) * Time.deltaTime);
    }
}
