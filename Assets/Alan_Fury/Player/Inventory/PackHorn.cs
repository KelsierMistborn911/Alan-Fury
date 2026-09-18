using UnityEngine;

/// <summary>
/// «Рог стаи». ПКМ в инвентаре → Использовать. Не тратится.
/// Спавнит одного оборотня ~50 м от игрока: знает цель и сразу атакует.
/// Анимация/звук рога — позже.
/// </summary>
public static class PackHorn
{
    public const string ItemName = "Рог стаи";
    public const float SpawnDistance = 50f;

    public static ItemData Item { get; private set; }

    static bool _ready;

    public static void Ensure()
    {
        if (_ready && Item != null) return;

        Item = ScriptableObject.CreateInstance<ItemData>();
        Item.name = ItemName;
        Item.itemName = ItemName;
        Item.type = ItemData.ItemType.Consumable;
        Item.maxStack = 1;
        Item.usable = true;
        Item.consumeOnUse = false;
        Item.useEffect = ItemData.UseEffect.SummonAwareWolf;
        Item.iconResource = "pack_horn";
        Item.width = 2;
        Item.height = 1;
        ItemIcons.Resolve(Item);

        _ready = true;
    }

    public static void Grant(Inventory inventory, int count = 1)
    {
        if (inventory == null) return;
        Ensure();
        if (inventory.CountOf(Item) > 0) return;
        inventory.Add(Item, Mathf.Max(1, count));
    }

    public static bool Activate(Inventory inventory)
    {
        if (inventory == null) return false;
        Transform user = inventory.transform;
        var pack = WerewolfPackManager.Instance;
        if (pack == null)
        {
            Debug.LogWarning("Рог стаи: на сцене нет WerewolfPackManager.");
            return false;
        }
        if (pack.wolfPrefab == null)
        {
            Debug.LogWarning("Рог стаи: у WerewolfPackManager не назначен wolfPrefab.");
            return false;
        }

        var wolf = pack.SpawnAwareWolf(user, SpawnDistance);
        if (wolf == null)
        {
            Debug.LogWarning("Рог стаи: не удалось поставить оборотня.");
            return false;
        }
        return true;
    }
}
