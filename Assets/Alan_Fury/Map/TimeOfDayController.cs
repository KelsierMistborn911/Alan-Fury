using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Переключатель времени суток. Вешать на пустой объект в сцене
/// (рядом с Directional Light / Global Volume).
///
/// Двигает: солнце, ambient, туман, фон камеры, Color Adjustments в Volume.
/// Природа (Nature/VisionFade) unlit — солнце её не красит.
/// Если указан NaturePlacement, тинт идёт в MaterialPropertyBlock варианта (_Color).
/// Shared-материалы не пишет.
///
/// Клавиши по умолчанию: 6 день, 7 закат, 8 сумерки, 9 луна, 0 ночь, - цикл.
/// </summary>
[ExecuteAlways]
public class TimeOfDayController : MonoBehaviour
{
    public enum Period
    {
        Noon = 0,
        Sunset = 1,
        Dusk = 2,
        FullMoon = 3,
        Night = 4
    }

    [System.Serializable]
    public class Preset
    {
        public string name = "Preset";

        [Header("Солнце / луна")]
        public Vector3 sunEuler = new Vector3(50f, 45f, 0f);
        public Color sunColor = Color.white;
        [Min(0f)] public float sunIntensity = 1f;
        [Range(0f, 1f)] public float shadowStrength = 0.65f;

        [Header("Ambient")]
        public Color ambientSky = new Color(0.55f, 0.65f, 0.75f);
        public Color ambientEquator = new Color(0.45f, 0.48f, 0.42f);
        public Color ambientGround = new Color(0.22f, 0.20f, 0.16f);
        [Range(0f, 2f)] public float ambientIntensity = 1f;

        [Header("Туман / фон")]
        public bool fog = true;
        public Color fogColor = new Color(0.55f, 0.62f, 0.68f);
        [Range(0f, 0.08f)] public float fogDensity = 0.008f;
        public Color cameraBackground = new Color(0.45f, 0.58f, 0.70f);

        [Header("Volume Color Adjustments")]
        public float postExposure = 0f;
        [Range(-100f, 100f)] public float contrast = 0f;
        [Range(-100f, 100f)] public float saturation = 0f;
        public Color colorFilter = Color.white;

        [Header("Природа (unlit _Color)")]
        public Color natureTint = Color.white;
    }

    [Header("Ссылки")]
    public Light sun;
    public Camera targetCamera;
    public Volume globalVolume;
    public NaturePlacement nature;
    public NatureRenderer natureRenderer;

    [Header("Текущее")]
    public Period period = Period.Noon;
    [Tooltip("0 = мгновенно.")]
    public float blendTime = 0.8f;
    public bool applyInEditMode = true;

    [Header("Клавиши")]
    public bool useHotkeys = true;
    public KeyCode keyNoon = KeyCode.Alpha6;
    public KeyCode keySunset = KeyCode.Alpha7;
    public KeyCode keyDusk = KeyCode.Alpha8;
    public KeyCode keyMoon = KeyCode.Alpha9;
    public KeyCode keyNight = KeyCode.Alpha0;
    public KeyCode keyCycle = KeyCode.Minus;

    [Header("Пресеты")]
    public Preset noon = new Preset
    {
        name = "Noon",
        sunEuler = new Vector3(48f, 45f, 0f),
        sunColor = new Color(1f, 0.96f, 0.88f),
        sunIntensity = 1.05f,
        shadowStrength = 0.62f,
        ambientSky = new Color(0.55f, 0.66f, 0.78f),
        ambientEquator = new Color(0.48f, 0.50f, 0.42f),
        ambientGround = new Color(0.22f, 0.20f, 0.16f),
        ambientIntensity = 0.95f,
        fog = true,
        fogColor = new Color(0.62f, 0.70f, 0.74f),
        fogDensity = 0.0045f,
        cameraBackground = new Color(0.48f, 0.62f, 0.74f),
        postExposure = 0f,
        contrast = 4f,
        saturation = 4f,
        colorFilter = Color.white,
        natureTint = Color.white
    };

