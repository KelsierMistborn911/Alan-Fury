using UnityEngine;

[System.Serializable]
public struct GaitConfig
{
    public float speed;          // базовая скорость (м/с)
    public float acceleration;   // разгон (выше = отзывчивее)
    public float deceleration;   // торможение (выше = резче, ниже = скользит)
    public float stepDistance;   // импульс шага поверх базовой скорости
    public float stepDuration;   // длина шага (сек)
    public float stepFrequency;  // шагов в секунду
}

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement3D : MonoBehaviour
{
    [Header("Режимы движения")]
    public GaitConfig walk = new GaitConfig
    {
        speed = 4f,
        acceleration = 25f,
        deceleration = 30f,   // тормозит почти мгновенно
        stepDistance = 0.5f,
        stepDuration = 0.35f,
        stepFrequency = 2.2f
    };

    public GaitConfig run = new GaitConfig
    {
        speed = 8f,
        acceleration = 18f,
        deceleration = 14f,   // чуть скользит
        stepDistance = 1.0f,
        stepDuration = 0.22f,
        stepFrequency = 3.2f
    };

    public GaitConfig sprint = new GaitConfig
    {
        speed = 13f,
        acceleration = 12f,
        deceleration = 6f,    // заметная инерция
        stepDistance = 1.6f,
        stepDuration = 0.16f,
        stepFrequency = 4.5f
    };

    [Header("Боевые режимы движения")]
    [Tooltip("Включаются, когда цель в боевой зоне или идёт замах/удар/блок. Пока копии обычных — под отдельные анимации.")]
    public GaitConfig combatWalk = new GaitConfig
    {
        speed = 4f,
        acceleration = 25f,
        deceleration = 30f,
        stepDistance = 0.5f,
        stepDuration = 0.35f,
        stepFrequency = 2.2f
    };

    public GaitConfig combatRun = new GaitConfig
    {
        speed = 8f,
        acceleration = 18f,
        deceleration = 14f,
        stepDistance = 1.0f,
        stepDuration = 0.22f,
        stepFrequency = 3.2f
    };

    public GaitConfig combatSprint = new GaitConfig
    {
        speed = 13f,
        acceleration = 12f,
        deceleration = 6f,
        stepDistance = 1.6f,
        stepDuration = 0.16f,
        stepFrequency = 4.5f
    };

    [Header("Штраф за движение боком и спиной")]
    [Tooltip("До этого угла между корпусом и движением скорость полная.")]
    public float strafeAngle = 45f;
    [Tooltip("Дальше этого угла движение считается спиной вперёд.")]
    public float backAngle = 135f;
    [Tooltip("Множитель скорости при движении боком.")]
    public float strafeSpeedMultiplier = 0.8f;
    [Tooltip("Множитель скорости при движении спиной вперёд.")]
    public float backSpeedMultiplier = 0.6f;

    [Header("Уворот")]
    public float dodgeSpeed = 14f;
    public float dodgeDuration = 0.25f;
    public float dodgeCooldown = 0.6f;

    [Header("Перекат")]
    public float rollSpeed = 11f;
    public float rollDuration = 0.4f;
    public float rollCooldown = 1.2f;

    [Header("Вичфаер-телепорт")]
    [Tooltip("Ссылка на WitchLight. Пока он горит (E), Space телепортирует вместо переката. Пусто → ищется на игроке/детях.")]
    public WitchLight witchLight;
    [Tooltip("Дальность мгновенного рывка (м). Упирается в стены и границу карты.")]
    public float teleportDistance = 5f;

    [Header("Паркур (перепрыгивание)")]
    [Tooltip("Слой препятствий, через которые можно переваливаться (объекты ObjectPlacer).")]
    public LayerMask vaultLayers;
    [Tooltip("Макс. высота препятствия над землёй, через которое ещё переваливаемся.")]
    public float vaultMaxHeight = 1.2f;
    [Tooltip("Дальность проверки 'не слишком ли высоко' над препятствием.")]
    public float vaultCheckDistance = 0.8f;
    public float vaultDuration = 0.45f;
    public float vaultForwardSpeed = 7f;
    [Tooltip("Высота дуги прыжка через препятствие.")]
    public float vaultRise = 1.5f;
    public float vaultCooldown = 0.8f;

    [Header("Прочее")]
    public float rotationSpeed = 15f;
    [Tooltip("Доворот корпуса на спринте: корпус рулится клавишами, мышь его не крутит. Ниже rotationSpeed — руление, а не разворот на месте.")]
    public float sprintTurnSpeed = 4f;
    public float gravity = -20f;

    [Header("Анимация")]
    [Tooltip("Аниматор. Параметры: Gait, Moving, Combat, Armed, Turn, MoveAngle, MoveDir, MoveX, MoveZ (для Blend Tree Run/Walk).")]
    public Animator animator;
    [Tooltip("Порог скорости, ниже которого игрок считается стоящим (м/с).")]
    public float moveThreshold = 0.15f;
    [Tooltip("Секторы MoveDir (°) от forward: |угол| ≤ этого → Forward. Дальше до backAngle → L/R, иначе Back.")]
    public float moveDirForwardAngle = 45f;
    [Tooltip("Минимальный угол (°) для записи Turn при стоянии. Меньше — микроповороты игнорируются.")]
    public float turnInPlaceThreshold = 15f;
    [Tooltip("Задержка первого шага с места (сек). Пока таймер > 0 скорость сильно снижена — под анимацию Idle→Walk / Walk Start.")]
    public float walkStartDelay = 0.28f;
    [Tooltip("Доля скорости во время walkStartDelay (0 = стоим на месте, 0.25 = четверть).")]
    [Range(0f, 1f)] public float walkStartSpeedFactor = 0.15f;
    [Tooltip("Если мышь не двигали дольше этого времени (сек) — корпус смотрит по направлению бега, а не на прицел.")]
    public float mouseLookTimeout = 2f;

    [Header("Граница карты (опционально)")]
    [Tooltip("Если не задана — берётся MapBoundary с этого же объекта.")]
    public MapBoundary boundary;

    // Компоненты
    private CharacterController _controller;
    private Camera _mainCamera;

    // Состояние движения
    private Vector3 _velocity;          // горизонтальная скорость
    private float _verticalVelocity;
    private GaitConfig _currentGait;
    private bool _isRunning;
    private bool _inCombat;             // цель в боевой зоне или идёт действие
    private GaitConfig _stepSlowRef;    // нижняя опора для StepController (шаг)
    private GaitConfig _stepFastRef;    // верхняя опора (бег/спринт того же набора)
    private float _walkStartTimer;      // >0 — старт с места, скорость режется
    private float _startTurnAngle;      // угол на момент старта шага (для Walk Start 180 и т.п.)
    private bool _hadMoveInput;         // был ли ввод в прошлом кадре
    private float _lastMouseMoveTime = -999f;
    private Vector3 _lastMousePos;

    // Манёвры
    private bool _isDodging;
    private bool _isRolling;
    private float _maneuverTimer;
    private float _maneuverSpeed;
    private Vector3 _maneuverDir;
    private float _lastDodgeTime;
    private float _lastRollTime;
    private int _arcSign; // уворот дугой: -1 влево, +1 вправо, 0 — прямая
    private Vector3 _arcCenter; // центр дуги, фиксируется в момент нажатия
    private float _lastDodgeEndTime = -99f;

    // ==== Для комбо "удар после уворота" в CombatController3D ====
    public bool IsDodging => _isDodging;
    public float DodgeTimeRemaining => _isDodging ? _maneuverTimer : 0f;
    public float DodgeProgress01 => _isDodging ? 1f - Mathf.Clamp01(_maneuverTimer / dodgeDuration) : 1f;
    public float TimeSinceDodgeEnd => Time.time - _lastDodgeEndTime;
    public float DodgeSpeedValue => dodgeSpeed;
    public float CurrentSpeed => _velocity.magnitude;

    public void StopHorizontalVelocity() => _velocity = Vector3.zero;

    // Выпад: разовая добавка скорости в момент удара. Отдельного состояния нет —
    // дальше HandleMovement сам гасит её ускорением текущей походки.
    public void AddLungeSpeed(Vector3 dir, float speed)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f || speed <= 0f) return;
        _velocity = dir.normalized * speed;
    }

    // Направление ввода WASD в мировых осях — боевому контроллеру для выпада.
    public Vector3 InputDirection => ComputeInputDirection();

    // Мгновенный (без сглаживания) разворот на цель — для рывкового удара назад.
    public void SnapRotationToTarget(Vector3 targetPosition)
    {
        Vector3 look = targetPosition - transform.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(look);
    }

    // Паркур
    private bool _isVaulting;
    private float _vaultTimer;
    private float _lastVaultTime;
    private Vector3 _vaultDir;

    // Шаги
    private readonly StepController _step = new StepController();

    // Боевой контроллер — для доворота на цель во время замаха
    private CombatController3D _combat;

    // Tab-таргет из боевого контроллера (null — лок-мода нет)
    private Transform LockTarget => _combat != null ? _combat.NearTarget : null;

    // ──────────────────────────────────────────────

    void Start()
    {
        _controller = GetComponent<CharacterController>();
        _mainCamera = Camera.main;
        _currentGait = walk;
        _combat = GetComponent<CombatController3D>();

        if (boundary == null) boundary = GetComponent<MapBoundary>();
        if (witchLight == null) witchLight = GetComponentInChildren<WitchLight>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        _lastMousePos = Input.mousePosition;
        _lastMouseMoveTime = Time.time; // при старте считаем мышь «свежей»
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

    private bool HasParam(string name) =>
        animator != null && _animParams != null && _animParams.Contains(name);

    void Update()
    {
        // Паркур владеет всем кадром: своя дуга, без гравитации и обычного движения
        if (_isVaulting)
        {
            TickVault();
            return;
        }

        HandleManeuvers();

        if (_isDodging || _isRolling)
        {
            ApplyGravity();
            return;
        }

        HandleGait();
        HandleMovement();
        ApplyGravity();
    }

    // ──────────────────────────────────────────────
    // Перемещение через границу карты
    // ──────────────────────────────────────────────

    // Все горизонтальные перемещения идут сюда: граница гасит движение
    // наружу в приграничной полосе и не пускает за край (в т.ч. перекатом).
    void MoveHorizontal(Vector3 delta)
    {
        if (boundary != null && boundary.IsReady)
            delta = boundary.Constrain(transform.position, delta);

        _controller.Move(delta);
    }

    // ──────────────────────────────────────────────
    // Манёвры
    // ──────────────────────────────────────────────

    void HandleManeuvers()
    {
        if (Input.GetKeyDown(KeyCode.LeftAlt)
            && Time.time - _lastDodgeTime > dodgeCooldown
            && !(_combat != null && _combat.IsCharging))
        {
            Vector3 dir = ComputeInputDirection();
            if (dir.magnitude > 0.1f)
            {
                _maneuverDir = dir;
                SetupLockManeuver(true); // уворот вбок при таргете — дугой вокруг цели
                StartManeuver(dodgeSpeed, dodgeDuration, ref _isDodging, ref _lastDodgeTime);
            }
        }
        else if (Input.GetKeyDown(KeyCode.Space)
            && Time.time - _lastRollTime > rollCooldown
            && !(_combat != null && _combat.IsCharging))
        {
            Vector3 dir = ComputeInputDirection();
            if (dir.magnitude > 0.1f)
            {
                _maneuverDir = dir;
                SetupLockManeuver(false); // перекат/телепорт — прямые, но в базисе врага
                if (witchLight != null && witchLight.IsOn)
                    Teleport();
                else
                {
                    StartManeuver(rollSpeed, rollDuration, ref _isRolling, ref _lastRollTime);
                    if (_combat != null) _combat.ClearTarget(); // перекат сбрасывает привязку
                    // Анимация переката: Trigger + Rolling. Gait остаётся 1/2/3 — Animator сам выбирает клип.
                    if (animator != null)
                    {
                        if (HasParam("Roll")) animator.SetTrigger("Roll");
                        if (HasParam("Rolling")) animator.SetBool("Rolling", true);
                    }
                }
            }
        }

        if (!_isDodging && !_isRolling) return;

        _maneuverTimer -= Time.deltaTime;
        if (_maneuverTimer <= 0f)
        {
            if (_isDodging) _lastDodgeEndTime = Time.time;
            if (_isRolling && animator != null && HasParam("Rolling"))
                animator.SetBool("Rolling", false);
            _isDodging = false;
            _isRolling = false;
            _arcSign = 0;
            return;
        }

        // Дуга: траектория зафиксирована при нажатии — окружность вокруг _arcCenter.
        if (_arcSign != 0)
        {
            Vector3 toCenter = _arcCenter - transform.position;
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude > 0.01f)
                _maneuverDir = Vector3.Cross(Vector3.up, toCenter.normalized) * _arcSign;
        }

        MoveHorizontal(_maneuverDir * _maneuverSpeed * Time.deltaTime);
    }

    // Направление ввода прямо сейчас (не устаревшее): всегда от камеры.
    // Общий расчёт для ходьбы (HandleMovement) и манёвров (HandleManeuvers).
    Vector3 ComputeInputDirection()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 input = new Vector3(h, 0f, v).normalized;
        if (input.magnitude < 0.1f) return Vector3.zero;

        if (_mainCamera == null) return input;
        Vector3 forward = _mainCamera.transform.forward;
        Vector3 right = _mainCamera.transform.right;
        forward.y = 0f; right.y = 0f;
        forward.Normalize(); right.Normalize();
        return (forward * input.z + right * input.x).normalized;
    }

    // При лок-цели раскладывает направление манёвра по осям «к врагу / вокруг врага».
    // allowArc: боковая составляющая доминирует → манёвр дугой (_arcSign), иначе прямая к/от цели.
    void SetupLockManeuver(bool allowArc)
    {
        _arcSign = 0;
        Transform lockT = LockTarget;
        if (lockT == null) return;

        Vector3 toEnemy = lockT.position - transform.position;
        toEnemy.y = 0f;
        if (toEnemy.sqrMagnitude < 0.01f) return;
        toEnemy.Normalize();
        Vector3 tangent = Vector3.Cross(Vector3.up, toEnemy); // вправо относительно взгляда на врага

        float side = Vector3.Dot(_maneuverDir, tangent);
        float axial = Vector3.Dot(_maneuverDir, toEnemy);

        if (allowArc && Mathf.Abs(side) > Mathf.Abs(axial))
        {
            _arcSign = side >= 0f ? 1 : -1;
            _arcCenter = lockT.position; // траектория фиксируется здесь
        }
        else
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

    // Мгновенный рывок вместо переката, пока горит вичфаер.
    // Кулдаун общий с перекатом (_lastRollTime / rollCooldown).
    void Teleport()
    {
        _lastRollTime = Time.time;
        _step.Cancel();
        if (_combat != null) _combat.ClearTarget();
        MoveHorizontal(_maneuverDir * teleportDistance);
    }

    // ──────────────────────────────────────────────
    // Паркур: перепрыгивание при столкновении на спринте
    // ──────────────────────────────────────────────

    // Срабатывает, когда CharacterController упирается в коллайдер.
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (_isVaulting || _isDodging || _isRolling) return;
        if (!Input.GetKey(KeyCode.LeftShift)) return;                 // только на спринте
        if (Time.time - _lastVaultTime < vaultCooldown) return;
        if ((vaultLayers.value & (1 << hit.gameObject.layer)) == 0) return;

        // Бьёмся в стену, а не в пол/потолок
        if (Mathf.Abs(hit.normal.y) > 0.3f) return;

        // Двигаемся именно в препятствие
        Vector3 flatVel = _velocity; flatVel.y = 0f;
        if (flatVel.sqrMagnitude < 0.1f) return;
        Vector3 into = -hit.normal; into.y = 0f; into.Normalize();
        if (Vector3.Dot(flatVel.normalized, into) < 0.5f) return;

        // Препятствие низкое? Луч над его макс. высотой — если пусто, переваливаемся
        float feetY = transform.position.y - _controller.height * 0.5f + _controller.center.y;
        Vector3 origin = new Vector3(transform.position.x, feetY + vaultMaxHeight, transform.position.z);
        if (Physics.Raycast(origin, into, vaultCheckDistance, vaultLayers)) return;  // слишком высокое

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
        float t = 1f - Mathf.Clamp01(_vaultTimer / vaultDuration);  // 0 → 1

        // Производная sin-дуги: вверх в начале, вниз в конце — плавный перелёт
        float vUp = vaultRise * (Mathf.PI / vaultDuration) * Mathf.Cos(t * Mathf.PI);
        Vector3 step = _vaultDir * vaultForwardSpeed + Vector3.up * vUp;
        _controller.Move(step * Time.deltaTime);

        if (_vaultTimer <= 0f)
            _isVaulting = false;
    }

    // ──────────────────────────────────────────────
    // Выбор режима
    // ──────────────────────────────────────────────

    void HandleGait()
    {
        if (Input.GetKeyDown(KeyCode.CapsLock))
            _isRunning = !_isRunning;

        bool armed = _combat != null && _combat.IsArmed;
        bool blocking = _combat != null && _combat.IsBlocking;
        // Бой из CombatController: удар/враг включают, 15с linger без врагов, сброс удержанием 1.
        bool combatMode = _combat != null && _combat.IsInCombat;

        // Управление без изменений: Shift=3 sprint, CapsLock=2 run, иначе 1 walk.
        int gait = Input.GetKey(KeyCode.LeftShift) ? 3 : (_isRunning ? 2 : 1);
        if (blocking && gait == 3) gait = 2;

        _stepSlowRef = combatMode ? combatWalk : walk;
        _stepFastRef = gait == 3
            ? (combatMode ? combatSprint : sprint)
            : (combatMode ? combatRun : run);

        if (gait == 1) _currentGait = _stepSlowRef;
        else _currentGait = _stepFastRef;

        // Аниматор: Gait 1/2/3 как есть. Run-клипы — на Gait=2 (CapsLock), не на 3 (sprint).
        if (animator != null)
        {
            bool hasInput = _hadMoveInput;
            bool isMoving = hasInput || _velocity.magnitude > moveThreshold;
            if (HasParam("Gait")) animator.SetInteger("Gait", gait);
            if (HasParam("Moving")) animator.SetBool("Moving", isMoving);
            if (HasParam("Combat")) animator.SetBool("Combat", combatMode);
            if (HasParam("Armed")) animator.SetBool("Armed", armed);
            if (combatMode && !_inCombat && HasParam("CombatEnter")) animator.SetTrigger("CombatEnter");

            // Слой "Battle Step" — ноги только в бою.
            int battleStepLayer = animator.GetLayerIndex("Battle Step");
            if (battleStepLayer >= 0)
                animator.SetLayerWeight(battleStepLayer, combatMode ? 1f : 0f);

            if (HasParam("Turn"))
            {
                float turn = 0f;
                // 180 Turn только старт ходьбы (gait 1).
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
                // Локальные оси корпуса: Z вперёд, X вправо — для 2D Blend Tree.
                Vector3 local = transform.InverseTransformDirection(vel);
                moveX = local.x;
                moveZ = local.z;
            }
            if (float.IsNaN(moveX) || float.IsInfinity(moveX)) moveX = 0f;
            if (float.IsNaN(moveZ) || float.IsInfinity(moveZ)) moveZ = 0f;

            if (HasParam("MoveAngle")) animator.SetFloat("MoveAngle", moveAngle);
            if (HasParam("MoveDir"))
                animator.SetInteger("MoveDir", isMoving ? ComputeMoveDir(moveAngle) : 0);

            // Если MoveX/MoveZ добавили в контроллер после Start — кэш устарел.
            if (!HasParam("MoveX") || !HasParam("MoveZ"))
                CacheAnimParams();
            if (HasParam("MoveX")) animator.SetFloat("MoveX", moveX);
            if (HasParam("MoveZ")) animator.SetFloat("MoveZ", moveZ);
        }

        _inCombat = combatMode;
    }

    /// <summary>
    /// Сектор направления относительно корпуса: 0 Forward, 1 Left, 2 Right, 3 Back.
    /// Пороги: |angle| ≤ moveDirForwardAngle → F; до backAngle → L/R; иначе B.
    /// </summary>
    int ComputeMoveDir(float signedAngle)
    {
        float a = signedAngle;
        float abs = Mathf.Abs(a);
        if (abs <= moveDirForwardAngle) return 0;          // Forward
        if (abs <= backAngle) return a < 0f ? 1 : 2;       // Left / Right (backAngle уже есть в инспекторе)
        return 3;                                          // Back
    }

    /// <summary>
    /// Угол до мыши стоя (InPlace). 0 если угол меньше порога или мышь недоступна.
    /// Знак: + вправо, − влево (SignedAngle вокруг up).
    /// </summary>
    float ComputeStandingTurnAngle()
    {
        if (_mainCamera == null) return 0f;
        // В бою с прицелом на цель — угол до цели, иначе до мыши.
        Vector3 look = Vector3.zero;
        Transform aim = _combat != null ? _combat.ActiveAimTarget : null;
        if (aim != null)
        {
            look = aim.position - transform.position;
        }
        else
        {
            Ray ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
            if (!new Plane(Vector3.up, transform.position).Raycast(ray, out float dist))
                return 0f;
            look = ray.GetPoint(dist) - transform.position;
        }
        look.y = 0f;
        if (look.sqrMagnitude < 0.01f) return 0f;

        float angle = Vector3.SignedAngle(transform.forward, look.normalized, Vector3.up);
        return Mathf.Abs(angle) >= turnInPlaceThreshold ? angle : 0f;
    }

    // ──────────────────────────────────────────────
    // Движение
    // ──────────────────────────────────────────────

    void HandleMovement()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 input = new Vector3(h, 0f, v).normalized;
        bool hasInput = input.magnitude > 0.1f && _mainCamera != null;

        // Старт с места: угол + задержка только для ХОДЬБЫ (Gait 1).
        // CapsLock-бег и Shift-спринт стартуют сразу, без Walk 180 Turn.
        bool startingWalk = hasInput && !_hadMoveInput && _velocity.magnitude <= moveThreshold
            && !Input.GetKey(KeyCode.LeftShift) && !_isRunning;
        if (startingWalk)
        {
            Vector3 startDir = ComputeInputDirection();
            _startTurnAngle = Vector3.SignedAngle(transform.forward, startDir, Vector3.up);
            if (Mathf.Abs(_startTurnAngle) < turnInPlaceThreshold)
                _startTurnAngle = 0f;
            _walkStartTimer = walkStartDelay;
        }
        if (!hasInput || _isRunning || Input.GetKey(KeyCode.LeftShift))
        {
            _walkStartTimer = 0f;
            _startTurnAngle = 0f;
        }
        else if (_walkStartTimer > 0f)
            _walkStartTimer -= Time.deltaTime;

        bool isSprinting = Input.GetKey(KeyCode.LeftShift);
        // Спринт работает и без WASD — просто зажал Shift.
        bool wantMove = hasInput || isSprinting;
        _hadMoveInput = wantMove;

        if (wantMove)
        {
            Vector3 targetDir;

            if (isSprinting)
            {
                // A/D — поворот корпуса
                if (Mathf.Abs(h) > 0.1f)
                    transform.Rotate(0f, h * 110f * Time.deltaTime, 0f);

                // Только Shift (или Shift+WASD) → бег строго вперёд. Нет стрейфа и бега спиной.
                targetDir = transform.forward;
            }
            else
            {
                targetDir = ComputeInputDirection();
            }

            _maneuverDir = targetDir;

            // Целевая скорость = базовая + импульс шага, с поправкой на движение боком/спиной
            _step.TryStart(_currentGait.speed, _stepSlowRef, _stepFastRef);
            float stepImpulse = _step.Tick(Time.deltaTime);

            float speedMult = targetDir.sqrMagnitude > 0.01f ? DirectionSpeedMultiplier(targetDir) : 0f;
            // Пока идёт стартовая анимация — сильно режем скорость, чтобы не уезжать вперёд клипа.
            if (_walkStartTimer > 0f)
                speedMult *= walkStartSpeedFactor;

            Vector3 targetVelocity = targetDir *
                ((_currentGait.speed + stepImpulse) * speedMult);

            _velocity = Vector3.MoveTowards(
                _velocity,
                targetVelocity,
                _currentGait.acceleration * Time.deltaTime);

            MoveHorizontal(_velocity * Time.deltaTime);
            HandleRotation(targetDir);
        }
        else
        {
            _step.Cancel();

            // Доворот к мыши стоя
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
    }

    // Боком и спиной вперёд игрок движется медленнее: множитель по углу
    // между корпусом и направлением движения.
    float DirectionSpeedMultiplier(Vector3 moveDir)
    {
        float angle = Vector3.Angle(transform.forward, moveDir);
        if (angle <= strafeAngle) return 1f;
        return angle <= backAngle ? strafeSpeedMultiplier : backSpeedMultiplier;
    }

    // ──────────────────────────────────────────────
    // Поворот на мышь
    // ──────────────────────────────────────────────

    // moveDir — направление ввода в этом кадре (нулевой, если стоим).
    void HandleRotation(Vector3 moveDir = default)
    {
        // Во время активного удара корпус не крутим — иначе после/во время атаки
        // персонаж разворачивается на мышь или цель и ломает анимацию.
        if (_combat != null && _combat.IsAttacking)
            return;

        // Во время замаха/блока с целью в боевой зоне — плавный доворот на неё вместо мыши.
        Transform aim = _combat != null ? _combat.ActiveAimTarget : null;
        if (aim != null)
        {
            Vector3 aimLook = aim.position - transform.position;
            aimLook.y = 0f;
            if (aimLook.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(aimLook),
                    rotationSpeed * Time.deltaTime);
            return;
        }

        // Обновляем «мышь недавно двигали».
        Vector3 mousePos = Input.mousePosition;
        if ((mousePos - _lastMousePos).sqrMagnitude > 0.01f)
            _lastMouseMoveTime = Time.time;
        _lastMousePos = mousePos;

        bool mouseRecentlyMoved = (Time.time - _lastMouseMoveTime) < mouseLookTimeout;
        bool blocking = _combat != null && _combat.IsBlocking;

        bool isSprinting = Input.GetKey(KeyCode.LeftShift);

        // Бежим передом, если мышь давно не трогали (и не спринт — на спринте всегда мышь/A/D).
        bool faceMoveDir = !blocking
            && !isSprinting
            && moveDir.sqrMagnitude > 0.01f
            && !mouseRecentlyMoved;

        if (faceMoveDir)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(moveDir),
                rotationSpeed * Time.deltaTime);
            return;
        }

        if (_mainCamera == null) return;

        // Мышь: на спринте медленно (sprintTurnSpeed), иначе обычно.
        float turnSpd = isSprinting ? sprintTurnSpeed : rotationSpeed;
        Ray ray = _mainCamera.ScreenPointToRay(mousePos);
        if (new Plane(Vector3.up, transform.position).Raycast(ray, out float dist))
        {
            Vector3 look = ray.GetPoint(dist) - transform.position;
            look.y = 0f;
            if (look.magnitude > 0.1f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(look),
                    turnSpd * Time.deltaTime);
        }
    }

    // ──────────────────────────────────────────────
    // Гравитация
    // ──────────────────────────────────────────────

    void ApplyGravity()
    {
        if (_controller.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
        else
            _verticalVelocity += gravity * Time.deltaTime;

        _controller.Move(new Vector3(0f, _verticalVelocity, 0f) * Time.deltaTime);
    }
}
