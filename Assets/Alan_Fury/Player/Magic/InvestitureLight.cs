using UnityEngine;

/// <summary>
/// Инвеститурный свет паладина искупления.
/// 7 уровней шкалы. Копится со временем. Может переполняться.
/// Пассив: +10% скорости за уровень.
/// Свечение лица с 1-го уровня, яркость растёт.
/// R удерживать — впитывать свет из шкалы в персонажа.
/// После отпускания следующие действия усилены, пока не потрачен впитанный свет.
/// Если впитать весь свет при >=5 уровнях — ульта на 7с за каждую заполненную ячейку.
/// Во время ульты энергия на действия не тратится.
/// Переименовываем бывший WitchLight.
/// </summary>
public class InvestitureLight : MonoBehaviour
{
    [Header("Шкала")]
    public int levels = 7;
    [Tooltip("Текущее заполнение 0..levels (может > levels при переполнении).")]
    public float currentFill = 0f;
    public float fillPerSecond = 0.08f; // примерно 1 уровень за ~12 сек
    public float overflowMax = 2f;      // сколько сверх 7 можно накопить

    [Header("Впитывание")]
    public KeyCode absorbKey = KeyCode.R;
    [Tooltip("Сколько шкалы уходит в персонажа в секунду, пока держишь R.")]
    public float absorbRate = 1.2f;

    [Header("Ульта")]
    public int ultMinLevels = 5;
    public float ultSecondsPerLevel = 7f;

    [Header("Пассив")]
    [Tooltip("Бонус скорости за каждый полный уровень.")]
    public float speedBonusPerLevel = 0.10f;

    [Header("Свечение лица")]
    public Renderer faceRenderer;
    [ColorUsage(false, true)]
    public Color faceEmission = new Color(1f, 0.92f, 0.7f);
    public float maxFaceIntensity = 3.5f;

    [Header("Точечный свет (опционально, как раньше)")]
    public bool usePointLight = true;
    public Color lightColor = new Color(1f, 0.9f, 0.6f);
    public float lightRange = 8f;

    // Состояние
    public float Absorbed { get; private set; }
    public bool IsAbsorbing { get; private set; }
    public bool IsEmpowered => Absorbed > 0.01f;
    public bool IsUltActive { get; private set; }
    public float UltTimeLeft { get; private set; }

    public int CurrentLevel => Mathf.Clamp(Mathf.FloorToInt(currentFill), 0, levels);
    public float SpeedMultiplier => 1f + CurrentLevel * speedBonusPerLevel;

    public System.Action onChanged;

    private Light _light;
    private Material _faceMat;
    private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    private float _ultTimer;

    void Awake()
    {
        if (faceRenderer == null)
            faceRenderer = GetComponentInChildren<Renderer>(); // fallback, потом назначить лицо

        SetupFace();
        if (usePointLight) CreateLight();
    }

    void Update()
    {
        // Копим, пока не держим R и не в ульте (в ульте тоже можно копить? по описанию да, но пока копим всегда кроме поглощения)
        if (!IsAbsorbing)
        {
            float max = levels + overflowMax;
            if (currentFill < max)
                currentFill = Mathf.Min(max, currentFill + fillPerSecond * Time.deltaTime);
        }

        // Впитывание
        bool wantAbsorb = Input.GetKey(absorbKey);
        if (wantAbsorb && currentFill > 0.01f && !IsUltActive)
        {
            IsAbsorbing = true;
            float take = Mathf.Min(absorbRate * Time.deltaTime, currentFill);
            currentFill -= take;
            Absorbed += take;
        }
        else
        {
            if (IsAbsorbing && Absorbed > 0.01f)
            {
                // Отпустили: проверяем ульту
                if (CurrentLevel >= ultMinLevels && currentFill < 0.05f)
                    StartUlt();
            }
            IsAbsorbing = false;
        }

        // Ульта
        if (IsUltActive)
        {
            _ultTimer -= Time.deltaTime;
            UltTimeLeft = Mathf.Max(0f, _ultTimer);
            if (_ultTimer <= 0f)
                EndUlt();
        }

        UpdateVisual();
        onChanged?.Invoke();
    }

    public void SpendAbsorbed(float amount)
    {
        if (IsUltActive) return; // в ульте не тратим
        Absorbed = Mathf.Max(0f, Absorbed - amount);
    }

    public bool TryBlockWithLight(float damage)
    {
        if (Absorbed <= 0.01f) return false;
        float cost = damage * 0.15f; // условно
        if (Absorbed >= cost)
        {
            SpendAbsorbed(cost);
            return true; // считаем блокированным
        }
        return false;
    }

    private void StartUlt()
    {
        int spentLevels = Mathf.Max(1, CurrentLevel);
        _ultTimer = spentLevels * ultSecondsPerLevel;
        IsUltActive = true;
        UltTimeLeft = _ultTimer;
        // в ульте можно дальше впитывать/тратить, пассивы живы
    }

    private void EndUlt()
    {
        IsUltActive = false;
        UltTimeLeft = 0f;
    }

    private void SetupFace()
    {
        if (faceRenderer == null) return;
        _faceMat = faceRenderer.material;
        _faceMat.EnableKeyword("_EMISSION");
        _faceMat.SetColor(EmissionId, Color.black);
    }

    private void CreateLight()
    {
        var go = new GameObject("InvestitureLight");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        _light = go.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.color = lightColor;
        _light.range = lightRange;
        _light.intensity = 0f;
    }

    private void UpdateVisual()
    {
        float t = Mathf.Clamp01(currentFill / levels);
        float faceK = CurrentLevel > 0 ? Mathf.Lerp(0.25f, 1f, t) : 0f;

        if (_faceMat != null)
            _faceMat.SetColor(EmissionId, faceEmission * (maxFaceIntensity * faceK));

        if (_light != null)
        {
            _light.intensity = faceK * 1.4f;
            _light.enabled = faceK > 0.01f;
        }
    }

    void OnDestroy()
    {
        if (_faceMat != null) Destroy(_faceMat);
    }
}
