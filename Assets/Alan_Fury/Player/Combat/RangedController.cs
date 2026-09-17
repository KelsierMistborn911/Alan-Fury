using UnityEngine;

/// <summary>
/// Дальний бой отдельно от HumanoidCombat.
/// Лук: зажать ЛКМ — натяг, отпустить — выстрел, Space — срыв.
/// После полного натяга персонаж сам довводит до цели; точное окно шире, если курсор ближе.
/// Арбалет: клик — выстрел, затем reloadDuration.
/// </summary>
public class RangedController : MonoBehaviour
{
    public PlayerLoadout loadout;
    public Inventory inventory;
    public PlayerResources resources;
    public PlayerTargeting targeting;
    public LayerMask enemyLayers;

    [Header("Наводка")]
    [Tooltip("В этом угле курсор даёт полный бонус к попаданию / окну.")]
    public float aimFullBonusAngle = 8f;
    [Tooltip("Дальше этого угла враг не считается наведённым.")]
    public float aimMaxAngle = 28f;
    public float aimRange = 28f;
    [Range(0f, 1f)] public float baseHitChance = 0.35f;
    [Range(0f, 1f)] public float maxHitChance = 0.92f;
    public float missYawMin = 8f;
    public float missYawMax = 16f;

    [Header("Самонаведение после полного натяга")]
    [Tooltip("Довод до цели при курсоре почти на враге.")]
    public float aimInFast = 0.18f;
    [Tooltip("Довод до цели на краю конуса.")]
    public float aimInSlow = 0.48f;
    [Tooltip("Точное окно при курсоре на краю конуса.")]
    public float precisionWindowMin = 0.12f;
    [Tooltip("Точное окно при курсоре на цели.")]
    public float precisionWindowMax = 0.55f;
    [Tooltip("Увод после окна, если не выстрелил.")]
    public float driftYawMin = 10f;
    public float driftYawMax = 18f;

    public bool IsDrawing { get; private set; }
    public bool IsReloading => Time.time < _reloadUntil;
    public float ChargePercent { get; private set; }
    public bool HasRangedEquipped => Weapon != null && Weapon.isRanged && Weapon.OccupiesBothHands;

    public Transform AimMark { get; private set; }
    public Vector3 ShotDir { get; private set; }
    public bool IsPrecisionLocked { get; private set; }
    public bool IsSelfAiming => IsDrawing && ChargePercent >= 1f && AimMark != null;

    public WeaponData Weapon
    {
        get
        {
            if (loadout == null) return null;
            var w = loadout.GetMainWeapon();
            if (w != null && w.isRanged) return w;
            w = loadout.GetOffhandWeapon();
            return w != null && w.isRanged ? w : null;
        }
    }

    enum AimPhase { Charge, AimIn, Precision, Drift }

    Camera _cam;
    float _drawStart;
    float _reloadUntil;
    SpellComposer _composer;
    SpellController _spells;

    AimPhase _phase = AimPhase.Charge;
    Vector3 _aimFrom;
    float _aimInStart;
    float _aimInDuration;
    float _precisionStart;
    float _driftYaw;
    Transform _phaseMark;

    void Awake()
    {
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (inventory == null) inventory = GetComponent<Inventory>();
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (targeting == null) targeting = GetComponent<PlayerTargeting>();
        _composer = GetComponent<SpellComposer>();
        _spells = GetComponent<SpellController>();
        _cam = Camera.main;
        if (enemyLayers.value == 0 && targeting != null)
            enemyLayers = targeting.enemyLayers;
        ShotDir = transform.forward;
    }

    void Update()
    {
        if (resources != null && resources.IsDead)
        {
            CancelDraw();
            return;
        }

        if (!HasRangedEquipped)
        {
            CancelDraw();
            return;
        }

        if (Blocked())
        {
            CancelDraw();
            return;
        }

        var weapon = Weapon;
        if (weapon.useCharge)
            TickBow(weapon);
        else
            TickCrossbow(weapon);
    }

    bool Blocked()
    {
        if (_composer != null && _composer.IsComposing) return true;
        if (_spells != null && _spells.BlocksMelee) return true;
        return false;
    }

    void TickBow(WeaponData weapon)
    {
        if (IsReloading) return;

        if (Input.GetKeyDown(KeyCode.Space) && IsDrawing)
        {
            CancelDraw();
            return;
        }

        if (Input.GetMouseButtonDown(0) && !IsDrawing)
            BeginDraw(weapon);

        if (IsDrawing)
        {
            float max = Mathf.Max(0.05f, weapon.chargeDuration);
            ChargePercent = Mathf.Clamp01((Time.time - _drawStart) / max);
            TickSelfAim();
            if (weapon.maxHoldTime > 0f && Time.time - _drawStart >= weapon.maxHoldTime)
                Fire(weapon, ChargePercent);
        }

        if (IsDrawing && Input.GetMouseButtonUp(0))
            Fire(weapon, ChargePercent);
    }

