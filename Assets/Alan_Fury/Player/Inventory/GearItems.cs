using UnityEngine;

/// <summary>
/// ItemData вокруг уже существующего WeaponData (меч/щит/лук с префаба).
/// Боевые цифры не копирует — ссылка на тот же ассет.
/// </summary>
public static class GearItems
{
    public static ItemData Wrap(WeaponData weapon)
    {
        if (weapon == null) return null;

        var item = ScriptableObject.CreateInstance<ItemData>();
        string name = DisplayName(weapon);
        item.name = name;
        item.itemName = name;
        item.type = ItemData.ItemType.Equipment;
        item.maxStack = 1;
        item.weapon = weapon;
        item.iconResource = ItemIcons.KeyForType(weapon.type);
        if (weapon.icon != null) item.icon = weapon.icon;
        item.EnsureFootprint();
        ItemIcons.Resolve(item);
        return item;
    }

    public static string DisplayName(WeaponData weapon)
    {
        if (weapon == null) return "Оружие";
        if (!string.IsNullOrEmpty(weapon.weaponName) && weapon.weaponName != "Оружие")
            return weapon.weaponName;
        if (!string.IsNullOrEmpty(weapon.name)
            && weapon.name != "Оружие"
            && weapon.name != "NewWeapon")
            return weapon.name;
        switch (weapon.type)
        {
            case WeaponData.WeaponType.Sword: return "Меч";
            case WeaponData.WeaponType.Shield: return "Щит";
            case WeaponData.WeaponType.Bow: return "Лук";
            case WeaponData.WeaponType.Crossbow: return "Арбалет";
            default: return weapon.type.ToString();
        }
    }
}
