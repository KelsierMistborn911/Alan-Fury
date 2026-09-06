using UnityEngine;

/// <summary>
/// Заклинание света ("ведьмин огонёк") на клавишу E.
/// Источник света — сама капсула игрока:
///  - emission на её материале (видимое свечение тела),
///  - точечный свет в центре капсулы (освещает окружение; intensity ставь
///    умеренный, чтобы капсула «подсвечивала», а не была прожектором).
/// Тратит ману при касте и (опционально) пока горит. Повторное E — выключить.
/// Гаснет сам, когда мана заканчивается.
///
/// Вешается на объект игрока (рядом с PlayerResources).
/// ВАЖНО (URP): материал капсулы — URP/Lit с включённым Emission,
/// иначе SetColor("_EmissionColor") ничего не даст.
/// </summary>
public class WitchLight : MonoBehaviour
{
    [Header("Ввод")]
    public KeyCode castKey = KeyCode.E;

    [Header("Ресурсы")]
    [Tooltip("Пусто → ищется PlayerResources на этом объекте.")]
    public PlayerResources resources;
    [Tooltip("Стоимость включения (мана). 0 = бесплатно.")]
    public float castCost = 15f;
    [Tooltip("Расход маны в секунду, пока горит. 0 = горит без расхода.")]
    public float drainPerSecond = 3f;

    [Header("Свет (точечный, в центре капсулы)")]
    [Tooltip("Куда крепить свет. Пусто = этот объект.")]
    public Transform attachTo;
    [Tooltip("Центр капсулы. Для стандартной капсулы высотой 2 — (0, 1, 0).")]
    public Vector3 localOffset = new Vector3(0f, 1f, 0f);
    public Color lightColor = new Color(0.7f, 0.85f, 1f);
    [Tooltip("Слабее, чем раньше: капсула подсвечивает, а не заливает всё.")]
    public float intensity = 1.2f;
    public float range = 12f;
    public LightShadows shadows = LightShadows.Soft;

    [Header("Свечение капсулы (emission)")]
    [Tooltip("Renderer капсулы. Пусто → ищется в детях.")]
    public Renderer glowRenderer;
    [ColorUsage(false, true)]
    public Color emissionColor = new Color(0.7f, 0.85f, 1f);
    [Tooltip("Множитель яркости emission на полной мощности.")]
    public float emissionIntensity = 2.5f;

    [Header("Появление / угасание")]
    [Tooltip("Время плавного включения/выключения (сек).")]
    public float fadeTime = 0.25f;

    [Header("Мерцание (опционально)")]
    public bool flicker = true;
    [Range(0f, 1f)] public float flickerAmount = 0.12f;
    public float flickerSpeed = 9f;

    public bool IsOn { get; private set; }

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private Light _light;
    private Material _glowMat;   // инстанс материала капсулы
    private float _current;      // текущая мощность 0..intensity (для плавного fade)
    private float _seed;

    void Awake()
    {
        if (resources == null) resources = GetComponent<PlayerResources>();
        if (attachTo == null) attachTo = transform;
        if (glowRenderer == null) glowRenderer = GetComponentInChildren<Renderer>();
        _seed = Random.value * 100f;

        CreateLight();
        SetupGlowMaterial();
    }

    void Update()
    {
        if (Input.GetKeyDown(castKey))
            Toggle();

        // Расход маны и автогашение при нехватке
        if (IsOn && drainPerSecond > 0f && resources != null)
        {
            if (!resources.SpendMana(drainPerSecond * Time.deltaTime))
                TurnOff();
        }

        UpdateVisual();
    }

    public void Toggle()
    {
        if (IsOn) TurnOff();
        else TurnOn();
    }

    public void TurnOn()
    {
        if (IsOn) return;

        // Каст: списываем стоимость, если маны не хватает — не зажигаем
        if (resources != null && castCost > 0f && !resources.SpendMana(castCost))
            return;

        IsOn = true;
        if (_light != null) _light.enabled = true;
    }

    public void TurnOff() => IsOn = false;

    // =================== Внутреннее ===================

    private void CreateLight()
    {
        var go = new GameObject("WitchLight");
        go.transform.SetParent(attachTo, false);
        go.transform.localPosition = localOffset;

        _light = go.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.color = lightColor;
        _light.range = range;
        _light.shadows = shadows;
        _light.intensity = 0f;
        _light.enabled = false;
    }

    private void SetupGlowMaterial()
    {
        if (glowRenderer == null) return;

        // .material → личный инстанс, чужие объекты с тем же материалом не светятся.
        _glowMat = glowRenderer.material;
        _glowMat.EnableKeyword("_EMISSION");
        _glowMat.SetColor(EmissionColorId, Color.black);
    }

    private void UpdateVisual()
    {
        float target = IsOn ? intensity : 0f;
        float speed = fadeTime > 0f ? intensity / fadeTime : float.MaxValue;
        _current = Mathf.MoveTowards(_current, target, speed * Time.deltaTime);

        // Общий коэффициент 0..1 + мерцание — синхронно для света и emission.
        float k = intensity > 0f ? _current / intensity : 0f;
        if (flicker && IsOn)
        {
            float n = Mathf.PerlinNoise(_seed, Time.time * flickerSpeed) * 2f - 1f;
            k *= 1f + n * flickerAmount;
        }
        k = Mathf.Max(0f, k);

        if (_light != null)
        {
            _light.intensity = intensity * k;

            // Выключаем компонент света, когда полностью погас (экономия)
            if (!IsOn && _current <= 0.001f && _light.enabled)
                _light.enabled = false;
            else if (_current > 0f && !_light.enabled)
                _light.enabled = true;
        }

        if (_glowMat != null)
            _glowMat.SetColor(EmissionColorId, emissionColor * (emissionIntensity * k));
    }

    void OnDestroy()
    {
        if (_glowMat != null) Destroy(_glowMat);
    }
}
