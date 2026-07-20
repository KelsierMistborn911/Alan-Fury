using UnityEngine;

/// <summary>
/// Мелкий HUD над оборотнем: 3 полоски (HP, агрессия, страх стаи) с цифрами.
/// Страх стаи общий — берётся из WerewolfPackManager, у всех волков одинаковый.
/// Строится из квадов и TextMesh кодом, по образцу PlayerStatsHUD —
/// на префаб волка вешать только этот скрипт (рядом с WerewolfStats).
/// Полоски всегда повёрнуты к камере; при смерти волка HUD прячется.
/// </summary>
public class WerewolfStatsHUD : MonoBehaviour
{
    [Header("Источник")]
    [Tooltip("Пусто → ищется WerewolfStats на этом объекте.")]
    public WerewolfStats stats;

    [Header("Размещение")]
    [Tooltip("Смещение HUD над волком (м).")]
    public Vector3 offset = new Vector3(0f, 2.4f, 0f);
    [Tooltip("Ширина полоски (м).")]
    public float barWidth = 0.9f;
    [Tooltip("Высота полоски (м).")]
    public float barHeight = 0.07f;
    [Tooltip("Зазор между полосками (м).")]
    public float barSpacing = 0.03f;

    [Header("Цифры")]
    [Tooltip("Размер цифр (characterSize у TextMesh).")]
    public float textSize = 0.04f;

    [Header("Цвета")]
    public Color healthColor = new Color(0.85f, 0.15f, 0.15f);
    public Color aggressionColor = new Color(1f, 0.55f, 0.1f);
    public Color fearColor = new Color(0.65f, 0.7f, 0.9f);
    public Color backColor = new Color(0.08f, 0.08f, 0.08f, 0.7f);

    private Transform _root;
    private Bar[] _bars;

    private struct Bar
    {
        public Transform fill;
        public TextMesh text;
    }

    void Start()
    {
        if (stats == null) stats = GetComponent<WerewolfStats>();
        if (stats != null) stats.OnDeath += HideHud;

        _root = new GameObject("WerewolfStatsHUD").transform;
        _root.SetParent(transform, false);
        _root.localPosition = offset;

        _bars = new Bar[3];
        _bars[0] = BuildBar(0, healthColor);
        _bars[1] = BuildBar(1, aggressionColor);
        _bars[2] = BuildBar(2, fearColor);
    }

    void OnDestroy()
    {
        if (stats != null) stats.OnDeath -= HideHud;
    }

    private void HideHud()
    {
        if (_root != null) _root.gameObject.SetActive(false);
        enabled = false;
    }

    void LateUpdate()
    {
        if (stats == null || _root == null) return;

        UpdateBar(_bars[0], stats.HealthPercent, stats.Health);
        UpdateBar(_bars[1], stats.Aggression01, stats.Aggression);

        var mgr = WerewolfPackManager.Instance;
        UpdateBar(_bars[2], mgr != null ? mgr.PackFearPercent : 0f, mgr != null ? mgr.PackFear : 0f);

        // Лицом к камере
        Camera cam = Camera.main;
        if (cam != null)
            _root.rotation = cam.transform.rotation;
    }

    // index — номер строки сверху вниз (0 = верхняя).
    private Bar BuildBar(int index, Color color)
    {
        float y = -index * (barHeight + barSpacing);

        var row = new GameObject("Bar" + index).transform;
        row.SetParent(_root, false);
        row.localPosition = new Vector3(0f, y, 0f);

        MakeQuad(row, "Back", backColor,
                 new Vector3(0f, 0f, 0.001f), new Vector3(barWidth, barHeight, 1f));
        Transform fill = MakeQuad(row, "Fill", color,
                 Vector3.zero, new Vector3(barWidth, barHeight, 1f));

        var textGo = new GameObject("Value");
        textGo.transform.SetParent(row, false);
        textGo.transform.localPosition = new Vector3(barWidth * 0.5f + 0.05f, 0f, 0f);
        var tm = textGo.AddComponent<TextMesh>();
        tm.anchor = TextAnchor.MiddleLeft;
        tm.alignment = TextAlignment.Left;
        tm.fontSize = 48;
        tm.characterSize = textSize;
        tm.color = color;

        return new Bar { fill = fill, text = tm };
    }

    private Transform MakeQuad(Transform parent, string name, Color color,
                               Vector3 localPos, Vector3 localScale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.SetFloat("_Surface", 1f); // Transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        mat.color = color;
        go.GetComponent<MeshRenderer>().material = mat;

        return go.transform;
    }

    private void UpdateBar(Bar bar, float percent, float value)
    {
        percent = Mathf.Clamp01(percent);
        // Заливка сжимается вправо-налево: пивот квада в центре,
        // поэтому сдвигаем к левому краю на половину «потерянной» ширины.
        Vector3 s = bar.fill.localScale;
        s.x = barWidth * percent;
        bar.fill.localScale = s;
        bar.fill.localPosition = new Vector3(-(barWidth - s.x) * 0.5f, 0f, 0f);

        bar.text.text = Mathf.RoundToInt(value).ToString();
    }
}
