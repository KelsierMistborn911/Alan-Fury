using UnityEngine;

/// <summary>
/// Инвеститурный свет паладина искупления.
/// 7 уровней шкалы. Копится со временем. Может переполняться.
/// R удерживать — впитывать свет из шкалы в персонажа (белый слой).
/// Белый свет тратится на усиленные действия (телепорт, удары, блок, лечение).
/// Уровень шкалы на момент впитывания задаёт силу бонусов.
/// Если впитать почти всё при >=5 уровнях — ульта. В ульте действия бесплатны,
/// белый сгорает только по таймеру (1 пункт / 7 с).
/// Пока есть белый — смертельный удар сжигает 1 пункт и не убивает.
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

    [Header("Траты белого (вне ульты)")]
    public float teleportCost = 0.5f;
    public float attackCost = 0.35f;
    public float blockCost = 0.4f;
    public float healCost = 0.6f;

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
    public int AbsorbLevel { get; private set; }   // уровень шкалы, от которого считаются бонусы
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
    private int _peakLevelDuringAbsorb;

    void Awake()
    {
        if (faceRenderer == null)
            faceRenderer = GetComponentInChildren<Renderer>();

        SetupFace();
        if (usePointLight) CreateLight();
    }

    void Update()
    {
        if (!IsAbsorbing)
        {
            float max = levels + overflowMax;
            if (currentFill < max)
                currentFill = Mathf.Min(max, currentFill + fillPerSecond * Time.deltaTime);
        }

        bool wantAbsorb = Input.GetKey(absorbKey);
        if (wantAbsorb && currentFill > 0.01f && !IsUltActive)
        {
            if (!IsAbsorbing)
                _peakLevelDuringAbsorb = CurrentLevel;
            IsAbsorbing = true;
            float take = Mathf.Min(absorbRate * Time.deltaTime, currentFill);
            currentFill -= take;
            Absorbed += take;
            if (CurrentLevel > _peakLevelDuringAbsorb)
                _peakLevelDuringAbsorb = CurrentLevel;
        }
        else
        {
            if (IsAbsorbing && Absorbed > 0.01f)
            {
                AbsorbLevel = Mathf.Max(AbsorbLevel, _peakLevelDuringAbsorb);
                if (AbsorbLevel >= ultMinLevels && currentFill < 0.05f)
                    StartUlt();
            }
            IsAbsorbing = false;
        }

        if (IsUltActive)
        {
            _ultTimer -= Time.deltaTime;
            UltTimeLeft = Mathf.Max(0f, _ultTimer);
            // сгорает 1 пункт за ultSecondsPerLevel
            float burn = Time.deltaTime / ultSecondsPerLevel;
            Absorbed = Mathf.Max(0f, Absorbed - burn);
            if (_ultTimer <= 0f || Absorbed <= 0.01f)
                EndUlt();
        }

        UpdateVisual();
        onChanged?.Invoke();
    }

    /// <summary>Потратить белый свет. В ульте ничего не тратит.</summary>
    public void SpendAbsorbed(float amount)
    {
        if (IsUltActive) return;
        Absorbed = Mathf.Max(0f, Absorbed - amount);
        if (Absorbed <= 0.01f)
            AbsorbLevel = 0;
    }

    public bool TrySpendForTeleport()
    {
        if (!IsEmpowered && !IsUltActive) return false;
        SpendAbsorbed(teleportCost);
        return true;
    }

    public bool TrySpendForAttack()
    {
        if (!IsEmpowered && !IsUltActive) return false;
        SpendAbsorbed(attackCost);
        return true;
    }

    public bool TryBlockWithLight(float damage)
    {
        if (!IsEmpowered && !IsUltActive) return false;
        if (AbsorbLevel < 4 && !IsUltActive) return false;
        SpendAbsorbed(blockCost);
        return true;
    }

    /// <summary>Смертельный удар: сжечь 1 пункт и выжить, если есть белый.</summary>
    public bool TrySurviveLethal()
    {
        if (Absorbed < 1f && !IsUltActive) return false;
        if (!IsUltActive)
            Absorbed = Mathf.Max(0f, Absorbed - 1f);
        if (Absorbed <= 0.01f)
            AbsorbLevel = 0;
        return true;
    }

    public float RangeBonus => AbsorbLevel * 0.15f;
    public float WindupMultiplier => 1f - AbsorbLevel * 0.05f;
    public float TeleportDistanceMult => 1f + AbsorbLevel * 0.08f;
    public bool HasLightTail => AbsorbLevel >= 3 || IsUltActive;

    private void StartUlt()
    {
        int spentLevels = Mathf.Max(1, AbsorbLevel);
        _ultTimer = spentLevels * ultSecondsPerLevel;
        IsUltActive = true;
        UltTimeLeft = _ultTimer;
    }

    private void EndUlt()
    {
        IsUltActive = false;
        UltTimeLeft = 0f;
        Absorbed = 0f;
        AbsorbLevel = 0;
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
        if (Absorbed > 0.01f)
            faceK = Mathf.Max(faceK, 0.6f + AbsorbLevel * 0.05f);

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
