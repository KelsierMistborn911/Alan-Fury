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
    public float gravity = -20f;

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
    private Transform LockTarget => _combat != null ? _combat.currentTarget : null;

    // ──────────────────────────────────────────────

    void Start()
    {
        _controller = GetComponent<CharacterController>();
        _mainCamera = Camera.main;
        _currentGait = walk;
        _combat = GetComponent<CombatController3D>();

        if (boundary == null) boundary = GetComponent<MapBoundary>();
        if (witchLight == null) witchLight = GetComponentInChildren<WitchLight>();
    }

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
                }
            }
        }

        if (!_isDodging && !_isRolling) return;

        _maneuverTimer -= Time.deltaTime;
        if (_maneuverTimer <= 0f)
        {
            if (_isDodging) _lastDodgeEndTime = Time.time;
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

        if (Input.GetKey(KeyCode.LeftShift))
            _currentGait = sprint;
        else if (_isRunning)
            _currentGait = run;
        else
            _currentGait = walk;
    }

    // ──────────────────────────────────────────────
    // Движение
    // ──────────────────────────────────────────────

    void HandleMovement()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        Vector3 input = new Vector3(h, 0f, v).normalized;

        if (input.magnitude > 0.1f && _mainCamera != null)
        {
            Vector3 targetDir = ComputeInputDirection();
            _maneuverDir = targetDir;

            // Целевая скорость = базовая + импульс шага
            _step.TryStart(_currentGait.speed, walk,
                Input.GetKey(KeyCode.LeftShift) ? sprint : run);
            float stepImpulse = _step.Tick(Time.deltaTime);

            Vector3 targetVelocity = targetDir * (_currentGait.speed + stepImpulse);

            // Плавный разгон через SmoothDamp
            _velocity = Vector3.MoveTowards(
                _velocity,
                targetVelocity,
                _currentGait.acceleration * Time.deltaTime);

            MoveHorizontal(_velocity * Time.deltaTime);
            HandleRotation();
        }
        else
        {
            _step.Cancel();

            // Доворот к мыши стоя — так же, как на ходу
            HandleRotation();

            // Торможение — у walk резкое, у sprint плавное
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

    // ──────────────────────────────────────────────
    // Поворот на мышь
    // ──────────────────────────────────────────────

    void HandleRotation()
    {
        // Во время замаха/удара с целью — плавный доворот на неё вместо мыши.
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

        // Лок-таргет: всегда лицом к врагу вместо мыши.
        Transform lockT = LockTarget;
        if (lockT != null)
        {
            Vector3 lockLook = lockT.position - transform.position;
            lockLook.y = 0f;
            if (lockLook.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(lockLook),
                    rotationSpeed * Time.deltaTime);
            return;
        }

        if (_mainCamera == null) return;

        Ray ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
        if (new Plane(Vector3.up, transform.position).Raycast(ray, out float dist))
        {
            Vector3 look = ray.GetPoint(dist) - transform.position;
            look.y = 0f;
            if (look.magnitude > 0.1f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    Quaternion.LookRotation(look),
                    rotationSpeed * Time.deltaTime);
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
