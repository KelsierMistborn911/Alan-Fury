using UnityEngine;

/// <summary>
/// Знак заметности над волком: жёлтый «!» → красный к Notice01=1.
/// Только картинка. Данные из NpcPerception.
/// Как WindupIndicator: квад лицом в камеру, спрайт опционален.
/// </summary>
public class WerewolfNoticeIndicator : MonoBehaviour
{
    [Header("Источник")]
    public NpcPerception perception;

    [Header("Вид")]
    [Tooltip("Опциональный спрайт восклицательного знака. Пусто — цветной квад.")]
    public Sprite sprite;
    public float size = 0.45f;
    public float height = 2.55f;
    public Color yellow = new Color(1f, 0.88f, 0.15f, 0.92f);
    public Color red = new Color(0.95f, 0.12f, 0.1f, 0.95f);
    [Tooltip("Ниже этого Notice01 знак скрыт.")]
    public float showThreshold = 0.02f;

    private GameObject _quad;
    private Material _mat;

    void Start()
    {
        if (perception == null) perception = GetComponent<NpcPerception>();

        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad.name = "NoticeSign";
        Object.Destroy(_quad.GetComponent<Collider>());
        _quad.transform.SetParent(transform, false);
        _quad.transform.localPosition = new Vector3(0f, height, 0f);
        _quad.transform.localScale = new Vector3(size, size, 1f);

        _mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        _mat.SetFloat("_Surface", 1f);
        _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _mat.SetInt("_ZWrite", 0);
        _mat.renderQueue = 3000;
        _mat.color = yellow;
        if (sprite != null) _mat.mainTexture = sprite.texture;
        _quad.GetComponent<MeshRenderer>().material = _mat;
        _quad.SetActive(false);
    }

    void LateUpdate()
    {
        if (perception == null || _quad == null) return;

        float n = perception.Notice01;
        bool show = n > showThreshold;
        if (_quad.activeSelf != show) _quad.SetActive(show);
        if (!show) return;

        if (_mat != null) _mat.color = Color.Lerp(yellow, red, Mathf.Clamp01(n));

        Camera cam = Camera.main;
        if (cam != null)
            _quad.transform.rotation = cam.transform.rotation;
    }
}
