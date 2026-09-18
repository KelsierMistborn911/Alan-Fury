using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Сумка — сетка клеток. Предмет занимает width×height.
/// Экипировка пишет weapon в PlayerLoadout, бой не меняется.
/// </summary>
public class Inventory : MonoBehaviour
{
    [System.Serializable]
    public class Slot
    {
        public ItemData item;
        public int count;
        public int x;
        public int y;

        public bool IsEmpty => item == null || count <= 0;
        public int W => item != null ? item.CellsW : 1;
        public int H => item != null ? item.CellsH : 1;

        public void Clear()
        {
            item = null;
            count = 0;
            x = 0;
            y = 0;
        }

        public bool ContainsCell(int cx, int cy)
        {
            if (IsEmpty) return false;
            return cx >= x && cy >= y && cx < x + W && cy < y + H;
        }
    }

    [Header("Ссылки")]
    [Tooltip("Пусто — найдётся на этом же объекте.")]
    public PlayerLoadout loadout;

    [Header("Сетка сумки")]
    public int gridWidth = 10;
    public int gridHeight = 8;

    [HideInInspector]
    public int slotCount = 20;

    [Header("Стартовый дальний набор")]
    public bool grantRangedKit = true;
    public int startingArrows = 30;
    public int startingBolts = 20;

    [Header("Стартовые расходники")]
    public bool grantPackHorn = true;

    public List<Slot> Slots { get; private set; } = new List<Slot>();

    public ItemData EquippedRight { get; private set; }
    public ItemData EquippedLeft { get; private set; }

    public System.Action onChanged;

    int[] _occ;

    void Awake()
    {
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (gridWidth < 8) gridWidth = 10;
        if (gridHeight < 4) gridHeight = 8;
        RebuildOcc();
    }

    void Start()
    {
        if (GetComponent<RangedController>() == null)
            gameObject.AddComponent<RangedController>();
        if (grantRangedKit)
            RangedKit.Grant(this, startingArrows, startingBolts);
        if (grantPackHorn)
            PackHorn.Grant(this);
        BindLoadoutItems();
    }

    void BindLoadoutItems()
    {
        if (loadout == null) return;

        if (EquippedRight == null && loadout.rightHandWeapon != null)
            EquippedRight = GearItems.Wrap(loadout.rightHandWeapon);

        if (loadout.HasTwoHandWeapon())
            EquippedLeft = EquippedRight;
        else if (EquippedLeft == null && loadout.leftHandWeapon != null)
            EquippedLeft = GearItems.Wrap(loadout.leftHandWeapon);

        onChanged?.Invoke();
    }

    public bool HasTwoHandEquipped()
    {
        return EquippedRight != null
            && EquippedLeft == EquippedRight
            && EquippedRight.weapon != null
            && EquippedRight.weapon.OccupiesBothHands;
    }

    public int SlotAt(int cx, int cy)
    {
        if (!InGrid(cx, cy) || _occ == null) return -1;
        int id = _occ[cy * gridWidth + cx];
        return id - 1;
    }

    // ==================== Добавление / удаление ====================

    public int Add(ItemData item, int count = 1)
    {
        if (item == null || count <= 0) return count;
        item.EnsureFootprint();

        foreach (var slot in Slots)
        {
            if (count <= 0) break;
            if (slot.IsEmpty || slot.item != item || slot.count >= item.maxStack) continue;
            int space = item.maxStack - slot.count;
            int put = Mathf.Min(space, count);
            slot.count += put;
            count -= put;
        }

        while (count > 0)
        {
            if (!TryFindSpace(item.CellsW, item.CellsH, out int px, out int py))
                break;
            int put = Mathf.Min(item.maxStack, count);
            var slot = new Slot
            {
                item = item,
                count = put,
                x = px,
                y = py
            };
            Slots.Add(slot);
            Paint(Slots.Count - 1);
            count -= put;
        }

        onChanged?.Invoke();
        return count;
    }

    public int Remove(ItemData item, int count = 1)
    {
        if (item == null || count <= 0) return 0;

        int removed = 0;
        for (int i = Slots.Count - 1; i >= 0 && removed < count; i--)
        {
            var slot = Slots[i];
            if (slot.item != item) continue;
            int take = Mathf.Min(slot.count, count - removed);
            slot.count -= take;
            removed += take;
            if (slot.count <= 0) slot.Clear();
        }

        if (removed > 0)
        {
            Compact();
            onChanged?.Invoke();
        }
        return removed;
    }

