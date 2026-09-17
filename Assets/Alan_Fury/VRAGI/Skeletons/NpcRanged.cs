using UnityEngine;

/// <summary>
/// Дальний бой для NPC. Без ввода, без инвентаря.
/// Draw / Fire / Cancel — мозг лучника или отряд.
/// </summary>
public class NpcRanged : MonoBehaviour
{
    public PlayerLoadout loadout;
    public PlayerResources resources;
    public LayerMask targetLayers;
    public float muzzleHeight = 1.45f;
    public float muzzleForward = 0.45f;
    [Range(0f, 1f)] public float hitChance = 0.72f;
    public float missYawMin = 6f;
    public float missYawMax = 14f;

    public bool IsDrawing { get; private set; }
    public bool IsReloading => Time.time < _reloadUntil;
    public float ChargePercent { get; private set; }
    public float LastFireTime { get; private set; } = -99f;
    public bool WasFiring(float window) => Time.time - LastFireTime <= window;

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

    float _drawStart;
    float _reloadUntil;

    void Awake()
    {
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (targetLayers.value == 0)
            targetLayers = LayerMask.GetMask("Player", "Fury");
    }

    public void EnsureBow()
    {
        RangedKit.Ensure();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (loadout == null) return;
        if (loadout.rightHandWeapon == null || !loadout.rightHandWeapon.isRanged)
        {
            loadout.rightHandWeapon = RangedKit.BowWeapon;
            loadout.leftHandWeapon = RangedKit.BowWeapon;
        }
    }

    public bool BeginDraw()
    {
        var weapon = Weapon;
        if (weapon == null || IsReloading) return false;
        IsDrawing = true;
        _drawStart = Time.time;
        ChargePercent = 0f;
        return true;
    }

    public void TickDraw()
    {
        if (!IsDrawing) return;
        var weapon = Weapon;
        float max = weapon != null ? Mathf.Max(0.05f, weapon.chargeDuration) : 0.8f;
        ChargePercent = Mathf.Clamp01((Time.time - _drawStart) / max);
    }

    public void Cancel()
    {
        IsDrawing = false;
        ChargePercent = 0f;
    }

    public bool FireAt(Transform target, float extraYaw = 0f)
    {
        Vector3 dir = transform.forward;
        if (target != null)
        {
            dir = target.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
            else dir.Normalize();
        }
        if (Mathf.Abs(extraYaw) > 0.01f)
            dir = Quaternion.Euler(0f, extraYaw, 0f) * dir;
        return FireDir(dir, target);
    }

    public bool FireDir(Vector3 dir, Transform mark)
    {
        var weapon = Weapon;
        IsDrawing = false;
        if (weapon == null) return false;

        float charge = ChargePercent;
        if (weapon.useCharge && charge < weapon.minChargePercent)
        {
            ChargePercent = 0f;
            return false;
        }

        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
        dir.Normalize();

        bool landed = Random.value < hitChance;
        if (!landed && mark != null)
            dir = MissDir(dir, mark);

        Vector3 spawnPos = transform.position + Vector3.up * muzzleHeight + dir * muzzleForward;
        Quaternion baseRot = Quaternion.LookRotation(dir);
        float dmgMult = weapon.useCharge ? Mathf.Lerp(0.5f, 1f, charge) : 1f;
        float spdMult = weapon.useCharge ? Mathf.Lerp(0.5f, 1f, charge) : 1f;
        int shots = Mathf.Max(1, weapon.projectilesPerShot);
        GameObject prefab = weapon.projectilePrefab != null
            ? weapon.projectilePrefab
            : Projectile.DefaultPrefab();

        LayerMask layers = weapon.targetLayers.value != 0 ? weapon.targetLayers : targetLayers;

        for (int i = 0; i < shots; i++)
        {
            float spread = weapon.spreadAngle > 0f
                ? Random.Range(-weapon.spreadAngle, weapon.spreadAngle) : 0f;
            Quaternion rot = baseRot * Quaternion.Euler(0f, spread, 0f);
            GameObject go = Object.Instantiate(prefab, spawnPos, rot);
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
        LastFireTime = Time.time;
        float reload = weapon.reloadDuration > 0f ? weapon.reloadDuration : 0.35f;
        _reloadUntil = Time.time + reload;
        return true;
    }

    public void SetReload(float seconds)
    {
        _reloadUntil = Time.time + Mathf.Max(0f, seconds);
    }

    Vector3 MissDir(Vector3 cursor, Transform mark)
    {
        Vector3 to = mark.position - transform.position;
        to.y = 0f;
        Vector3 side = Vector3.Cross(Vector3.up, cursor);
        float away = Vector3.Dot(side, to) >= 0f ? -1f : 1f;
        float yaw = Random.Range(missYawMin, missYawMax) * away;
        return Quaternion.Euler(0f, yaw, 0f) * cursor;
    }
}