    public Preset sunset = new Preset
    {
        name = "Sunset",
        sunEuler = new Vector3(12f, 40f, 0f),
        sunColor = new Color(1.00f, 0.42f, 0.18f),
        sunIntensity = 0.58f,
        shadowStrength = 0.62f,
        ambientSky = new Color(0.35f, 0.22f, 0.28f),
        ambientEquator = new Color(0.42f, 0.26f, 0.20f),
        ambientGround = new Color(0.12f, 0.08f, 0.06f),
        ambientIntensity = 0.42f,
        fog = true,
        fogColor = new Color(0.28f, 0.16f, 0.10f),
        fogDensity = 0.012f,
        cameraBackground = new Color(0.38f, 0.18f, 0.12f),
        postExposure = -0.55f,
        contrast = 12f,
        saturation = 6f,
        colorFilter = new Color(1f, 0.82f, 0.70f),
        natureTint = new Color(0.72f, 0.52f, 0.38f)
    };

    public Preset dusk = new Preset
    {
        name = "Dusk",
        sunEuler = new Vector3(4f, 38f, 0f),
        sunColor = new Color(0.95f, 0.28f, 0.16f),
        sunIntensity = 0.28f,
        shadowStrength = 0.48f,
        ambientSky = new Color(0.16f, 0.14f, 0.28f),
        ambientEquator = new Color(0.22f, 0.14f, 0.18f),
        ambientGround = new Color(0.06f, 0.05f, 0.06f),
        ambientIntensity = 0.28f,
        fog = true,
        fogColor = new Color(0.14f, 0.10f, 0.16f),
        fogDensity = 0.016f,
        cameraBackground = new Color(0.16f, 0.10f, 0.18f),
        postExposure = -0.95f,
        contrast = 10f,
        saturation = -4f,
        colorFilter = new Color(0.88f, 0.72f, 0.82f),
        natureTint = new Color(0.42f, 0.34f, 0.40f)
    };

    public Preset fullMoon = new Preset
    {
        name = "FullMoon",
        sunEuler = new Vector3(32f, 210f, 0f),
        sunColor = new Color(0.55f, 0.68f, 1.00f),
        sunIntensity = 0.20f,
        shadowStrength = 0.42f,
        ambientSky = new Color(0.08f, 0.12f, 0.22f),
        ambientEquator = new Color(0.07f, 0.09f, 0.14f),
        ambientGround = new Color(0.03f, 0.04f, 0.06f),
        ambientIntensity = 0.22f,
        fog = true,
        fogColor = new Color(0.06f, 0.08f, 0.12f),
        fogDensity = 0.010f,
        cameraBackground = new Color(0.04f, 0.06f, 0.10f),
        postExposure = -1.25f,
        contrast = 8f,
        saturation = -16f,
        colorFilter = new Color(0.75f, 0.82f, 1f),
        natureTint = new Color(0.34f, 0.40f, 0.52f)
    };

    public Preset night = new Preset
    {
        name = "Night",
        sunEuler = new Vector3(18f, 205f, 0f),
        sunColor = new Color(0.35f, 0.42f, 0.70f),
        sunIntensity = 0.08f,
        shadowStrength = 0.28f,
        ambientSky = new Color(0.04f, 0.05f, 0.10f),
        ambientEquator = new Color(0.03f, 0.04f, 0.07f),
        ambientGround = new Color(0.015f, 0.016f, 0.025f),
        ambientIntensity = 0.12f,
        fog = true,
        fogColor = new Color(0.03f, 0.035f, 0.055f),
        fogDensity = 0.018f,
        cameraBackground = new Color(0.02f, 0.025f, 0.04f),
        postExposure = -1.7f,
        contrast = 6f,
        saturation = -22f,
        colorFilter = new Color(0.62f, 0.70f, 0.92f),
        natureTint = new Color(0.20f, 0.24f, 0.34f)
    };

    static readonly int TodTintId = Shader.PropertyToID("_TodTint");
    Period _from;
    Period _to;
    float _blend;
    bool _blending;
    ColorAdjustments _colorAdj;
    readonly Dictionary<NaturePlacement.NatureVariant, Color> _natureBase =
        new Dictionary<NaturePlacement.NatureVariant, Color>();

    public Period Current => _blending ? _to : period;

    void OnEnable()
    {
        ResolveRefs();
        CacheVolume();
        CacheNatureBase();
        _from = _to = period;
        _blend = 1f;
        _blending = false;
        Apply(GetPreset(period), 1f);
    }

