using UnityEngine;

/// <summary>
/// Данные предмета инвентаря. Создание: ПКМ в Project → Create → Inventory → Item Data.
/// Для экипировки (оружие/щит) заполни поле weapon — при экипировке оно уйдёт в PlayerLoadout.
/// </summary>
[CreateAssetMenu(fileName = "NewItem", menuName = "Inventory/Item Data")]
public class ItemData : ScriptableObject
{
    public enum ItemType { Equipment, Consumable, Resource }

    [Header("Общее")]
    public string itemName = "Предмет";
    public Sprite icon;
    public ItemType type = ItemType.Resource;

    [Tooltip("Макс. количество в одном слоте. 1 — предмет не стакается (экипировка).")]
    public int maxStack = 1;

    [Header("Экипировка (только для type = Equipment)")]
    [Tooltip("Оружие/щит, которое встанет в PlayerLoadout при экипировке.")]
    public WeaponData weapon;
}
