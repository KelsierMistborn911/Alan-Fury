using UnityEngine;

/// <summary>
/// Мелкий HUD над головой игрока: 4 полоски (HP, стамина, оборона, мана)
/// с цифрами и кружок блока слева (зелёный = готов, жёлтый = блокирую).
/// Строится из квадов и TextMesh кодом — на сцену вешать только
/// этот скрипт на объект игрока (рядом с PlayerResources).
/// Полоски всегда повёрнуты к камере.
/// </summary>
public class PlayerStatsHUD : MonoBehaviour
{
    [Header("Источник")]
    [Tooltip("Пусто → ищется PlayerResources на этом объекте.")]
    public PlayerResources resources;
    [Tooltip("Пусто → ищется CombatController3D на этом объекте.")]
    public CombatController3D combat;

    [Header("Размещение")]
    [Tooltip("Смещение HUD над игроком (м).")]
    public Vector3 offset = new Vector3(0f, 2.6f, 0f);
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
    public Color staminaColor = new Color(0.9f, 0.8f, 0.2f);
    public Color guardColor = new Color(0.4f, 0.65f, 1f);
    public Color manaColor = new Color(0.6f, 0.3f, 0.9f);
    public Color backColor = new Color(0.08f, 0.08f, 0.08f, 0.7f);

    [Header("Кружок блока")]
    public Color blockReadyColor = new Color(0.25f, 0.85f, 0.3f);
    public Color blockActiveColor = new Color(0.95f, 0.85f, 0.2f);
    [Tooltip("Зазор между кружком и полосками (м).")]
    public float blockCircleGap = 0.05f;

    private Transform _root;
    private Bar[] _bars;
    private MeshRenderer _blockCircle;

    private struct Bar
    {
        public Transform fill;
        public TextMesh text;
        public Color color;
    }

    void Start()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (combat == null) combat = GetComponent<CombatController3D>();

        _root = new GameObject("PlayerStatsHUD").transform;
        _root.SetParent(transform, false);
        _root.localPosition = offset;

        _bars = new Bar[4];
        _bars[0] = BuildBar(0, healthColor);
        _bars[1] = BuildBar(1, staminaColor);
        _bars[2] = BuildBar(2, guardColor);
        _bars[3] = BuildBar(3, manaColor);

        BuildBlockCircle();
    }

    // Кружок слева от полосок, диаметром во всю их суммарную высоту.
    void BuildBlockCircle()
    {
        float diameter = 4 * barHeight + 3 * barSpacing;
        float centerY = -1.5f * (barHeight + barSpacing); // центр стопки полосок

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "BlockCircle";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(_root, false);
        go.transform.localPosition = new Vector3(
            -(barWidth * 0.5f + blockCircleGap + diameter * 0.5f), centerY, 0f);
        go.transform.localScale = new Vector3(diameter, diameter, 1f);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.SetFloat("_Surface", 1f);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        mat.color = blockReadyColor;
        mat.mainTexture = MakeCircleTexture();
        _blockCircle = go.GetComponent<MeshRenderer>();
        _blockCircle.material = mat;
    }

    // Генерирует белый круг на прозрачном фоне (64x64), чтобы квад выглядел кружком.
    static Texture2D MakeCircleTexture()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size * 0.5f - 1f;
        Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                float a = Mathf.Clamp01(r - d); // мягкий край в 1 пиксель
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    void LateUpdate()
    {
        if (resources == null || _root == null) return;

        UpdateBar(_bars[0], resources.HealthPercent, resources.CurrentHealth);
        UpdateBar(_bars[1], resources.StaminaPercent, resources.CurrentStamina);
        UpdateBar(_bars[2], resources.GuardPercent, resources.CurrentGuard);
        UpdateBar(_bars[3], resources.ManaPercent, resources.CurrentMana);

        if (_blockCircle != null && combat != null)
            _blockCircle.material.color = combat.IsBlocking ? blockActiveColor : blockReadyColor;

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

        return new Bar { fill = fill, text = tm, color = color };
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
