using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Тинт травы и очагов тумана под TimeOfDayController.
/// Сам вешается на объект с контроллером после загрузки сцены.
/// Трава — GrassField (Dynamic Grass FX), не NatureRenderer.
/// Туман — ParticleSystem на FogVolume, не RenderSettings.fog.
/// Погоды и цикла суток нет. Пресет только по кнопке контроллера.
/// </summary>
[DefaultExecutionOrder(50)]
public class TimeOfDayMapTint : MonoBehaviour
{
    public TimeOfDayController clock;
    public Color grassNoon = Color.white;
    public Color grassSunset = new Color(0.78f, 0.48f, 0.28f, 1f);
    public Color grassDusk = new Color(0.42f, 0.30f, 0.34f, 1f);
    public Color grassMoon = new Color(0.32f, 0.40f, 0.55f, 1f);
    public Color grassNight = new Color(0.18f, 0.22f, 0.32f, 1f);

    public Color fogNoon = new Color(0.85f, 0.90f, 0.92f, 1f);
    public Color fogSunset = new Color(1.00f, 0.55f, 0.28f, 1f);
    public Color fogDusk = new Color(0.55f, 0.38f, 0.48f, 1f);
    public Color fogMoon = new Color(0.45f, 0.58f, 0.85f, 1f);
    public Color fogNight = new Color(0.22f, 0.28f, 0.42f, 1f);

    readonly Dictionary<int, Material> _grassMats = new Dictionary<int, Material>();
    readonly Dictionary<int, Color[]> _grassBase = new Dictionary<int, Color[]>();
    readonly Dictionary<int, int[]> _grassIds = new Dictionary<int, int[]>();
    readonly Dictionary<int, ParticleSystem.MinMaxGradient> _fogBase = new Dictionary<int, ParticleSystem.MinMaxGradient>();
    readonly List<MeshRenderer> _grass = new List<MeshRenderer>(8);
    readonly List<ParticleSystem> _fog = new List<ParticleSystem>(32);
    readonly List<ParticleSystemRenderer> _fogR = new List<ParticleSystemRenderer>(32);

