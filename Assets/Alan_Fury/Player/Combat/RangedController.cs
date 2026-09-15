using UnityEngine;

/// <summary>
/// ƒальний бой отдельно от HumanoidCombat.
/// Ћук: зажать Ћ ћ Ч нат€г, отпустить Ч выстрел, Space Ч срыв.
/// јрбалет: клик Ч выстрел, затем reloadDuration.
/// </summary>
public class RangedController : MonoBehaviour
{
    public PlayerLoadout loadout;
    public Inventory inventory;
    public PlayerResources resources;
    public PlayerTargeting targeting;
    public LayerMask enemyLayers;

    public bool IsDrawing { get; private set; }
    public bool IsReloading => Time.time < _reloadUntil;
    public float ChargePercent { get; private set; }
    public bool HasRangedEquipped => Weapon != null && Weapon.isRanged && Weapon.OccupiesBothHands;

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

    Camera _cam;
    float _drawStart;
    float _reloadUntil;
    SpellComposer _composer;
    SpellController _spells;

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
        Fire(weapon, 1f);
    }

    void BeginDraw(WeaponData weapon)
    {
        if (resources != null && !resources.HasStamina(weapon.staminaCost * 0.5f))
            return;
        IsDrawing = true;
        _drawStart = Time.time;
        ChargePercent = 0f;
    }

    void CancelDraw()
    {
        IsDrawing = false;
        ChargePercent = 0f;
    }

    void Fire(WeaponData weapon, float charge)
    {
        IsDrawing = false;
        if (weapon == null) return;
        if (weapon.useCharge && charge < weapon.minChargePercent)
        {
            ChargePercent = 0f;
            return;
        }

        if (!TryConsumeAmmo(weapon))
        {
            ChargePercent = 0f;
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
                return;
            }
            resources.SpendStamina(cost);
        }

        Vector3 dir = AimDir();
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
            float spread = shots > 1
                ? Random.Range(-weapon.spreadAngle, weapon.spreadAngle) : 0f;
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
        if (weapon.reloadDuration > 0f)
            _reloadUntil = Time.time + weapon.reloadDuration;
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
        return transform.forward;
    }
}
