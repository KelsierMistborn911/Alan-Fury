using UnityEngine;

/// <summary>
/// Данные предмета инвентаря. Создание: ПКМ в Project → Create → Inventory → Item Data.
/// Для экипировки (оружие/щит) заполни поле weapon — при экипировке оно уйдёт в PlayerLoadout.
/// </summary>
[CreateAssetMenu(fileName = "NewItem", menuName = "Inventory/Item Data")]
public class ItemData : ScriptableObject
{
    public enum ItemType { Equipment, Consumable, Resource }
    public enum UseEffect { None, SummonAwareWolf }

    [Header("Общее")]
    public string itemName = "Предмет";
    [Tooltip("Готовый Sprite. Либо перетащи PNG как Sprite, либо заполни iconTexture.")]
    public Sprite icon;
    [Tooltip("PNG/JPG как Texture2D. Если icon пуст — из этой текстуры соберётся спрайт.")]
    public Texture2D iconTexture;
    [Tooltip("Имя в Resources/InventoryIcons без расширения (pack_horn, bow…).")]
    public string iconResource;
    public ItemType type = ItemType.Resource;

    [Tooltip("Макс. количество в одном слоте. 1 — предмет не стакается (экипировка).")]
    public int maxStack = 1;

    [Header("Размер в сетке сумки")]
    [Tooltip("Клеток по ширине. Лук — 8.")]
    public int width = 1;
    [Tooltip("Клеток по высоте. Лук — 2.")]
    public int height = 1;

    public int CellsW => Mathf.Max(1, width);
    public int CellsH => Mathf.Max(1, height);

    [Header("Использование из сумки")]
    [Tooltip("ПКМ → Использовать. Не экипировка.")]
    public bool usable;
    [Tooltip("Съесть слот после успешного использования.")]
    public bool consumeOnUse = true;
    public UseEffect useEffect = UseEffect.None;

    [Header("Экипировка (только для type = Equipment)")]
    [Tooltip("Оружие/щит, которое встанет в PlayerLoadout при экипировке.")]
    public WeaponData weapon;

    public bool CanUse => usable && useEffect != UseEffect.None;

    public Sprite ResolvedIcon => ItemIcons.Resolve(this);

    /// <summary>Если стоит 1×1 — подставить габарит по типу/имени.</summary>
    public void EnsureFootprint()
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;
        if (width != 1 || height != 1) return;

        if (weapon != null)
        {
            FootprintForWeapon(weapon.type, out width, out height);
            return;
        }

        string key = !string.IsNullOrEmpty(iconResource) ? iconResource : itemName;
        if (string.IsNullOrEmpty(key)) key = name;
        key = key.Trim().ToLowerInvariant();
        if (key == "bow" || key == "лук") { width = 8; height = 2; }
        else if (key == "crossbow" || key == "арбалет") { width = 6; height = 2; }
        else if (key == "sword" || key == "меч") { width = 2; height = 4; }
        else if (key == "shield" || key == "щит") { width = 2; height = 3; }
        else if (key == "arrows" || key == "стрелы") { width = 1; height = 2; }
        else if (key == "bolts" || key == "болты") { width = 1; height = 2; }
        else if (key == "pack_horn" || key == "рог стаи") { width = 2; height = 1; }
    }

    public static void FootprintForWeapon(WeaponData.WeaponType type, out int w, out int h)
    {
        switch (type)
        {
            case WeaponData.WeaponType.Bow: w = 8; h = 2; return;
            case WeaponData.WeaponType.Crossbow: w = 6; h = 2; return;
            case WeaponData.WeaponType.Sword: w = 2; h = 4; return;
            case WeaponData.WeaponType.Shield: w = 2; h = 3; return;
            case WeaponData.WeaponType.Spear: w = 1; h = 5; return;
            case WeaponData.WeaponType.Staff: w = 1; h = 5; return;
            case WeaponData.WeaponType.Axe: w = 2; h = 3; return;
            case WeaponData.WeaponType.Dagger: w = 1; h = 2; return;
            default: w = 2; h = 2; return;
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (icon == null && iconTexture != null)
            icon = ItemIcons.FromTexture(iconTexture);
    }
#endif
}