    MaterialPropertyBlock _block;
    TimeOfDayController.Period _applied;
    bool _hasApplied;
    bool _grassCached;
    bool _fogCached;

    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int TintColorId = Shader.PropertyToID("_TintColor");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var clocks = FindObjectsOfType<TimeOfDayController>();
        for (int i = 0; i < clocks.Length; i++)
        {
            if (clocks[i] == null) continue;
            if (clocks[i].GetComponent<TimeOfDayMapTint>() == null)
                clocks[i].gameObject.AddComponent<TimeOfDayMapTint>();
        }
    }

    void OnEnable()
    {
        _block = new MaterialPropertyBlock();
        _grassCached = false;
        _fogCached = false;
        _hasApplied = false;
    }

    void LateUpdate()
    {
        if (clock == null) clock = GetComponent<TimeOfDayController>();
        if (clock == null) clock = FindObjectOfType<TimeOfDayController>();
        if (clock == null) return;

        var period = clock.Current;
        bool changed = !_hasApplied || period != _applied;
        if (changed)
        {
            _applied = period;
            _hasApplied = true;
        }

        if (!_fogCached)
            EnsureFog();
        if (changed && _fogCached)
            TintFog(FogTint(period));

        if (!_grassCached)
            EnsureGrass();
        if (changed && _grassCached)
            TintGrassMaterials(GrassTint(period));

        StampGrassBlock(GrassTint(period));
    }

    Color GrassTint(TimeOfDayController.Period p)
    {
        switch (p)
        {
            case TimeOfDayController.Period.Sunset: return grassSunset;
            case TimeOfDayController.Period.Dusk: return grassDusk;
            case TimeOfDayController.Period.FullMoon: return grassMoon;
            case TimeOfDayController.Period.Night: return grassNight;
            default: return grassNoon;
        }
    }

    Color FogTint(TimeOfDayController.Period p)
    {
        switch (p)
        {
            case TimeOfDayController.Period.Sunset: return fogSunset;
            case TimeOfDayController.Period.Dusk: return fogDusk;
            case TimeOfDayController.Period.FullMoon: return fogMoon;
            case TimeOfDayController.Period.Night: return fogNight;
            default: return fogNoon;
        }
    }

    void EnsureGrass()
    {
        if (_grassCached && _grass.Count > 0 && _grass[0] != null) return;
        _grass.Clear();
        var fields = FindObjectsOfType<GrassField>();
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i] == null) continue;
            var rends = fields[i].GetComponentsInChildren<MeshRenderer>(true);
            for (int r = 0; r < rends.Length; r++)
                if (rends[r] != null) _grass.Add(rends[r]);
        }
        _grassCached = _grass.Count > 0;
    }

    void TintGrassMaterials(Color tint)
    {
        for (int i = 0; i < _grass.Count; i++)
        {
            var mr = _grass[i];
            if (mr == null) continue;
            var mat = mr.sharedMaterial;
            if (mat == null) continue;
            Material inst = mat;
            int id = mat.GetInstanceID();
            if (!_grassMats.ContainsKey(id))
            {
                inst = new Material(mat);
                inst.name = mat.name + "_Tod";
                id = inst.GetInstanceID();
                CacheGrassColors(id, inst);
                _grassMats[id] = inst;
                mr.sharedMaterial = inst;
            }
            ApplyGrassColors(id, inst, tint);
        }
    }

    void StampGrassBlock(Color tint)
    {
        if (_block == null) _block = new MaterialPropertyBlock();
        for (int i = 0; i < _grass.Count; i++)
        {
            var mr = _grass[i];
            if (mr == null) continue;
            var mat = mr.sharedMaterial;
            if (mat == null) continue;
            mr.GetPropertyBlock(_block);
            ApplyGrassBlock(mat.GetInstanceID(), _block, tint);
            mr.SetPropertyBlock(_block);
        }
    }

    void CacheGrassColors(int id, Material inst)
    {
        var shader = inst.shader;
        int n = shader != null ? shader.GetPropertyCount() : 0;
        var ids = new List<int>();
        var cols = new List<Color>();
        for (int i = 0; i < n; i++)
        {
            if (shader.GetPropertyType(i) != ShaderPropertyType.Color) continue;
            int pid = shader.GetPropertyNameId(i);
            ids.Add(pid);
            cols.Add(inst.GetColor(pid));
        }
        if (ids.Count == 0 && inst.HasProperty("_Color"))
        {
            ids.Add(ColorId);
            cols.Add(inst.GetColor(ColorId));
        }
        _grassIds[id] = ids.ToArray();
        _grassBase[id] = cols.ToArray();
    }

    void ApplyGrassColors(int id, Material inst, Color tint)
    {
        if (!_grassIds.TryGetValue(id, out var ids)) return;
        if (!_grassBase.TryGetValue(id, out var cols)) return;
        for (int i = 0; i < ids.Length && i < cols.Length; i++)
            inst.SetColor(ids[i], cols[i] * tint);
    }

    void ApplyGrassBlock(int id, MaterialPropertyBlock block, Color tint)
    {
        if (!_grassIds.TryGetValue(id, out var ids)) return;
        if (!_grassBase.TryGetValue(id, out var cols)) return;
        for (int i = 0; i < ids.Length && i < cols.Length; i++)
            block.SetColor(ids[i], cols[i] * tint);
    }

    void EnsureFog()
    {
        if (_fogCached && _fog.Count > 0 && _fog[0] != null) return;
        _fog.Clear();
        _fogR.Clear();
        var volumes = FindObjectsOfType<FogVolume>();
        for (int i = 0; i < volumes.Length; i++)
        {
            var v = volumes[i];
            if (v == null) continue;
            var systems = v.systems;
            if (systems == null || systems.Length == 0)
                systems = v.GetComponentsInChildren<ParticleSystem>(true);
            if (systems == null) continue;
            for (int s = 0; s < systems.Length; s++)
            {
                var ps = systems[s];
                if (ps == null) continue;
                _fog.Add(ps);
                _fogR.Add(ps.GetComponent<ParticleSystemRenderer>());
            }
        }
        _fogCached = _fog.Count > 0;
    }

    void TintFog(Color tint)
    {
        for (int i = 0; i < _fog.Count; i++)
        {
            var ps = _fog[i];
            if (ps == null) continue;
            int id = ps.GetInstanceID();
            var main = ps.main;
            if (!_fogBase.TryGetValue(id, out var baseGrad))
            {
                baseGrad = main.startColor;
                _fogBase[id] = baseGrad;
            }
            main.startColor = Mul(baseGrad, tint);

            var r = i < _fogR.Count ? _fogR[i] : null;
            if (r == null) continue;
            if (_block == null) _block = new MaterialPropertyBlock();
            r.GetPropertyBlock(_block);
            _block.SetColor(ColorId, tint);
            _block.SetColor(TintColorId, tint);
            _block.SetColor(BaseColorId, tint);
            r.SetPropertyBlock(_block);
        }
    }

    static ParticleSystem.MinMaxGradient Mul(ParticleSystem.MinMaxGradient g, Color tint)
    {
        switch (g.mode)
        {
            case ParticleSystemGradientMode.TwoColors:
                g.colorMin = g.colorMin * tint;
                g.colorMax = g.colorMax * tint;
                break;
            case ParticleSystemGradientMode.Gradient:
            case ParticleSystemGradientMode.TwoGradients:
                break;
            default:
                g.color = g.color * tint;
                break;
        }
        return g;
    }
}
