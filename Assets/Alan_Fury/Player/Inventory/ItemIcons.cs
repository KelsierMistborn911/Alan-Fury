using UnityEngine;

/// <summary>
/// Иконки инвентаря. Приоритет:
///   1) Sprite, уже назначенный на ItemData.icon
///   2) Texture2D на ItemData.iconTexture (перетащи PNG/JPG)
///   3) Resources/InventoryIcons/{имя} — Sprite или Texture2D
///   4) цветной квадрат с буквой, чтобы слот не был пустым
/// </summary>
public static class ItemIcons
{
    public static Sprite Resolve(ItemData item)
    {
        if (item == null) return null;
        if (item.icon != null) return item.icon;
        if (item.weapon != null && item.weapon.icon != null)
        {
            item.icon = item.weapon.icon;
            return item.icon;
        }

        if (item.iconTexture != null)
        {
            item.icon = FromTexture(item.iconTexture);
            return item.icon;
        }

        string[] keys = KeysFor(item);
        for (int i = 0; i < keys.Length; i++)
        {
            Sprite fromRes = LoadResource(keys[i]);
            if (fromRes != null)
            {
                item.icon = fromRes;
                return fromRes;
            }
        }

        item.icon = MakeLetter(item.itemName, FallbackColor(item));
        return item.icon;
    }

    public static Sprite LoadResource(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        string[] paths =
        {
            key,
            "InventoryIcons/" + key,
            "InventoryIcons/" + Sanitize(key)
        };

        for (int i = 0; i < paths.Length; i++)
        {
            var sprite = Resources.Load<Sprite>(paths[i]);
            if (sprite != null) return sprite;
            var tex = Resources.Load<Texture2D>(paths[i]);
            if (tex != null) return FromTexture(tex);
        }
        return null;
    }

    public static Sprite FromTexture(Texture2D tex)
    {
        if (tex == null) return null;
        return Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    public static Sprite MakeLetter(string name, Color color)
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        var fill = color;
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
        tex.SetPixels(pixels);
        tex.Apply();

        var sprite = FromTexture(tex);
        sprite.name = string.IsNullOrEmpty(name) ? "item_icon" : name;
        return sprite;
    }

    static string[] KeysFor(ItemData item)
    {
        var list = new System.Collections.Generic.List<string>(6);
        AddKey(list, item.iconResource);
        AddKey(list, item.itemName);
        AddKey(list, item.name);
        AddKey(list, Alias(item.itemName));
        AddKey(list, Alias(item.name));
        if (item.weapon != null)
        {
            AddKey(list, KeyForType(item.weapon.type));
            AddKey(list, item.weapon.weaponName);
            AddKey(list, Alias(item.weapon.weaponName));
        }
        return list.ToArray();
    }

    static void AddKey(System.Collections.Generic.List<string> list, string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!list.Contains(key)) list.Add(key);
        string s = Sanitize(key);
        if (!string.IsNullOrEmpty(s) && !list.Contains(s)) list.Add(s);
    }

    static string Alias(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        switch (name.Trim().ToLowerInvariant())
        {
            case "лук": return "bow";
            case "арбалет": return "crossbow";
            case "стрелы": return "arrows";
            case "болты": return "bolts";
            case "рог стаи": return "pack_horn";
            case "меч": return "sword";
            case "щит": return "shield";
            case "sword": return "sword";
            case "shield": return "shield";
            default: return null;
        }
    }

    public static string KeyForType(WeaponData.WeaponType type)
    {
        switch (type)
        {
            case WeaponData.WeaponType.Sword: return "sword";
            case WeaponData.WeaponType.Shield: return "shield";
            case WeaponData.WeaponType.Bow: return "bow";
            case WeaponData.WeaponType.Crossbow: return "crossbow";
            default: return type.ToString().ToLowerInvariant();
        }
    }

    static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "item";
        return name.Trim().ToLowerInvariant().Replace(' ', '_');
    }

    static Color FallbackColor(ItemData item)
    {
        if (item == null) return new Color(0.35f, 0.35f, 0.32f, 1f);
        if (item.useEffect == ItemData.UseEffect.SummonAwareWolf)
            return new Color(0.42f, 0.28f, 0.16f, 1f);
        if (item.type == ItemData.ItemType.Equipment)
            return new Color(0.32f, 0.30f, 0.18f, 1f);
        if (item.type == ItemData.ItemType.Consumable)
            return new Color(0.28f, 0.36f, 0.22f, 1f);
        return new Color(0.30f, 0.30f, 0.34f, 1f);
    }
}
