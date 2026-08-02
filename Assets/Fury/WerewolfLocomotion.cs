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
    /// <summary>Стойка. На четвереньках быстрее, но нет стрейфа — волк всегда смотрит куда бежит.
    /// На двух ногах медленнее, зато ходит боком и спиной, не теряя цель из виду.</summary>
    public enum Stance { Quad, Biped }

    [Header("Граница карты (опционально)")]
    public MapBoundary boundary;

    [Header("Разгон/торможение")]
    public float acceleration = 7f;
    public float deceleration = 22f;
    [Tooltip("На какой дистанции до цели начинать тормозить (м).")]
    public float slowdownDistance = 2.5f;

    [Header("Направленная скорость (относительно взгляда)")]
    [Tooltip("Множитель скорости при движении боком.")]
    [Range(0f, 1f)] public float sideSpeedMult = 0.6f;
    [Tooltip("Множитель скорости при движении спиной (спиной не спринтуем).")]
    [Range(0f, 1f)] public float backSpeedMult = 0.35f;

    [Header("Походка толчками")]
    [Tooltip("0 = ровное скольжение (как раньше), 1 = движение почти только толчками шагов. Торможения не касается.")]
    [Range(0f, 1f)] public float stepPulseWeight = 0.6f;

    [Header("Стойка")]
    [Tooltip("Умеет ли этот волк вставать на две ноги. Выключено — всегда на четвереньках (волчий тип).")]
    public bool canBiped = true;
    [Tooltip("Сколько длится подъём/опускание (сек). Всё это время волк не двигается и не бьёт.")]
    public float stanceChangeDuration = 0.4f;

    [Header("Наземная походка: важны speed/stepDistance/stepDuration/stepFrequency, acceleration/deceleration не используются")]
    [Tooltip("На четвереньках, шаг. Gait=1.")]
    public GaitConfig walk = new GaitConfig
    {
        speed = 2f,
        stepDistance = 0.25f,
        stepDuration = 0.36f,
        stepFrequency = 1.9f
    };
    [Tooltip("На четвереньках, бег. Gait=2.")]
    public GaitConfig trot = new GaitConfig
    {
        speed = 5f,
        stepDistance = 0.4f,
        stepDuration = 0.28f,
        stepFrequency = 2.6f
    };
    [Tooltip("На четвереньках, спринт. Gait=3.")]
    public GaitConfig gallop = new GaitConfig
    {
        speed = 9f,
        stepDistance = 0.9f,
        stepDuration = 0.18f,
        stepFrequency = 3.6f
    };
    [Tooltip("На двух ногах — единственный темп. Стрейф разрешён, поэтому медленнее бега на четырёх.")]
    public GaitConfig bipedGait = new GaitConfig
    {
        speed = 3.5f,
        stepDistance = 0.3f,
        stepDuration = 0.3f,
        stepFrequency = 2.2f
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
    [Tooltip("Скорость поворота на четвереньках. Низкая — бег выглядит плавно, разворот широкой дугой.")]
    public float rotationSpeed = 6f;
    [Tooltip("Скорость поворота на двух ногах. Выше, чтобы волк успевал доворачиваться к цели в ближнем бою.")]
    public float bipedRotationSpeed = 13f;

    /// <summary>Скорость поворота для текущей стойки.</summary>
    private float TurnSpeed => _stance == Stance.Biped ? bipedRotationSpeed : rotationSpeed;

    [Header("Прибытие")]
    public float arriveThreshold = 1.5f;

    [Header("Старт: посадка на землю")]
    public LayerMask groundLayers = ~0;
    public float groundProbeHeight = 50f;

    [Header("Анимация")]
    [Tooltip("Аниматор волка (опционально — пока нет, оставь пустым, ошибок не будет). " +
             "Параметры: Stance (bool: вкл = на двух ногах), Gait (int: 0 стоит, 1 шаг, 2 бег, 3 спринт), " +
             "триггеры StandUp/DropDown/Leap/Vault/Death. Старые Run (bool) и Speed (float) пока пишутся тоже.")]
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

    // Какие параметры реально есть в контроллере. Аниматор ругается в консоль на каждую
    // запись несуществующего параметра, поэтому проверяем — клипы можно добавлять постепенно.
    private System.Collections.Generic.HashSet<string> _animParams;

    private void CacheAnimParams()
    {
        _animParams = new System.Collections.Generic.HashSet<string>();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters) _animParams.Add(p.name);
    }

    private bool HasParam(string name) =>
        animator != null && _animParams != null && _animParams.Contains(name);

    private void SetTrig(string name) { if (HasParam(name)) animator.SetTrigger(name); }
    private void SetBoolIf(string name, bool v) { if (HasParam(name)) animator.SetBool(name, v); }
    private void SetIntIf(string name, int v) { if (HasParam(name)) animator.SetInteger(name, v); }
    private void SetFloatIf(string name, float v) { if (HasParam(name)) animator.SetFloat(name, v); }

    /// <summary>Проиграть смерть (клип VDEATH). Нет параметра Death в контроллере — тихо пропускаем.</summary>
    public void PlayDeath() => SetTrig("Death");

    /// <summary>Послать триггер атаки. Зовёт WerewolfCombat, чтобы не дублировать проверку параметров.</summary>
    public void PlayAttack(string trigger) => SetTrig(trigger);

    private int _moveFrame = -1;
    private Vector3 _moveTarget;
    private float _moveSpeed;

    // стойка
    private Stance _stance = Stance.Quad;
    private float _stanceTimer;          // >0 — идёт переход, движение и атаки запрещены
    private Stance _stanceTarget = Stance.Quad;

    public bool IsGrounded => _cc != null && _cc.isGrounded;
    public bool IsLeaping => _leaping;

    /// <summary>Текущая стойка. Меняется через SetStance, во время перехода остаётся прежней.</summary>
    public Stance CurrentStance => _stance;
    /// <summary>Идёт подъём/опускание — волк стоит на месте и не может бить.</summary>
    public bool IsChangingStance => _stanceTimer > 0f;
    /// <summary>Стоит на двух ногах (можно двигаться боком и назад).</summary>
    public bool IsBiped => _stance == Stance.Biped;
    /// <summary>Текущий аллюр для аниматора: 0 стоит, 1 шаг, 2 бег, 3 спринт.</summary>
    public int CurrentGait { get; private set; }

    /// <summary>Сменить стойку. Пока идёт переход, волк не двигается и не атакует.
    /// Волку с canBiped=false подъём недоступен — вызов игнорируется.</summary>
    public void SetStance(Stance s)
    {
        if (s == Stance.Biped && !canBiped) return;
        if (_stanceTarget == s) return;              // уже там или уже идём туда
        if (_leaping || _vaulting) return;           // в воздухе не встаём

        _stanceTarget = s;
        _stanceTimer = stanceChangeDuration;
        _step.Cancel();
        if (animator != null)
            SetTrig(s == Stance.Biped ? "StandUp" : "DropDown");
    }

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        CacheAnimParams();
    }
    void Start() { if (boundary == null) boundary = GetComponent<MapBoundary>(); }

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
        SetTrig("Leap");
    }

    /// <summary>Мгновенный горизонтальный импульс (отброс от удара). Затухает обычным торможением.</summary>
    public void AddImpulse(Vector3 force)
    {
        force.y = 0f;
        _horizVel += force;
    }

    /// <summary>Повернуться к точке. На четвереньках работает только СТОЯ: в движении
    /// волк смотрит туда, куда бежит, иначе он полз бы боком (старая беда).
    /// На двух ногах — всегда, это и есть смысл стойки.</summary>
    public void FaceTowards(Vector3 worldPoint, float dt)
    {
        // В Quad в момент активного движения ориентацию задаёт направление бега.
        if (_stance == Stance.Quad && _moveFrame == Time.frameCount && _horizVel.sqrMagnitude > 0.04f)
            return;

        Vector3 look = worldPoint - transform.position; look.y = 0f;
        if (look.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(look.normalized, Vector3.up), TurnSpeed * dt);
    }

    // =================== Физика (после мозга) ===================

    void LateUpdate()
    {
        if (!_placed) { if (TryPlaceOnGround()) _placed = true; return; }

        float dt = Time.deltaTime;

        // Перелаз сам двигает оборотня — остальную физику в этом кадре пропускаем.
        if (_vaulting) { TickVault(dt); return; }

        // Смена стойки: волк встаёт/опускается на месте, приказ на движение игнорируется.
        if (_stanceTimer > 0f)
        {
            _stanceTimer -= dt;
            if (_stanceTimer <= 0f) _stance = _stanceTarget;
        }

        bool active = _moveFrame == Time.frameCount && _stanceTimer <= 0f;
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

                if (_stance == Stance.Quad)
                {
                    // На четвереньках стрейфа нет: волк доворачивается по ходу движения и
                    // всегда бежит вперёд. Множители side/back не применяются — иначе он
                    // полз бы боком, не разворачиваясь (старое поведение).
                    if (dir.sqrMagnitude > 0.0001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(dir, Vector3.up), TurnSpeed * dt);
                }
                else
                {
                    // На двух ногах — стрейф: идём куда надо, смотрим куда смотрели.
                    // Вперёд полная скорость, вбок/назад медленнее.
                    float facingDot = Vector3.Dot(transform.forward, dir);
                    float dirMult = facingDot >= 0f
                        ? Mathf.Lerp(sideSpeedMult, 1f, facingDot)
                        : Mathf.Lerp(sideSpeedMult, backSpeedMult, -facingDot);
                    targetSpeed *= dirMult;
                }

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
                // StepController берёт два гейта и интерполирует между ними, поэтому
                // выбираем соседнюю пару по текущей скорости.
                if (_stance == Stance.Biped) _step.TryStart(spd, bipedGait, bipedGait);
                else if (spd <= trot.speed) _step.TryStart(spd, walk, trot);
                else _step.TryStart(spd, trot, gallop);
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

        // Пульс шага: скорость проседает между толчками лап. Только в активном движении по земле —
        // торможение и полёт идут без пульса, чтобы волк мог нормально останавливаться.
        float pulse = 1f;
        if (!_leaping && active && _step.Curve > 0f)
            pulse = Mathf.Lerp(1f, Mathf.Max(_step.Curve, 0.15f), stepPulseWeight);

        Vector3 horiz = (_horizVel * pulse + stepVec) * dt; horiz.y = 0f;
        if (boundary != null && boundary.IsReady) horiz = boundary.Constrain(pos, horiz);
        _cc.Move(horiz + Vector3.up * (_vertVel * dt));

        // --- Анимация бега ---
        float speedNow = _horizVel.magnitude;
        CurrentGait = GaitFromSpeed(speedNow);

        if (animator != null)
        {
            SetBoolIf("Stance", _stance == Stance.Biped);
            SetIntIf("Gait", CurrentGait);

            // Старые параметры пишем как раньше — контроллер продолжит работать на них,
            // пока переходы не перевешены на Stance/Gait.
            SetBoolIf("Run", !_leaping && speedNow > 0.1f);
            SetFloatIf("Speed", speedNow);
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
        SetTrig("Vault");
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

    /// <summary>0 стоит, 1 шаг, 2 бег, 3 спринт. На двух ногах темп один — всегда 1.
    /// Границы по середине между гейтами, чтобы аллюр не дёргался у самого порога.</summary>
    private int GaitFromSpeed(float spd)
    {
        if (_leaping || _stanceTimer > 0f || spd <= 0.1f) return 0;
        if (_stance == Stance.Biped) return 1;
        if (spd < (walk.speed + trot.speed) * 0.5f) return 1;
        if (spd < (trot.speed + gallop.speed) * 0.5f) return 2;
        return 3;
    }

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
