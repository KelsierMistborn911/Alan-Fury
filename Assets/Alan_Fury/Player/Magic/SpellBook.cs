using UnityEngine;

public enum SpellId
{
    None = 0,
    SelfHealBurst = 1,
    LightPillar = 2,
    BlindFlash = 3
}

public static class SpellBook
{
    public static SpellId Resolve(SpellSign[] signs)
    {
        if (signs == null || signs.Length < 3) return SpellId.None;
        var a = signs[0];
        var b = signs[1];
        var c = signs[2];
        if (a == SpellSign.Up && b == SpellSign.Left && c == SpellSign.Up)
            return SpellId.SelfHealBurst;
        if (a == SpellSign.Down && b == SpellSign.Left && c == SpellSign.Right)
            return SpellId.LightPillar;
        if (a == SpellSign.Up && b == SpellSign.Up && c == SpellSign.Up)
            return SpellId.BlindFlash;
        return SpellId.None;
    }

    public static bool NeedsGroundAim(SpellId id) => id == SpellId.LightPillar;

    public static string Title(SpellId id)
    {
        switch (id)
        {
            case SpellId.SelfHealBurst: return "Сильное лечение";
            case SpellId.LightPillar: return "Столб света";
            case SpellId.BlindFlash: return "Вспышка";
            default: return "—";
        }
    }

    public static string Combo(SpellId id)
    {
        switch (id)
        {
            case SpellId.SelfHealBurst: return "↑ ← ↑";
            case SpellId.LightPillar: return "↓ ← →";
            case SpellId.BlindFlash: return "↑ ↑ ↑";
            default: return "";
        }
    }
}
