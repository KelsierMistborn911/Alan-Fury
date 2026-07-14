using UnityEngine;

public class CombatController3D : MonoBehaviour
{
    [Header("Ссылки")]
    public PlayerResources resources;
    public PlayerLoadout loadout;
    public WeaponHitbox hitbox;

    [Header("Захват цели")]
    public float targetLockRange = 15f;
    public LayerMask enemyLayers;

    [Header("Комбо")]
    [Tooltip("Аниматор игрока. Пусто — найдётся на объекте.")]
    public Animator animator;
    [Tooltip("Пауза между ударами, после которой серия сбрасывается (сек).")]
    public float comboWindow = 1f;

    public bool IsWindingUp { get; private set; }
    public bool IsAttacking { get; private set; }
    public bool IsBlocking { get; private set; }
    public bool IsCharging { get; private set; }
    public bool HasTarget => currentTarget != null;
    public float ChargePercent { get; private set; }
    public Transform currentTarget { get; private set; }

    private float stateTimer;
    private WeaponData currentWeapon;
    private float chargeStartTime;
    private bool isHoldingAttack;
    private int _combo;
    private float _comboExpire;

    void Awake()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (animator == null) animator = GetComponent<Animator>();
    }

    void Update()
    {
        // Блокировка цели (Tab)
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (HasTarget) ClearTarget();
            else AcquireTarget();
        }

        // Активная атака — ждём конца
        if (IsAttacking)
        {
            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0f) EndAttack();
            return;
        }

        // Идёт заряд
        if (IsCharging)
        {
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

        // Блок (ПКМ)
        if (Input.GetMouseButton(1) && loadout.HasShield())
        {
            IsBlocking = true;
            return;
        }
        IsBlocking = false;

        // Начало атаки (ЛКМ)
        if (Input.GetMouseButtonDown(0))
        {
            currentWeapon = loadout.GetMainWeapon();
            if (currentWeapon == null) return;

            if (resources.HasStamina(currentWeapon.staminaCost))
            {
                StartHoldAttack();
            }
        }
    }

    void StartHoldAttack()
    {
        IsCharging = true;
        isHoldingAttack = true;
        chargeStartTime = Time.time;
        ChargePercent = 0f;

        if (hitbox != null && hitbox.visual != null)
            hitbox.visual.ShowWindup();
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

                hitbox.Activate(
                    currentWeapon.attackRange,
                    currentWeapon.attackRadius,
                    currentWeapon.attackHeight,
                    currentWeapon.hitboxOffset,
                    GetAttackDirection(),
                    currentWeapon.damage * damageMult,
                    currentWeapon.staggerForce * staggerMult,
                    currentWeapon.targetLayers,
                    currentWeapon.attackDuration,
                    currentWeapon.tickInterval,
                    ChargePercent,
                    _combo
                );
            }
        }

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
    }

    void AcquireTarget()
    {
        Collider[] enemies = Physics.OverlapSphere(transform.position, targetLockRange, enemyLayers);
        Transform closest = null;
        float minDist = float.MaxValue;
        foreach (Collider col in enemies)
        {
            float dist = Vector3.Distance(transform.position, col.transform.position);
            if (dist < minDist) { minDist = dist; closest = col.transform; }
        }
        currentTarget = closest;
    }

    void ClearTarget() => currentTarget = null;

    Vector3 GetAttackDirection()
    {
        if (HasTarget)
        {
            Vector3 dir = currentTarget.position - transform.position;
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
