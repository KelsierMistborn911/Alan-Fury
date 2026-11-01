using UnityEngine;

/// <summary>
/// Вертикальная шкала инвеститурного света (7 сегментов).
/// Обычный свет — тёплый. Впитанный (белый) рисуется поверх.
/// </summary>
public class InvestitureHud : MonoBehaviour
{
    public InvestitureLight light;
    public bool visible = true;
    public int marginRight = 40;
    public int bottom = 80;
    public int cellH = 36;
    public int cellW = 48;

    Texture2D _fill;
    Texture2D _white;
    Texture2D _empty;
    Texture2D _frame;

    void Awake()
    {
        if (light == null) light = GetComponent<InvestitureLight>();
        _fill = MakeTex(new Color(1f, 0.88f, 0.55f, 0.92f));
        _white = MakeTex(new Color(1f, 1f, 1f, 0.95f));
        _empty = MakeTex(new Color(1f, 1f, 1f, 0.15f));
        _frame = MakeTex(new Color(0.85f, 0.82f, 0.75f, 0.7f));
    }

    void OnGUI()
    {
        if (!visible) return;
        if (light == null) light = GetComponent<InvestitureLight>();
        if (light == null) light = FindObjectOfType<InvestitureLight>();

        int levels = light != null ? light.levels : 7;
        float fill = light != null ? light.currentFill : 0f;
        float absorbed = light != null ? light.Absorbed : 0f;

        int totalH = levels * cellH;
        int x = Screen.width - cellW - marginRight;
        int y = Screen.height - bottom - totalH;

        GUI.Box(new Rect(x - 4, y - 4, cellW + 8, totalH + 8), GUIContent.none);

        for (int i = 0; i < levels; i++)
        {
            int seg = levels - 1 - i;
            float filled = Mathf.Clamp01(fill - seg);
            int sy = y + i * cellH;

            GUI.DrawTexture(new Rect(x, sy, cellW, cellH), _empty);
            if (filled > 0f)
                GUI.DrawTexture(new Rect(x, sy + cellH * (1f - filled), cellW, cellH * filled), _fill);

            GUI.DrawTexture(new Rect(x, sy, cellW, 1), _frame);
        }

        // белый слой — впитанный, рисуем снизу вверх отдельной колонкой слева
        if (absorbed > 0.01f)
        {
            int whiteH = Mathf.RoundToInt(Mathf.Clamp01(absorbed / levels) * totalH);
            int wy = y + totalH - whiteH;
            GUI.DrawTexture(new Rect(x - 14, wy, 10, whiteH), _white);
        }

        int iconSize = 28;
        int ix = x - iconSize - 24;
        int iy = y + totalH / 2 - iconSize / 2;
        GUI.Box(new Rect(ix, iy, iconSize, iconSize), "☀");

        string status = light == null ? "нет InvestitureLight" : "свет " + fill.ToString("0.0");
        GUI.Label(new Rect(x - 140, y - 22, 140, 20), status);

        if (light == null) return;

        if (light.IsAbsorbing)
            GUI.Label(new Rect(x - 100, y - 40, 90, 20), "впитываю");
        if (light.IsUltActive)
            GUI.Label(new Rect(x - 110, y + totalH + 4, 140, 20),
                "ульта " + light.UltTimeLeft.ToString("0.0") + "с");
        else if (light.IsEmpowered)
            GUI.Label(new Rect(x - 110, y + totalH + 4, 140, 20),
                "белый " + light.Absorbed.ToString("0.0") + " ур" + light.AbsorbLevel);
    }

    Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
}
