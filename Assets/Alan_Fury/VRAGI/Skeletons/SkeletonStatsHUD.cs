using UnityEngine;

/// <summary>
/// HUD над скелетом, как WerewolfStatsHUD: полоски HP / стамина / оборона блока.
/// Источник — PlayerResources на том же объекте (блок уже в ресурсах игрока).
/// Вешать рядом с SkeletonBrain или мозг добавит сам.
/// </summary>
public class SkeletonStatsHUD : MonoBehaviour
{
    [Header("Источник")]
    public PlayerResources resources;

    [Header("Размещение")]
    public Vector3 offset = new Vector3(0f, 2.15f, 0f);
    public float barWidth = 0.9f;
    public float barHeight = 0.07f;
    public float barSpacing = 0.03f;
    public float textSize = 0.04f;

    [Header("Цвета")]
    public Color healthColor = new Color(0.85f, 0.15f, 0.15f);
    public Color staminaColor = new Color(0.9f, 0.8f, 0.2f);
    public Color guardColor = new Color(0.4f, 0.65f, 1f);
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
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (resources != null) resources.onDeath += HideHud;

        _root = new GameObject("SkeletonStatsHUD").transform;
        _root.SetParent(transform, false);
        _root.localPosition = offset;

        _bars = new Bar[3];
        _bars[0] = BuildBar(0, healthColor);
        _bars[1] = BuildBar(1, staminaColor);
        _bars[2] = BuildBar(2, guardColor);
    }

    void OnDestroy()
    {
        if (resources != null) resources.onDeath -= HideHud;
    }

    void HideHud()
    {
        if (_root != null) _root.gameObject.SetActive(false);
        enabled = false;
    }

    void LateUpdate()
    {
        if (resources == null || _root == null || _bars == null) return;

        UpdateBar(_bars[0], resources.HealthPercent, resources.CurrentHealth);
        UpdateBar(_bars[1], resources.StaminaPercent, resources.CurrentStamina);
        UpdateBar(_bars[2], resources.GuardPercent, resources.CurrentGuard);

        Camera cam = Camera.main;
        if (cam != null)
            _root.rotation = cam.transform.rotation;
    }

    Bar BuildBar(int index, Color color)
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

    Transform MakeQuad(Transform parent, string name, Color color,
                       Vector3 localPos, Vector3 localScale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;

        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var mat = new Material(sh);
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
        }
        mat.color = color;
        go.GetComponent<MeshRenderer>().material = mat;
        return go.transform;
    }

    void UpdateBar(Bar bar, float percent, float value)
    {
        if (bar.fill == null) return;
        percent = Mathf.Clamp01(percent);
        Vector3 s = bar.fill.localScale;
        s.x = barWidth * percent;
        bar.fill.localScale = s;
        bar.fill.localPosition = new Vector3(-(barWidth - s.x) * 0.5f, 0f, 0f);
        if (bar.text != null)
            bar.text.text = Mathf.RoundToInt(value).ToString();
    }
}
