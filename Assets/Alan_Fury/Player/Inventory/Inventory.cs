using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Инвентарь игрока: слоты предметов (стакаются по maxStack) + экипировка.
/// Экипировка НЕ дублирует PlayerLoadout — при Equip() пишет weapon предмета
/// в существующие поля rightHandWeapon/leftHandWeapon, боевая система не меняется.
/// UI подписывается на onChanged.
/// </summary>
public class Inventory : MonoBehaviour
{
    [System.Serializable]
    public class Slot
    {
        public ItemData item;
        public int count;

        public bool IsEmpty => item == null || count <= 0;

        public void Clear() { item = null; count = 0; }
    }

    [Header("Ссылки")]
    [Tooltip("Пусто — найдётся на этом же объекте.")]
    public PlayerLoadout loadout;

    [Header("Слоты")]
    [Tooltip("Число слотов сумки.")]
    public int slotCount = 20;

    [Header("Стартовый дальний набор")]
    public bool grantRangedKit = true;
    public int startingArrows = 30;
    public int startingBolts = 20;

    public List<Slot> Slots { get; private set; } = new List<Slot>();

    // Что сейчас экипировано (для UI и снятия).
    public ItemData EquippedRight { get; private set; }
    public ItemData EquippedLeft { get; private set; }

    /// <summary>Инвентарь изменился (добавили/убрали/экипировали) — UI перерисовывается.</summary>
    public System.Action onChanged;

    void Awake()
    {
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        for (int i = 0; i < slotCount; i++) Slots.Add(new Slot());
    }

    void Start()
    {
        if (GetComponent<RangedController>() == null)
            gameObject.AddComponent<RangedController>();
        if (grantRangedKit)
            RangedKit.Grant(this, startingArrows, startingBolts);
    }

    public bool HasTwoHandEquipped()
    {
        return EquippedRight != null
            && EquippedLeft == EquippedRight
            && EquippedRight.weapon != null
            && EquippedRight.weapon.OccupiesBothHands;
    }

    // ==================== Добавление / удаление ====================

    /// <summary>Добавить предмет. Возвращает сколько НЕ влезло (0 = всё добавлено).</summary>
    public int Add(ItemData item, int count = 1)
    {
        if (item == null || count <= 0) return count;

        // Сначала докладываем в существующие стаки
        foreach (var slot in Slots)
        {
            if (count <= 0) break;
            if (slot.item != item || slot.count >= item.maxStack) continue;

            int space = item.maxStack - slot.count;
            int put = Mathf.Min(space, count);
            slot.count += put;
            count -= put;
        }

        // Потом в пустые слоты
        foreach (var slot in Slots)
        {
            if (count <= 0) break;
            if (!slot.IsEmpty) continue;

            int put = Mathf.Min(item.maxStack, count);
            slot.item = item;
            slot.count = put;
            count -= put;
        }

        onChanged?.Invoke();
        return count;
    }

    /// <summary>Убрать предмет (из любых слотов). Возвращает сколько реально убрано.</summary>
    public int Remove(ItemData item, int count = 1)
    {
        if (item == null || count <= 0) return 0;

        int removed = 0;
        foreach (var slot in Slots)
        {
            if (removed >= count) break;
            if (slot.item != item) continue;

            int take = Mathf.Min(slot.count, count - removed);
            slot.count -= take;
            removed += take;
            if (slot.count <= 0) slot.Clear();
        }

        if (removed > 0) onChanged?.Invoke();
        return removed;
    }

    public int CountOf(ItemData item)
    {
        int total = 0;
        foreach (var slot in Slots)
            if (slot.item == item) total += slot.count;
        return total;
    }

    // ==================== Экипировка ====================

    /// <summary>Экипировать предмет из слота index. Правая рука — оружие, левая — щит.</summary>
    public bool Equip(int index)
    {
        if (index < 0 || index >= Slots.Count) return false;
        var slot = Slots[index];
        if (slot.IsEmpty || slot.item.type != ItemData.ItemType.Equipment || slot.item.weapon == null)
            return false;
        if (loadout == null) return false;

        var weapon = slot.item.weapon;
        bool twoHand = weapon.OccupiesBothHands;
        bool toLeft = !twoHand && weapon.type == WeaponData.WeaponType.Shield;

        if (twoHand || HasTwoHandEquipped()) UnequipTwoHand();
        else if (toLeft) UnequipLeft();
        else UnequipRight();

        if (twoHand)
        {
            loadout.rightHandWeapon = weapon;
            loadout.leftHandWeapon = weapon;
            EquippedRight = slot.item;
            EquippedLeft = slot.item;
        }
        else if (toLeft)
        {
            loadout.leftHandWeapon = weapon;
            EquippedLeft = slot.item;
        }
        else
        {
            loadout.rightHandWeapon = weapon;
            EquippedRight = slot.item;
        }

        // Предмет уходит из сумки в "надето"
        slot.count -= 1;
        if (slot.count <= 0) slot.Clear();

        onChanged?.Invoke();
        return true;
    }

    public void UnequipRight()
    {
        if (HasTwoHandEquipped())
        {
            UnequipTwoHand();
            return;
        }
        if (EquippedRight == null) return;
        Add(EquippedRight, 1);
        EquippedRight = null;
        if (loadout != null) loadout.rightHandWeapon = null;
        onChanged?.Invoke();
    }

    public void UnequipLeft()
    {
        if (HasTwoHandEquipped())
        {
            UnequipTwoHand();
            return;
        }
        if (EquippedLeft == null) return;
        Add(EquippedLeft, 1);
        EquippedLeft = null;
        if (loadout != null) loadout.leftHandWeapon = null;
        onChanged?.Invoke();
    }

    public void UnequipTwoHand()
    {
        if (!HasTwoHandEquipped()) return;
        var item = EquippedRight;
        EquippedRight = null;
        EquippedLeft = null;
        if (loadout != null)
        {
            loadout.rightHandWeapon = null;
            loadout.leftHandWeapon = null;
        }
        if (item != null) Add(item, 1);
        onChanged?.Invoke();
    }
}
