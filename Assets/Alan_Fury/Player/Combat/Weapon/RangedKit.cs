using UnityEngine;

/// <summary>
/// Рантайм-набор: лук, арбалет, стрелы, болты.
/// Ассетов в проекте нет — инстансы живут сессию и кладутся в Inventory.
/// </summary>
public static class RangedKit
{
    public static ItemData Bow { get; private set; }
    public static ItemData Crossbow { get; private set; }
    public static ItemData Arrows { get; private set; }
    public static ItemData Bolts { get; private set; }

    public static WeaponData BowWeapon { get; private set; }
    public static WeaponData CrossbowWeapon { get; private set; }

    static bool _ready;

    public static void Ensure()
    {
        if (_ready) return;

        Arrows = MakeResource("Стрелы", 99);
        Bolts = MakeResource("Болты", 99);

        BowWeapon = MakeWeapon(
            WeaponData.WeaponType.Bow,
            "Лук",
            damage: 9f,
            stagger: 3f,
            penetration: 1.5f,
            useCharge: true,
            chargeDuration: 0.85f,
            minCharge: 0.35f,
            speed: 22f,
            lifetime: 2.2f,
            stamina: 8f,
            ammo: Arrows,
            reload: 0f);

        CrossbowWeapon = MakeWeapon(
            WeaponData.WeaponType.Crossbow,
            "Арбалет",
            damage: 14f,
            stagger: 5f,
            penetration: 2.5f,
            useCharge: false,
            chargeDuration: 0.15f,
            minCharge: 1f,
            speed: 32f,
            lifetime: 2.4f,
            stamina: 10f,
            ammo: Bolts,
            reload: 2f);

        Bow = MakeEquipment("Лук", BowWeapon);
        Crossbow = MakeEquipment("Арбалет", CrossbowWeapon);

        _ready = true;
    }

    public static void Grant(Inventory inventory, int arrows, int bolts)
    {
        if (inventory == null) return;
        Ensure();

        if (inventory.CountOf(Bow) == 0 && inventory.EquippedRight != Bow && inventory.EquippedLeft != Bow)
            inventory.Add(Bow, 1);
        if (inventory.CountOf(Crossbow) == 0 && inventory.EquippedRight != Crossbow && inventory.EquippedLeft != Crossbow)
            inventory.Add(Crossbow, 1);

        int needArrows = Mathf.Max(0, arrows - inventory.CountOf(Arrows));
        int needBolts = Mathf.Max(0, bolts - inventory.CountOf(Bolts));
        if (needArrows > 0) inventory.Add(Arrows, needArrows);
        if (needBolts > 0) inventory.Add(Bolts, needBolts);
    }

    static ItemData MakeResource(string name, int stack)
    {
        var item = ScriptableObject.CreateInstance<ItemData>();
        item.name = name;
        item.itemName = name;
        item.type = ItemData.ItemType.Resource;
        item.maxStack = stack;
        return item;
    }

    static ItemData MakeEquipment(string name, WeaponData weapon)
    {
        var item = ScriptableObject.CreateInstance<ItemData>();
        item.name = name;
        item.itemName = name;
        item.type = ItemData.ItemType.Equipment;
        item.maxStack = 1;
        item.weapon = weapon;
        return item;
    }

    static WeaponData MakeWeapon(
        WeaponData.WeaponType type,
        string name,
        float damage,
        float stagger,
        float penetration,
        bool useCharge,
        float chargeDuration,
        float minCharge,
        float speed,
        float lifetime,
        float stamina,
        ItemData ammo,
        float reload)
    {
        var w = ScriptableObject.CreateInstance<WeaponData>();
        w.name = name;
        w.type = type;
        w.weaponName = name;
        w.damage = damage;
        w.staggerForce = stagger;
        w.penetration = penetration;
        w.isRanged = true;
        w.useCharge = useCharge;
        w.chargeDuration = chargeDuration;
        w.minChargePercent = minCharge;
        w.maxHoldTime = 3f;
        w.projectileSpeed = speed;
        w.projectileLifetime = lifetime;
        w.projectilesPerShot = 1;
        w.spreadAngle = 0f;
        w.ammoItem = ammo;
        w.ammoPerShot = 1;
        w.reloadDuration = reload;
        w.staminaCost = stamina;
        w.windupDuration = useCharge ? 0.08f : 0.12f;
        w.attackDuration = 0.15f;
        w.cooldownDuration = useCharge ? 0.25f : 0.35f;
        w.multPointBlank = 0.35f;
        w.multClinch = 0.5f;
        w.multClose = 0.75f;
        w.multMid = 1f;
        w.multFar = 1f;
        w.multSpecial = 1f;
        return w;
    }
}
