using UnityEngine;

/// <summary>
/// Движение оборотня через CharacterController.
///  - бег по земле с импульсом шага (StepController), как у игрока; чем выше
///    скорость, тем длиннее и чаще шаги (интерполяция trot → gallop) — самый
///    быстрый аллюр это галоп, оборотень ВСЕГДА касается земли;
///  - vault — преодоление рельефа по упору в препятствие (как у игрока, но с
///    более высоким порогом): контролируемый перелёт дугой, без баллистики,
///    поэтому за карту улететь нельзя.
///
/// Мозг лишь говорит «беги к точке с такой скоростью» (MoveTo). Leap()/Jump() —
/// отдельные боевые механики (наскок/прыжок) и к обычному движению отношения не имеют.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class WerewolfLocomotion : MonoBehaviour
{
    [Header("Граница карты (опционально)")]
    public MapBoundary boundary;

    [Header("Разгон/торможение")]
    public float acceleration = 18f;
    public float deceleration = 22f;
    [Tooltip("На какой дистанции до цели начинать тормозить (м).")]
    public float slowdownDistance = 2.5f;

    [Header("Наземная походка (шаги)")]
    [Tooltip("У этих гейтов важны только speed/stepDistance/stepDuration/stepFrequency. " +
             "acceleration/deceleration НЕ используются — разгон берётся из полей выше.")]
    public GaitConfig trot = new GaitConfig
    {
        speed = 3.5f,
        stepDistance = 0.4f,
        stepDuration = 0.28f,
        stepFrequency = 2.6f
    };
    public GaitConfig gallop = new GaitConfig
    {
        speed = 9f,
        stepDistance = 0.9f,
        stepDuration = 0.18f,
        stepFrequency = 3.6f
    };

    [Header("Преодоление рельефа (vault — как у игрока, но лазает выше)")]
    [Tooltip("Слой препятствий, которые можно перелезать (назначь Terrain).")]
    public LayerMask vaultLayers;
    [Tooltip("Макс. высота препятствия, которое оборотень осиливает (м). Выше игрока.")]
    public float vaultMaxHeight = 2.0f;
    [Tooltip("Дальность пробы препятствия перед перелазом (м).")]
    public float vaultCheckDistance = 0.8f;
    [Tooltip("Длительность перелаза (сек).")]
    public float vaultDuration = 0.45f;
    [Tooltip("Горизонтальная скорость во время перелаза (м/с).")]
    public float vaultForwardSpeed = 7f;
    [Tooltip("Высота дуги перелаза.")]
    public float vaultRise = 1.6f;
    [Tooltip("Пауза между перелазами (сек).")]
    public float vaultCooldown = 0.6f;

    [Header("Гравитация")]
    public float gravity = -20f;

    [Header("Поворот")]
    public float rotationSpeed = 6f;

    [Header("Прибытие")]
    public float arriveThreshold = 1.5f;

    [Header("Старт: посадка на землю")]
    public LayerMask groundLayers = ~0;
    public float groundProbeHeight = 50f;

    [Header("Анимация")]
    [Tooltip("Аниматор волка (опционально — пока нет, оставь пустым, ошибок не будет). Параметры: Run (bool), Speed (float), триггеры Leap/Jump/Vault.")]
    public Animator animator;

    private CharacterController _cc;
    private Vector3 _horizVel;
    private float _vertVel;
    private bool _leaping;        // сейчас в воздухе (боевой Leap/Jump)
    private float _stepImpulse;   // импульс шага в этом кадре (наземная походка)
    private bool _placed;

    // vault
    private bool _vaulting;
    private float _vaultTimer;
    private Vector3 _vaultDir;
    private float _lastVaultTime = -999f;

    private readonly StepController _step = new StepController();

    private int _moveFrame = -1;
    private Vector3 _moveTarget;
    private float _moveSpeed;

    public bool IsGrounded => _cc != null && _cc.isGrounded;
    public bool IsLeaping => _leaping;
    public bool IsVaulting => _vaulting;
    public float CurrentSpeed => _horizVel.magnitude;
    public float StepCurve => _step.Curve;   // для боба/анимации/звука

    void Awake() => _cc = GetComponent<CharacterController>();
    void Start() { if (boundary == null) boundary = GetComponent<MapBoundary>(); }

    /// <summary>Заново посадить на землю (например, после перегенерации карты).</summary>
    public void RequestGroundSnap() => _placed = false;

    // =================== Намерение от мозга ===================

    /// <summary>Двигаться к цели с заданной скоростью. true, когда дошли.</summary>
    public bool MoveTo(Vector3 target, float speed, float dt)
    {
        _moveFrame = Time.frameCount;
        _moveTarget = target;
        _moveSpeed = speed;
        return !_leaping && !_vaulting && FlatDistance(target) <= arriveThreshold;
    }

    /// <summary>Одиночный бросок точно в точку (прыжок-наскок в бою).</summary>
    public void Leap(Vector3 target, float height)
    {
        if (_leaping || _vaulting || !_cc.isGrounded) return;
        Vector3 to = target - transform.position; to.y = 0f;
        float dist = to.magnitude;
        Vector3 dir = dist > 0.0001f ? to / dist : transform.forward;
        float g = -gravity;
        float vUp = Mathf.Sqrt(2f * g * height);
        float airTime = 2f * vUp / g;
        _horizVel = dir * (airTime > 0.0001f ? dist / airTime : 0f);
        _vertVel = vUp;
        _leaping = true;
        _step.Cancel();
        if (animator != null) animator.SetTrigger("Leap");
    }

    /// <summary>Вертикальный прыжок на месте.</summary>
    public void Jump(float height)
    {
        if (!_cc.isGrounded || _leaping || _vaulting) return;
        _vertVel = Mathf.Sqrt(2f * -gravity * height);
        if (animator != null) animator.SetTrigger("Jump");
    }

    public void FaceTowards(Vector3 worldPoint, float dt)
    {
        Vector3 look = worldPoint - transform.position; look.y = 0f;
        if (look.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(look.normalized, Vector3.up), rotationSpeed * dt);
    }

    // =================== Физика (после мозга) ===================

    void LateUpdate()
    {
        if (!_placed) { if (TryPlaceOnGround()) _placed = true; return; }

        float dt = Time.deltaTime;

        // Перелаз сам двигает оборотня — остальную физику в этом кадре пропускаем.
        if (_vaulting) { TickVault(dt); return; }

        bool active = _moveFrame == Time.frameCount;
        Vector3 pos = transform.position;

        _stepImpulse = 0f;

        if (_leaping)
        {
            // В полёте горизонталь зафиксирована — баллистика (боевой Leap/Jump).
        }
        else
        {
            // --- Горизонтальная скорость: разгон/торможение ---
            if (active)
            {
                Vector3 to = _moveTarget - pos; to.y = 0f;
                float dist = to.magnitude;
                Vector3 dir = dist > 0.001f ? to / dist : Vector3.zero;
                float targetSpeed = dist < slowdownDistance
                    ? Mathf.Lerp(0f, _moveSpeed, dist / slowdownDistance)
                    : _moveSpeed;
                Vector3 targetVel = dir * targetSpeed;
                float rate = targetVel.magnitude > _horizVel.magnitude ? acceleration : deceleration;
                _horizVel = Vector3.MoveTowards(_horizVel, targetVel, rate * dt);
            }
            else
            {
                _horizVel = Vector3.MoveTowards(_horizVel, Vector3.zero, deceleration * dt);
            }

            float spd = _horizVel.magnitude;

            // --- Наземная походка с импульсом шага (как у игрока) ---
            if (active && spd > 0.1f)
            {
                _step.TryStart(spd, trot, gallop);
                _stepImpulse = _step.Tick(dt);
            }
            else
            {
                _step.Cancel();
            }
        }

        // --- Гравитация ---
        if (_cc.isGrounded && _vertVel <= 0f)
        {
            if (_leaping) _leaping = false; // приземлились, импульс сохраняем
            _vertVel = -2f;
        }
        else _vertVel += gravity * dt;

        // --- Движение: горизонталь (+ импульс шага) через границу карты + вертикаль ---
        Vector3 stepVec = (_stepImpulse != 0f && _horizVel.sqrMagnitude > 0.0001f)
            ? _horizVel.normalized * _stepImpulse
            : Vector3.zero;

        Vector3 horiz = (_horizVel + stepVec) * dt; horiz.y = 0f;
        if (boundary != null && boundary.IsReady) horiz = boundary.Constrain(pos, horiz);
        _cc.Move(horiz + Vector3.up * (_vertVel * dt));

        // --- Анимация бега ---
        if (animator != null)
        {
            float animSpd = _horizVel.magnitude;
            animator.SetBool("Run", !_leaping && animSpd > 0.1f);
            animator.SetFloat("Speed", animSpd); // для blend-tree trot↔gallop
        }
    }

    // =================== vault (перелаз рельефа) ===================

    // Срабатывает, когда CharacterController упирается в коллайдер.
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (_vaulting || _leaping) return;
        if (!_cc.isGrounded) return;
        if (Time.time - _lastVaultTime < vaultCooldown) return;
        if ((vaultLayers.value & (1 << hit.gameObject.layer)) == 0) return;

        // Бьёмся в стену, а не в пол/потолок.
        if (Mathf.Abs(hit.normal.y) > 0.3f) return;

        // Должны бежать именно в препятствие.
        Vector3 flatVel = _horizVel; flatVel.y = 0f;
        if (flatVel.sqrMagnitude < 0.1f) return;
        Vector3 into = -hit.normal; into.y = 0f; into.Normalize();
        if (Vector3.Dot(flatVel.normalized, into) < 0.5f) return;

        // Препятствие низкое? Луч над его макс. высотой — если пусто, переваливаемся.
        float feetY = transform.position.y - _cc.height * 0.5f + _cc.center.y;
        Vector3 origin = new Vector3(transform.position.x, feetY + vaultMaxHeight, transform.position.z);
        if (Physics.Raycast(origin, into, vaultCheckDistance, vaultLayers,
                            QueryTriggerInteraction.Ignore)) return;  // слишком высокое

        StartVault(into);
    }

    private void StartVault(Vector3 dir)
    {
        _vaulting = true;
        _lastVaultTime = Time.time;
        _vaultTimer = vaultDuration;
        _vaultDir = dir;
        _vertVel = 0f;
        _step.Cancel();
        if (animator != null) animator.SetTrigger("Vault");
    }

    private void TickVault(float dt)
    {
        _vaultTimer -= dt;
        float t = 1f - Mathf.Clamp01(_vaultTimer / vaultDuration);  // 0 → 1

        // Производная sin-дуги: вверх в начале, вниз в конце — плавный перелёт.
        float vUp = vaultRise * (Mathf.PI / vaultDuration) * Mathf.Cos(t * Mathf.PI);

        Vector3 horiz = _vaultDir * (vaultForwardSpeed * dt); horiz.y = 0f;
        if (boundary != null && boundary.IsReady)
            horiz = boundary.Constrain(transform.position, horiz);

        _cc.Move(horiz + Vector3.up * (vUp * dt));

        if (_vaultTimer <= 0f)
        {
            _vaulting = false;
            // Переносим импульс в бег, чтобы не вставать колом сразу после перелаза.
            _horizVel = _vaultDir * Mathf.Max(_horizVel.magnitude, vaultForwardSpeed * 0.6f);
        }
    }

    // =================== helpers ===================

    private bool TryPlaceOnGround()
    {
        _cc.enabled = false;
        Vector3 origin = transform.position + Vector3.up * groundProbeHeight;
        bool found = Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                                     groundProbeHeight * 2f, groundLayers,
                                     QueryTriggerInteraction.Ignore);
        if (found)
        {
            Vector3 p = transform.position;
            p.y = hit.point.y + _cc.skinWidth + _cc.height * 0.5f - _cc.center.y + 0.02f;
            transform.position = p;
            _vertVel = 0f;
        }
        _cc.enabled = true;
        return found;
    }

    private float FlatDistance(Vector3 p)
    {
        Vector3 d = p - transform.position; d.y = 0f;
        return d.magnitude;
    }
}
