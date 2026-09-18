using UnityEngine;

/// <summary>
/// Диспетчер использования предмета из сумки. Инвентарь не знает про волков.
/// </summary>
public static class ItemUse
{
    public static bool Try(Inventory inventory, ItemData item)
    {
        if (inventory == null || item == null || !item.CanUse) return false;

        switch (item.useEffect)
        {
            case ItemData.UseEffect.SummonAwareWolf:
                return PackHorn.Activate(inventory);
            default:
                Debug.LogWarning("ItemUse: нет обработчика для " + item.itemName);
                return false;
        }
    }
}
