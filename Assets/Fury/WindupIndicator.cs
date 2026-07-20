using UnityEngine;

/// <summary>
/// Знак замаха: квад над головой волка, виден во время фазы Windup.
/// Вешается на объект волка (рядом с WerewolfCombat).
/// Всегда повёрнут к камере. Спрайт опционален — без него цветной квад.
/// </summary>
public class WindupIndicator : MonoBehaviour
{
    [Header("Источник")]
    [Tooltip("Пусто → ищется на этом объекте.")]
    public WerewolfCombat combat;

    [Header("Вид")]
    [Tooltip("Опциональный спрайт (например восклицательный знак). Пусто — цветной квад.")]
    public Sprite sprite;
    public Color color = new Color(1f, 0.45f, 0.1f, 0.9f);
    [Tooltip("Размер знака (м).")]
    public float size = 0.5f;
    [Tooltip("Высота над волком (м).")]
    public float height = 2.4f;

    private GameObject _quad;

    void Start()
    {
        if (combat == null) combat = GetComponent<WerewolfCombat>();

        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad.name = "WindupSign";
        Destroy(_quad.GetComponent<Collider>());
        _quad.transform.SetParent(transform, false);
        _quad.transform.localPosition = new Vector3(0f, height, 0f);
        _quad.transform.localScale = new Vector3(size, size, 1f);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.SetFloat("_Surface", 1f); // Transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.renderQueue = 3000;
        mat.color = color;
        if (sprite != null) mat.mainTexture = sprite.texture;
        _quad.GetComponent<MeshRenderer>().material = mat;

        _quad.SetActive(false);
    }

    void LateUpdate()
    {
        if (combat == null || _quad == null) return;

        _quad.SetActive(combat.IsWindingUp);

        if (_quad.activeSelf && Camera.main != null)
            _quad.transform.rotation = Camera.main.transform.rotation;
    }
}