    void OnDisable()
    {
        RestoreNatureBase();
    }

    void OnValidate()
    {
        if (!isActiveAndEnabled) return;
        if (!Application.isPlaying && !applyInEditMode) return;
        _from = _to = period;
        _blend = 1f;
        _blending = false;
        ResolveRefs();
        CacheVolume();
        CacheNatureBase();
        Apply(GetPreset(period), 1f);
    }

    void Update()
    {
        if (Application.isPlaying && useHotkeys)
            ReadHotkeys();

        if (!Application.isPlaying && !applyInEditMode)
            return;

        if (_blending)
        {
            float dur = Mathf.Max(0.0001f, blendTime);
            _blend = Mathf.MoveTowards(_blend, 1f, Time.deltaTime / dur);
            Apply(LerpPreset(GetPreset(_from), GetPreset(_to), _blend), 1f);
            if (_blend >= 1f)
            {
                _blending = false;
                period = _to;
            }
        }
    }

    public void SetPeriod(Period next, bool instant = false)
    {
        if (next == Current && !_blending && !instant)
            return;

        ResolveRefs();
        CacheVolume();
        CacheNatureBase();

        if (instant || blendTime <= 0f || !Application.isPlaying)
        {
            period = next;
            _from = _to = next;
            _blend = 1f;
            _blending = false;
            Apply(GetPreset(next), 1f);
            return;
        }

        _from = Current;
        _to = next;
        _blend = 0f;
        _blending = true;
        period = next;
    }

    public void Cycle(int dir = 1)
    {
        int n = System.Enum.GetValues(typeof(Period)).Length;
        int i = ((int)Current + dir) % n;
        if (i < 0) i += n;
        SetPeriod((Period)i);
    }

    void ReadHotkeys()
    {
        if (Input.GetKeyDown(keyNoon)) SetPeriod(Period.Noon);
        else if (Input.GetKeyDown(keySunset)) SetPeriod(Period.Sunset);
        else if (Input.GetKeyDown(keyDusk)) SetPeriod(Period.Dusk);
        else if (Input.GetKeyDown(keyMoon)) SetPeriod(Period.FullMoon);
        else if (Input.GetKeyDown(keyNight)) SetPeriod(Period.Night);
        else if (Input.GetKeyDown(keyCycle)) Cycle(1);
    }

