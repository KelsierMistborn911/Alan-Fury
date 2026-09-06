using UnityEngine;

/// <summary>
/// Экранная таблица справа сверху: язык знаков, слоты, текущий набор.
/// Вешать на игрока рядом со SpellSlots.
/// </summary>
public class SpellHud : MonoBehaviour
{
    public SpellSlots slots;
    public SpellComposer composer;

    public bool visible = true;
    public int right = 10;
    public int top = 10;
    public int width = 440;

    [Header("Иконки (пусто = чёрный квадрат)")]
    public Texture healIcon;
    public Texture pillarIcon;
    public Texture flashIcon;

    int _x;
    Texture2D _black;

    GUIStyle _box;
    GUIStyle _title;
    GUIStyle _row;
    GUIStyle _dim;

    void Awake()
    {
        if (slots == null) slots = GetComponent<SpellSlots>();
        if (composer == null) composer = GetComponent<SpellComposer>();
    }

    void OnGUI()
    {
        if (!visible) return;
        EnsureStyles();

        bool composing = composer != null && composer.IsComposing;
        int h = composing ? 430 : 360;
        _x = Screen.width - width - right;
        GUI.Box(new Rect(_x, top, width, h), GUIContent.none, _box);

        float y = top + 8;
        GUI.Label(new Rect(_x + 8, y, width - 16, 18), "Заклинания", _title);
        y += 22;
        GUI.Label(new Rect(_x + 8, y, width - 16, 18),
            "4 фокус в правую / ещё 4 в левую.  1 меч  2 щит  3 голос", _dim);
        y += 20;
        GUI.Label(new Rect(_x + 8, y, width - 16, 18),
            "куда:  ↑ себе   ← цель   ↓ область   → аура", _row);
        y += 18;
        GUI.Label(new Rect(_x + 8, y, width - 16, 18),
            "что:   ↑ контроль   ← сияние   ↓ оплот   → благодать", _row);
        y += 18;
        GUI.Label(new Rect(_x + 8, y, width - 16, 18),
            "как:   ↑ вспышка   ← узко   ↓ у ног   → широко", _row);
        y += 22;
        DrawKnown(ref y, healIcon, "↑←↑  лечение");
        DrawKnown(ref y, pillarIcon, "↓←→  столб");
        DrawKnown(ref y, flashIcon, "↑↑↑  вспышка");
        y += 8;

        if (composing)
        {
            string ch = composer.HasChannel
                ? SpellSlots.ChannelLabel(composer.Channel)
                : "жми 1-4";
            string signs = "—";
            if (composer.Signs != null && composer.Signs.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < composer.Signs.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(SpellSlots.SignGlyph(composer.Signs[i]));
                }
                signs = sb.ToString();
            }
            string need = composer.HasChannel
                ? composer.Signs.Count + "/" + composer.Required
                : "";
            string extra = "";
            if (composer.HasChannel)
                extra = SpellSlots.ExtraRemaining(composer.Channel, composer.Signs.Count);
            GUI.Label(new Rect(_x + 8, y, width - 16, 20),
                "набор: " + ch + "  " + signs + "  " + need, _title);
            y += 20;
            if (extra.Length > 0)
            {
                GUI.Label(new Rect(_x + 8, y, width - 16, 18),
                    "ещё: " + extra, _title);
                y += 20;
            }
            else if (composer.HasChannel && SpellSlots.Tax(composer.Channel) > 0
                     && composer.Signs.Count < 3)
            {
                GUI.Label(new Rect(_x + 8, y, width - 16, 18),
                    "потом: " + SpellSlots.ExtraText(composer.Channel), _dim);
                y += 20;
            }
            y += 6;
        }

        DrawSlot(ref y, SpellChannel.Hand1);
        y += 6;
        DrawSlot(ref y, SpellChannel.Hand2);
        y += 6;
        DrawSlot(ref y, SpellChannel.Voice);
        y += 6;
        DrawSlot(ref y, SpellChannel.Mind);
    }

    void DrawSlot(ref float y, SpellChannel ch)
    {
        string key = SpellSlots.ChannelKey(ch);
        string label = SpellSlots.ChannelLabel(ch);
        Texture icon = IconOf(ch);
        GUI.DrawTexture(new Rect(_x + 8, y, 16, 16), icon, ScaleMode.StretchToFill);

        if (slots == null || slots.IsEmpty(ch))
        {
            GUI.Label(new Rect(_x + 28, y, width - 36, 16),
                key + "  " + label + "  —", _dim);
            y += 18;
            return;
        }

        var b = slots.Get(ch);
        string st = SpellSlots.StateLabel(slots.State(ch));
        GUI.Label(new Rect(_x + 28, y, width - 36, 16),
            key + "  " + label + "  " + st + "  " + b.ShortName + "  " + b.SignsText +
            "  " + b.manaInvested.ToString("0"),
            _row);
        y += 18;
    }

    void DrawKnown(ref float y, Texture icon, string text)
    {
        EnsureBlack();
        GUI.DrawTexture(new Rect(_x + 8, y, 16, 16), icon != null ? icon : _black, ScaleMode.StretchToFill);
        GUI.Label(new Rect(_x + 28, y, width - 36, 18), text, _title);
        y += 20;
    }

    Texture IconOf(SpellChannel ch)
    {
        EnsureBlack();
        if (slots == null || slots.IsEmpty(ch)) return _black;
        var id = SpellBook.Resolve(slots.Get(ch).signs);
        Texture t = null;
        if (id == SpellId.SelfHealBurst) t = healIcon;
        else if (id == SpellId.LightPillar) t = pillarIcon;
        else if (id == SpellId.BlindFlash) t = flashIcon;
        return t != null ? t : _black;
    }

    void EnsureBlack()
    {
        if (_black != null) return;
        _black = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        _black.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
        _black.Apply();
    }

    void EnsureStyles()
    {
        if (_box != null) return;
        _box = new GUIStyle(GUI.skin.box);
        _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
        _title.normal.textColor = Color.white;
        _row = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        _row.normal.textColor = new Color(0.92f, 0.92f, 0.88f);
        _dim = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        _dim.normal.textColor = new Color(0.7f, 0.7f, 0.68f);
    }
}
