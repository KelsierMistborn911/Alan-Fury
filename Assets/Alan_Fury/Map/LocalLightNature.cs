using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Круг света на unlit-природе. Вешать на объект с Point Light.
/// Радиус и цвет берутся с Light, тут только множители.
/// Деревья: Nature/VisionFade. Трава: Nature/GrassField.
/// Пакетный Dynamic Grass FX этот круг не видит.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
public class LocalLightNature : MonoBehaviour
{
    [Tooltip("Красить кроны и траву этим светом.")]
    public bool affectNature = true;

    [Tooltip("0 = Light.range.")]
    public float rangeOverride;

    [Tooltip("Множитель к Light.color * Light.intensity.")]
    public float natureStrength = 1.4f;

    [Tooltip("Насколько быстро гаснет к краю. 1 = линейно, выше = жёстче центр.")]
    [Range(0.5f, 4f)] public float falloff = 1.6f;

    [Tooltip("Иначе natureColor, не цвет Light.")]
    public bool useLightColor = true;
    public Color natureColor = new Color(1f, 0.9f, 0.7f, 1f);

    static readonly List<LocalLightNature> All = new List<LocalLightNature>();
    static readonly int PosId = Shader.PropertyToID("_NatureLightPos");
    static readonly int ColId = Shader.PropertyToID("_NatureLightColor");
    static readonly int ParamsId = Shader.PropertyToID("_NatureLightParams");

    Light _light;

    void Awake()
    {
        _light = GetComponent<Light>();
    }

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    void OnDisable()
    {
        All.Remove(this);
        if (All.Count == 0) PushOff();
    }

    void LateUpdate()
    {
        LocalLightNature best = null;
        float bestI = 0f;
        for (int i = 0; i < All.Count; i++)
        {
            var n = All[i];
            if (n == null || !n.isActiveAndEnabled) continue;
            float w = n.Weight();
            if (w > bestI)
            {
                bestI = w;
                best = n;
            }
        }

        if (best == null)
        {
            PushOff();
            return;
        }
        if (best == this)
            best.Push();
    }

    float Weight()
    {
        if (!affectNature || _light == null || !_light.enabled) return 0f;
        if (_light.intensity <= 0.001f) return 0f;
        return _light.intensity * Mathf.Max(0f, natureStrength);
    }

    void Push()
    {
        float range = rangeOverride > 0.01f ? rangeOverride : _light.range;
        Color c = useLightColor ? _light.color : natureColor;
        c *= _light.intensity * Mathf.Max(0f, natureStrength);
        c.a = 1f;
        Shader.SetGlobalVector(PosId, transform.position);
        Shader.SetGlobalColor(ColId, c);
        Shader.SetGlobalVector(ParamsId, new Vector4(Mathf.Max(0.05f, range), falloff, 1f, 0f));
    }

    static void PushOff()
    {
        Shader.SetGlobalVector(ParamsId, Vector4.zero);
        Shader.SetGlobalColor(ColId, Color.black);
    }
}