    void ResolveRefs()
    {
        if (sun == null)
        {
            var lights = FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && lights[i].type == LightType.Directional)
                {
                    sun = lights[i];
                    break;
                }
            }
        }

        if (targetCamera == null)
            targetCamera = Camera.main;

        if (globalVolume == null)
        {
            var volumes = FindObjectsOfType<Volume>();
            for (int i = 0; i < volumes.Length; i++)
            {
                if (volumes[i] != null && volumes[i].isGlobal)
                {
                    globalVolume = volumes[i];
                    break;
                }
            }
        }

        if (nature == null)
            nature = FindObjectOfType<NaturePlacement>();
        if (natureRenderer == null)
            natureRenderer = NatureRenderer.Active != null
                ? NatureRenderer.Active
                : FindObjectOfType<NatureRenderer>();
    }

    void CacheVolume()
    {
        _colorAdj = null;
        if (globalVolume == null || globalVolume.profile == null) return;
        globalVolume.profile.TryGet(out _colorAdj);
    }

    void CacheNatureBase()
    {
        if (nature == null || nature.allVariants == null) return;
        for (int i = 0; i < nature.allVariants.Count; i++)
        {
            var v = nature.allVariants[i];
            if (v == null || _natureBase.ContainsKey(v)) continue;
            Color c = Color.white;
            if (v.material != null && v.material.HasProperty("_Color"))
                c = v.material.GetColor("_Color");
            else if (v.parts != null)
            {
                for (int p = 0; p < v.parts.Count; p++)
                {
                    var part = v.parts[p];
                    if (part != null && part.material != null && part.material.HasProperty("_Color"))
                    {
                        c = part.material.GetColor("_Color");
                        break;
                    }
                }
            }
            _natureBase[v] = c;
        }
    }

    void RestoreNatureBase()
    {
        if (nature == null || nature.allVariants == null) return;
        for (int i = 0; i < nature.allVariants.Count; i++)
        {
            var v = nature.allVariants[i];
            if (v == null) continue;
            Color c;
            if (!_natureBase.TryGetValue(v, out c)) c = Color.white;
            ApplyNatureColor(v, c);
        }
    }

    public Preset GetPreset(Period p)
    {
        switch (p)
        {
            case Period.Sunset: return sunset;
            case Period.Dusk: return dusk;
            case Period.FullMoon: return fullMoon;
            case Period.Night: return night;
            default: return noon;
        }
    }

    static Preset LerpPreset(Preset a, Preset b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Preset
        {
            name = t < 0.5f ? a.name : b.name,
            sunEuler = Vector3.Lerp(a.sunEuler, b.sunEuler, t),
            sunColor = Color.Lerp(a.sunColor, b.sunColor, t),
            sunIntensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t),
            shadowStrength = Mathf.Lerp(a.shadowStrength, b.shadowStrength, t),
            ambientSky = Color.Lerp(a.ambientSky, b.ambientSky, t),
            ambientEquator = Color.Lerp(a.ambientEquator, b.ambientEquator, t),
            ambientGround = Color.Lerp(a.ambientGround, b.ambientGround, t),
            ambientIntensity = Mathf.Lerp(a.ambientIntensity, b.ambientIntensity, t),
            fog = t < 0.5f ? a.fog : b.fog,
            fogColor = Color.Lerp(a.fogColor, b.fogColor, t),
            fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t),
            cameraBackground = Color.Lerp(a.cameraBackground, b.cameraBackground, t),
            postExposure = Mathf.Lerp(a.postExposure, b.postExposure, t),
            contrast = Mathf.Lerp(a.contrast, b.contrast, t),
            saturation = Mathf.Lerp(a.saturation, b.saturation, t),
            colorFilter = Color.Lerp(a.colorFilter, b.colorFilter, t),
            natureTint = Color.Lerp(a.natureTint, b.natureTint, t)
        };
    }

    void Apply(Preset p, float w)
    {
        if (p == null) return;

        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(p.sunEuler);
            sun.color = p.sunColor;
            sun.intensity = p.sunIntensity;
            sun.shadowStrength = p.shadowStrength;
            RenderSettings.sun = sun;
        }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = p.ambientSky * p.ambientIntensity;
        RenderSettings.ambientEquatorColor = p.ambientEquator * p.ambientIntensity;
        RenderSettings.ambientGroundColor = p.ambientGround * p.ambientIntensity;

        RenderSettings.fog = p.fog;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = p.fogColor;
        RenderSettings.fogDensity = p.fogDensity;

        if (targetCamera != null)
        {
            targetCamera.backgroundColor = p.cameraBackground;
            targetCamera.clearFlags = CameraClearFlags.SolidColor;
        }

        if (_colorAdj != null)
        {
            _colorAdj.active = true;
            _colorAdj.postExposure.overrideState = true;
            _colorAdj.contrast.overrideState = true;
            _colorAdj.saturation.overrideState = true;
            _colorAdj.colorFilter.overrideState = true;
            _colorAdj.postExposure.value = p.postExposure;
            _colorAdj.contrast.value = p.contrast;
            _colorAdj.saturation.value = p.saturation;
            _colorAdj.colorFilter.value = p.colorFilter;
        }

        ApplyNature(p.natureTint);
    }

    void ApplyNature(Color tint)
    {
        Shader.SetGlobalColor(TodTintId, tint);
        if (natureRenderer == null)
            natureRenderer = NatureRenderer.Active != null
                ? NatureRenderer.Active
                : FindObjectOfType<NatureRenderer>();
        if (natureRenderer != null)
            natureRenderer.todTint = tint;

        if (nature == null || nature.allVariants == null) return;
        for (int i = 0; i < nature.allVariants.Count; i++)
        {
            var v = nature.allVariants[i];
            if (v == null) continue;
            Color baseCol;
            if (!_natureBase.TryGetValue(v, out baseCol)) baseCol = Color.white;
            ApplyNatureColor(v, baseCol * tint);
        }
    }

    static void ApplyNatureColor(NaturePlacement.NatureVariant v, Color c)
    {
        if (v.propertyBlock == null)
            v.propertyBlock = new MaterialPropertyBlock();
        v.propertyBlock.SetColor("_Color", c);
    }
}