    void TickCrossbow(WeaponData weapon)
    {
        if (IsDrawing) CancelDraw();
        if (IsReloading) return;
        if (!Input.GetMouseButtonDown(0)) return;
        ResetAimState();
        Vector3 dir = AimDir();
        AimMark = BestAimMark(dir);
        ShotDir = dir;
        Fire(weapon, 1f);
    }

    void BeginDraw(WeaponData weapon)
    {
        if (resources != null && !resources.HasStamina(weapon.staminaCost * 0.5f))
            return;
        IsDrawing = true;
        _drawStart = Time.time;
        ChargePercent = 0f;
        ResetAimState();
    }

    void CancelDraw()
    {
        IsDrawing = false;
        ChargePercent = 0f;
        ResetAimState();
    }

    void ResetAimState()
    {
        _phase = AimPhase.Charge;
        AimMark = null;
        IsPrecisionLocked = false;
        _phaseMark = null;
        _driftYaw = 0f;
        ShotDir = flatten(transform.forward);
    }

    void TickSelfAim()
    {
        Vector3 cursor = AimDir();
        Transform mark = BestAimMark(cursor);
        AimMark = mark;
        IsPrecisionLocked = false;

        if (ChargePercent < 1f || mark == null)
        {
            _phase = AimPhase.Charge;
            _phaseMark = null;
            ShotDir = cursor;
            return;
        }

        if (targeting != null && targeting.CurrentTarget != mark)
            targeting.SetTarget(mark);

        Vector3 toMark = flatten(mark.position - transform.position);
        if (toMark.sqrMagnitude < 0.01f) toMark = cursor;
        float quality = AimQuality(cursor, mark);

        if (_phase == AimPhase.Charge || _phaseMark != mark)
            EnterAimIn(cursor, mark, quality);

        if (_phase == AimPhase.AimIn)
        {
            float t = _aimInDuration > 0.01f
                ? Mathf.Clamp01((Time.time - _aimInStart) / _aimInDuration)
                : 1f;
            ShotDir = Vector3.Slerp(_aimFrom, toMark, Smooth(t)).normalized;
            if (t >= 1f)
                EnterPrecision(mark);
            return;
        }

        if (_phase == AimPhase.Precision)
        {
            ShotDir = toMark;
            float window = Mathf.Lerp(precisionWindowMin, precisionWindowMax, quality);
            if (Time.time - _precisionStart < window)
            {
                IsPrecisionLocked = true;
                return;
            }
            EnterDrift(cursor, mark);
        }

        if (_phase == AimPhase.Drift)
            ShotDir = (Quaternion.Euler(0f, _driftYaw, 0f) * toMark).normalized;
    }

    void EnterAimIn(Vector3 from, Transform mark, float quality)
    {
        _phase = AimPhase.AimIn;
        _phaseMark = mark;
        _aimFrom = from.sqrMagnitude > 0.01f ? from.normalized : flatten(transform.forward);
        _aimInStart = Time.time;
        _aimInDuration = Mathf.Lerp(aimInSlow, aimInFast, quality);
        IsPrecisionLocked = false;
    }

    void EnterPrecision(Transform mark)
    {
        _phase = AimPhase.Precision;
        _phaseMark = mark;
        _precisionStart = Time.time;
        IsPrecisionLocked = true;
    }

    void EnterDrift(Vector3 cursor, Transform mark)
    {
        _phase = AimPhase.Drift;
        _phaseMark = mark;
        IsPrecisionLocked = false;
        Vector3 to = flatten(mark.position - transform.position);
        Vector3 side = Vector3.Cross(Vector3.up, cursor.sqrMagnitude > 0.01f ? cursor : transform.forward);
        float away = Vector3.Dot(side, to) >= 0f ? -1f : 1f;
        _driftYaw = Random.Range(driftYawMin, driftYawMax) * away;
    }

