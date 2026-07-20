using UnityEngine;

public class CombatController3D : MonoBehaviour
{
    [Header("Ссылки")]
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public WeaponHitbox hitbox;

    [Header("Захват цели")]
    public float targetLockRange = 15f;
    [Tooltip("Дальше этой дистанции Tab-таргет сбрасывается: перехват на ближайшего в этом же радиусе, иначе — совсем.")]
    public float targetHoldRange = 10f;
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
    [Tooltip("Клавиша блока (щит в левой руке обязателен). ПКМ.")]
    public KeyCode blockKey = KeyCode.Mouse1;
    [Tooltip("Множитель дальности укола (Q при блоке) относительно обычной атаки.")]
    public float thrustRangeMultiplier = 1.3f;
    [Tooltip("Множитель урона укола относительно обычной атаки.")]
    public float thrustDamageMultiplier = 1f;

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
    public bool IsCharging { get; private set; }
    public bool HasTarget => currentTarget != null;
    public float ChargePercent { get; private set; }
    public Transform currentTarget { get; private set; }

    // Цель для доворота корпуса: Tab-таргет приоритетнее авто-цели.
    // Не null только во время замаха/удара — иначе движение крутит на мышь.
    public Transform ActiveAimTarget =>
        (IsCharging || IsAttacking) ? (currentTarget != null ? currentTarget : _autoTarget) : null;

    private float stateTimer;
    private WeaponData currentWeapon;
    private float chargeStartTime;
    private bool isHoldingAttack;
    private int _combo;
    private float _comboExpire;
    private Transform _autoTarget; // авто-цель текущего замаха, сбрасывается после удара
    private PlayerMovement3D movement;