    public int CountOf(ItemData item)
    {
        int total = 0;
        foreach (var slot in Slots)
            if (slot.item == item) total += slot.count;
        return total;
    }

    // ==================== Использование ====================

    public bool Use(int index)
    {
        if (index < 0 || index >= Slots.Count) return false;
        var slot = Slots[index];
        if (slot.IsEmpty || !slot.item.CanUse) return false;

        ItemData item = slot.item;
        if (!ItemUse.Try(this, item)) return false;

        if (item.consumeOnUse)
        {
            slot.count -= 1;
            if (slot.count <= 0) slot.Clear();
            Compact();
        }

        onChanged?.Invoke();
        return true;
    }

    // ==================== Экипировка ====================

    public bool Equip(int index)
    {
        if (index < 0 || index >= Slots.Count) return false;
        var slot = Slots[index];
        if (slot.IsEmpty || slot.item.type != ItemData.ItemType.Equipment || slot.item.weapon == null)
            return false;
        if (loadout == null) return false;

        var taken = slot.item;
        var weapon = taken.weapon;
        bool twoHand = weapon.OccupiesBothHands;
        bool toLeft = !twoHand && weapon.type == WeaponData.WeaponType.Shield;

        slot.count -= 1;
        if (slot.count <= 0) slot.Clear();
        Compact();

        if (twoHand || HasTwoHandEquipped()) UnequipTwoHand();
        else if (toLeft) UnequipLeft();
        else UnequipRight();

        if (twoHand)
        {
            loadout.rightHandWeapon = weapon;
            loadout.leftHandWeapon = weapon;
            EquippedRight = taken;
            EquippedLeft = taken;
        }
        else if (toLeft)
        {
            loadout.leftHandWeapon = weapon;
            EquippedLeft = taken;
        }
        else
        {
            loadout.rightHandWeapon = weapon;
            EquippedRight = taken;
        }

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
        var item = EquippedRight;
        EquippedRight = null;
        if (loadout != null) loadout.rightHandWeapon = null;
        Add(item, 1);
    }

    public void UnequipLeft()
    {
        if (HasTwoHandEquipped())
        {
            UnequipTwoHand();
            return;
        }
        if (EquippedLeft == null) return;
        var item = EquippedLeft;
        EquippedLeft = null;
        if (loadout != null) loadout.leftHandWeapon = null;
        Add(item, 1);
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
    }

    // ==================== Сетка ====================

    bool InGrid(int x, int y)
    {
        return x >= 0 && y >= 0 && x < gridWidth && y < gridHeight;
    }

    void RebuildOcc()
    {
        _occ = new int[Mathf.Max(1, gridWidth) * Mathf.Max(1, gridHeight)];
        for (int i = 0; i < Slots.Count; i++)
            if (!Slots[i].IsEmpty) Paint(i);
    }

    void Paint(int index)
    {
        var s = Slots[index];
        if (s.IsEmpty) return;
        for (int yy = 0; yy < s.H; yy++)
            for (int xx = 0; xx < s.W; xx++)
            {
                int cx = s.x + xx;
                int cy = s.y + yy;
                if (InGrid(cx, cy))
                    _occ[cy * gridWidth + cx] = index + 1;
            }
    }

    bool TryFindSpace(int w, int h, out int px, out int py)
    {
        if (_occ == null) RebuildOcc();
        for (int y = 0; y <= gridHeight - h; y++)
        {
            for (int x = 0; x <= gridWidth - w; x++)
            {
                if (!RectFree(x, y, w, h)) continue;
                px = x;
                py = y;
                return true;
            }
        }
        px = 0;
        py = 0;
        return false;
    }

    bool RectFree(int x, int y, int w, int h)
    {
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int cx = x + xx;
                int cy = y + yy;
                if (!InGrid(cx, cy)) return false;
                if (_occ[cy * gridWidth + cx] != 0) return false;
            }
        return true;
    }

    void Compact()
    {
        for (int i = Slots.Count - 1; i >= 0; i--)
            if (Slots[i].IsEmpty) Slots.RemoveAt(i);
        RebuildOcc();
    }
}