    void Fire(WeaponData weapon, float charge)
    {
        IsDrawing = false;
        if (weapon == null) return;
        if (weapon.useCharge && charge < weapon.minChargePercent)
        {
            ChargePercent = 0f;
            ResetAimState();
            return;
        }

        if (!TryConsumeAmmo(weapon))
        {
            ChargePercent = 0f;
            ResetAimState();
            return;
        }

        float cost = weapon.useCharge
            ? Mathf.Lerp(weapon.staminaCost * 0.5f, weapon.staminaCost, charge)
            : weapon.staminaCost;
        if (resources != null)
        {
            if (!resources.HasStamina(cost))
            {
                ChargePercent = 0f;
                ResetAimState();
                return;
            }
            resources.SpendStamina(cost);
        }

        Vector3 dir = ShotDir.sqrMagnitude > 0.01f ? ShotDir : AimDir();
        Transform mark = AimMark != null ? AimMark : BestAimMark(dir);
        bool precision = IsPrecisionLocked;
        bool landed = precision ? RollPrecisionHit() : Random.value < HitChance(weapon, charge, AimQuality(dir, mark));

        if (!landed && mark != null)
            dir = MissDir(dir, mark);

        Vector3 spawnPos = transform.position + Vector3.up * 1.5f + dir * 0.5f;
        Quaternion baseRot = Quaternion.LookRotation(dir);
        float dmgMult = weapon.useCharge ? Mathf.Lerp(0.5f, 1f, charge) : 1f;
        float spdMult = weapon.useCharge ? Mathf.Lerp(0.5f, 1f, charge) : 1f;
        int shots = Mathf.Max(1, weapon.projectilesPerShot);
        GameObject prefab = weapon.projectilePrefab != null
            ? weapon.projectilePrefab
            : Projectile.DefaultPrefab();

        LayerMask layers = weapon.targetLayers;
        if (layers.value == 0)
            layers = enemyLayers.value != 0 ? enemyLayers : targeting != null ? targeting.enemyLayers : ~0;

        for (int i = 0; i < shots; i++)
        {
            float spread = weapon.spreadAngle > 0f
                ? Random.Range(-weapon.spreadAngle, weapon.spreadAngle) : 0f;
            if (precision || landed) spread *= 0.15f;
            Quaternion rot = baseRot * Quaternion.Euler(0f, spread, 0f);
            GameObject go = Instantiate(prefab, spawnPos, rot);
            go.SetActive(true);
            var proj = go.GetComponent<Projectile>();
            if (proj != null)
            {
                proj.Initialize(
                    weapon.damage * dmgMult,
                    weapon.staggerForce * dmgMult,
                    weapon.projectileSpeed * spdMult,
                    weapon.projectileLifetime,
                    layers,
                    transform);
            }
        }

        ChargePercent = 0f;
        ResetAimState();
        if (weapon.reloadDuration > 0f)
            _reloadUntil = Time.time + weapon.reloadDuration;
    }

    /// <summary>Позже: бросок в точном окне. Сейчас окно = попадание в направление цели.</summary>
    bool RollPrecisionHit()
    {
        return true;
    }

    bool TryConsumeAmmo(WeaponData weapon)
    {
        if (weapon.ammoItem == null) return true;
        int need = Mathf.Max(1, weapon.ammoPerShot);
        if (inventory == null) inventory = GetComponent<Inventory>();
        if (inventory == null) return true;
        if (inventory.CountOf(weapon.ammoItem) < need) return false;
        return inventory.Remove(weapon.ammoItem, need) >= need;
    }

    Transform BestAimMark(Vector3 cursor)
    {
        if (targeting == null) return null;
        if (targeting.CurrentTarget != null && targeting.IsValidEnemy(targeting.CurrentTarget))
        {
            Vector3 to = targeting.CurrentTarget.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude <= aimRange * aimRange && Vector3.Angle(cursor, to) <= aimMaxAngle)
                return targeting.CurrentTarget;
        }
        return targeting.FindClosestToDirection(cursor, aimRange);
    }

    float AimQuality(Vector3 cursor, Transform mark)
    {
        if (mark == null) return 0f;
        Vector3 to = mark.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return 1f;
        float ang = Vector3.Angle(cursor, to);
        if (ang >= aimMaxAngle) return 0f;
        if (ang <= aimFullBonusAngle) return 1f;
        return 1f - Mathf.InverseLerp(aimFullBonusAngle, aimMaxAngle, ang);
    }

    float HitChance(WeaponData weapon, float charge, float aim)
    {
        float chance = Mathf.Lerp(baseHitChance, maxHitChance, aim);
        if (weapon != null && weapon.useCharge)
            chance += Mathf.Lerp(0f, 0.08f, charge);
        return Mathf.Clamp01(chance);
    }

    Vector3 MissDir(Vector3 cursor, Transform mark)
    {
        Vector3 to = mark.position - transform.position;
        to.y = 0f;
        Vector3 side = Vector3.Cross(Vector3.up, cursor.sqrMagnitude > 0.01f ? cursor : transform.forward);
        float away = Vector3.Dot(side, to) >= 0f ? -1f : 1f;
        float yaw = Random.Range(missYawMin, missYawMax) * away;
        return Quaternion.Euler(0f, yaw, 0f) * cursor;
    }

    Vector3 AimDir()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam != null)
        {
            Ray ray = _cam.ScreenPointToRay(Input.mousePosition);
            if (new Plane(Vector3.up, transform.position).Raycast(ray, out float dist))
            {
                Vector3 dir = ray.GetPoint(dist) - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f) return dir.normalized;
            }
        }
        if (targeting != null && targeting.CurrentTarget != null)
        {
            Vector3 dir = targeting.CurrentTarget.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        }
        return flatten(transform.forward);
    }

    static Vector3 flatten(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.01f ? v.normalized : Vector3.forward;
    }

    static float Smooth(float t)
    {
        return t * t * (3f - 2f * t);
    }
}