    private bool _isThrustAttack;
    private bool _dodgeAttackPending;
    private float _dodgeAttackFireTime;
    private bool _dodgeAttackPerfectFlag;

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
    }

    void Update()
    {
        // Удержание таргета: цель ушла дальше targetHoldRange — перехват/сброс.
        if (HasTarget) MaintainLockTarget();

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

        // Комбо-удар ждёт истечения укороченного замаха
        if (_dodgeAttackPending)
        {
            if (Time.time >= _dodgeAttackFireTime) FireDodgeAttack();
            return;
        }

        // Идёт заряд
        if (IsCharging)
        {
            // Без Tab-таргета — поворот (мышь/WASD) может сменить цель прямо во время замаха.
            if (currentTarget == null)
            {
                Transform reTarget = FindClosestToDirection(GetPreferredAimDirection());
                if (reTarget != null) _autoTarget = reTarget;
            }

            if (Input.GetMouseButton(0))
            {
                ChargePercent = Mathf.Clamp01((Time.time - chargeStartTime) / currentWeapon.chargeDuration);
            }

            if (Input.GetMouseButtonUp(0) && isHoldingAttack)
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

        // Блок (ПКМ). Держит IsBlocking, но ЛКМ/Q под блоком бьют, не снимая его.
        bool wantsBlock = Input.GetKey(blockKey) && loadout.HasShield();
        IsBlocking = wantsBlock;

        if (wantsBlock)
        {
            if (Input.GetMouseButtonDown(0)) FireQuickAttack(false);
            else if (Input.GetKeyDown(KeyCode.Q)) FireQuickAttack(true);
            return;
        }

        // Удар во время/сразу после уворота (Alt) — отдельное укороченное комбо.
        if (Input.GetMouseButtonDown(0) && movement != null &&
            (movement.IsDodging || movement.TimeSinceDodgeEnd <= dodgeAttackBufferAfter))
        {
            StartDodgeAttack();
            return;
        }

        // Начало атаки (ЛКМ)
        if (Input.GetMouseButtonDown(0))
        {
            currentWeapon = loadout.GetMainWeapon();
            if (currentWeapon == null) return;

            if (resources.HasStamina(currentWeapon.staminaCost))
            {
                _isThrustAttack = false;
                StartHoldAttack();
            }
        }
    }

    // Мгновенная атака без стадии заряда — ЛКМ/Q во время блока.
    void FireQuickAttack(bool thrust)
    {
        currentWeapon = loadout.GetMainWeapon();
        if (currentWeapon == null) return;

        float cost = currentWeapon.staminaCost * 0.5f;
        if (!resources.HasStamina(cost)) return;
        resources.SpendStamina(cost);

        _isThrustAttack = thrust;
        ChargePercent = currentWeapon.minChargePercent;
        SampleAttackMoveMode();
        ExecuteAttack();
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

        _dodgeAttackPending = true;
        _dodgeAttackFireTime = Time.time + windup;
        _isThrustAttack = false;
        SampleAttackMoveMode();
    }

    void FireDodgeAttack()
    {
        _dodgeAttackPending = false;

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

        // Автонаведение: без Tab-таргета каждый замах сам берёт ближайшего в конусе (с учётом стороны).
        _autoTarget = currentTarget != null ? null : FindPreferredTarget();
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
        resources.SpendStamina(cost);

        ExecuteAttack();
    }

    void ExecuteAttack()
    {
        IsAttacking = true;
        stateTimer = currentWeapon.attackDuration;

        // Серия: удар в окне comboWindow продолжает комбо, иначе — сброс.
        _combo = Time.time <= _comboExpire ? _combo + 1 : 0;
        _comboExpire = Time.time + currentWeapon.attackDuration + comboWindow;

        if (animator != null)
            animator.SetTrigger(_combo % 2 != 0 ? "AttackLeft" : "AttackRight");

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

                float range = currentWeapon.attackRange * (_isThrustAttack ? thrustRangeMultiplier : 1f);
                float damage = currentWeapon.damage * damageMult * (_isThrustAttack ? thrustDamageMultiplier : 1f);
                damage += ComputeMomentumBonus();

                ApplyAttackMoveMode();

                hitbox.Activate(
                    range,
                    currentWeapon.attackRadius,
                    currentWeapon.attackHeight,
                    currentWeapon.hitboxOffset,
                    GetAttackDirection(),
                    damage,
                    currentWeapon.staggerForce * staggerMult,
                    currentWeapon.targetLayers,
                    currentWeapon.attackDuration,
                    currentWeapon.tickInterval,
                    ChargePercent,
                    _combo
                );
            }
        }

        _isThrustAttack = false;
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

    // Цель дальше радиуса удержания — берём ближайшего врага в этом радиусе
    // (любое направление: в лок-моде враги уже вокруг), никого нет — сброс.
    void MaintainLockTarget()
    {
        Vector3 to = currentTarget.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude <= targetHoldRange * targetHoldRange) return;

        currentTarget = FindNearestInRadius(targetHoldRange);
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

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Plane ground = new Plane(Vector3.up, transform.position);
        if (ground.Raycast(ray, out float dist))
        {
            Vector3 point = ray.GetPoint(dist);
            Vector3 dir = point - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }
        return transform.forward;
    }

    // Смена цели поворотом во время замаха: приоритет стороне поворота, не дистанции.
    Transform FindClosestToDirection(Vector3 preferred)
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, targetLockRange, enemyLayers);
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

    // Ближайший враг в конусе с приоритетом направления наводки (WASD/мышь).
    Transform FindPreferredTarget()
    {
        Vector3 preferred = GetPreferredAimDirection();
        Collider[] enemies = Physics.OverlapSphere(transform.position, targetLockRange, enemyLayers);
        Transform best = null;
        float bestScore = float.MaxValue;
        float halfAngle = aimConeAngle * 0.5f;

        foreach (Collider col in enemies)
        {
            Vector3 to = col.transform.position - transform.position;
            to.y = 0f;
            if (Vector3.Angle(transform.forward, to) > halfAngle) continue;

            float angleFromPreferred = Vector3.Angle(preferred, to);
            float score = angleFromPreferred * 0.5f + to.magnitude;
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
        Transform aim = currentTarget != null ? currentTarget : _autoTarget;
        if (aim != null)
        {
            // Таргет дальше, чем бьёт оружие, — целимся в ближайшего вместо него.
            float effectiveRange = currentWeapon != null
                ? currentWeapon.attackRange * (_isThrustAttack ? thrustRangeMultiplier : 1f)
                : targetLockRange;

            Vector3 diff = aim.position - transform.position;
            diff.y = 0f;
            if (diff.magnitude > effectiveRange)
            {
                Transform nearest = FindNearestInRadius(targetLockRange);
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
