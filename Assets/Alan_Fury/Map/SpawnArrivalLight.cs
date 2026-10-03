using UnityEngine;

/// <summary>
/// Столб света с неба на точке появления.
/// Форма — цилиндр (столб). Появляется МГНОВЕННО, вспыхивает, держится и гаснет.
/// </summary>
public class SpawnArrivalLight : MonoBehaviour
{
    [Header("Запуск")]
    public bool playOnStart = true;

    [Header("Столб")]
    public float height = 28f;
    public float radius = 2.6f;

    [Header("Цвет")]
    [ColorUsage(true, true)]
    public Color color = new Color(1.6f, 1.55f, 1.4f, 1f);
    [ColorUsage(true, true)]
    public Color coreColor = new Color(2.6f, 2.5f, 2.3f, 1f);

    [Header("Свет")]
    public float intensity = 120f;
    public float lightRange = 32f;
    public float flashBoost = 6f;      // вспышка в момент появления
    public float flashDecay = 12f;     // как быстро гаснет вспышка
    public LightShadows shadows = LightShadows.None;

    [Header("Тайминг")]
    [Tooltip("Луч бьёт через столько секунд после старта.")]
    public float beamDelay = 0.5f;
    [Tooltip("Персонаж появляется через столько секунд после луча.")]
    public float revealAfterBeam = 0.5f;
    public float arriveTime = 0f;
    public float holdTime = 3.5f;
    public float fadeTime = 4f;

    [Header("Персонаж")]
    [Tooltip("Кого спрятать до луча и показать до гашения. Пусто — только свет.")]
    public Transform arriveActor;

    [Header("Halo (ударная волна на земле)")]
    public float haloSize = 7f;
    public float ringLife = 0.7f;

    [Header("Частицы")]
    public bool spawnBurst = true;
    public int burstCount = 28;

    public bool IsPlaying { get; private set; }

    Transform _root;
    Transform _column;
    Transform _halo;
    Light _light;
    Material _columnMat;
    Material _haloMat;
    ParticleSystem _burst;
    float _born;
    float _ringStart;
    float _revealAt;
    bool _revealed;
    bool _beamed;
    Renderer[] _hidden;
    CharacterController _hiddenCc;
    bool _ccWasEnabled;

    public static SpawnArrivalLight PlayAt(Vector3 worldPos)
    {
        var existing = FindObjectOfType<SpawnArrivalLight>();
        if (existing != null) { existing.Play(worldPos); return existing; }

        var go = new GameObject("SpawnArrivalLight");
        go.transform.position = worldPos;
        var light = go.AddComponent<SpawnArrivalLight>();
        light.playOnStart = false;
        light.Play(worldPos);
        return light;
    }

    void Start()
    {
        if (playOnStart) Play(transform.position);
    }

    void Awake()
    {
        if (playOnStart && arriveActor != null)
            HideActor();
    }

    public void Play() => Play(transform.position);

    public void Arm(Transform actor)
    {
        arriveActor = actor;
        _revealed = false;
        HideActor();
    }

    public void Play(Vector3 worldPos)
    {
        transform.position = worldPos;
        EnsureBuilt();
        _root.position = worldPos;
        _root.gameObject.SetActive(true);
        _born = Time.time;
        _ringStart = -1f;
        _revealed = false;
        _beamed = false;
        _revealAt = beamDelay + revealAfterBeam;
        IsPlaying = true;

        HideActor();
        ApplyVisual(0f, 0f, 0f);
    }

    void Update()
    {
        if (!IsPlaying) return;

        float age = Time.time - _born;
        float beamAge = age - beamDelay;
        if (beamAge < 0f)
        {
            ApplyVisual(0f, 0f, 0f);
            return;
        }
        if (!_beamed)
        {
            _beamed = true;
            if (spawnBurst && _burst != null)
            {
                var main = _burst.main;
                main.startColor = color;
                _burst.Clear();
                _burst.Emit(burstCount);
            }
        }
        if (beamAge >= arriveTime + holdTime + fadeTime) { Finish(); return; }

        float alpha, drop;
        if (beamAge < arriveTime)
        {
            float t = Mathf.Clamp01(beamAge / Mathf.Max(0.01f, arriveTime));
            drop = t;
            alpha = t;
        }
        else if (beamAge < arriveTime + holdTime)
        {
            drop = 1f; alpha = 1f;
        }
        else
        {
            float t = (beamAge - arriveTime - holdTime) / Mathf.Max(0.01f, fadeTime);
            drop = 1f; alpha = 1f - t * t;
        }

        if (!_revealed && age >= _revealAt)
            RevealActor();

        ApplyVisual(beamAge, drop, alpha);
    }

    void Finish()
    {
        IsPlaying = false;
        RevealActor();
        ApplyVisual(0f, 1f, 0f);
        if (_root != null)
            _root.gameObject.SetActive(false);
    }

    void HideActor()
    {
        _hidden = null;
        _hiddenCc = null;
        if (arriveActor == null) return;
        _hidden = arriveActor.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < _hidden.Length; i++)
        {
            if (_hidden[i] == null) continue;
            if (_root != null && _hidden[i].transform.IsChildOf(_root)) continue;
            _hidden[i].enabled = false;
        }
        _hiddenCc = arriveActor.GetComponent<CharacterController>();
        if (_hiddenCc != null)
        {
            _ccWasEnabled = _hiddenCc.enabled;
            _hiddenCc.enabled = false;
        }
    }

    void RevealActor()
    {
        if (_revealed) return;
        _revealed = true;
        _ringStart = Time.time - _born;
        if (_hidden != null)
        {
            for (int i = 0; i < _hidden.Length; i++)
                if (_hidden[i] != null) _hidden[i].enabled = true;
        }
        if (_hiddenCc != null)
            _hiddenCc.enabled = _ccWasEnabled;
        _hidden = null;
        _hiddenCc = null;
        if (arriveActor != null)
            ArrivalMaterialize.Begin(arriveActor, color);
    }

    void ApplyVisual(float age, float drop, float alpha)
    {
        alpha = Mathf.Clamp01(alpha);
        bool on = alpha > 0.02f;
        if (_column != null) _column.gameObject.SetActive(on);
        if (_halo != null) _halo.gameObject.SetActive(on);

        // --- Столб ---
        // Unity cylinder: height 2, radius 0.5, центр в середине.
        // Ставим так, чтобы низ был в точке появления, верх — на height.
        float h = height * drop;
        if (_column != null)
        {
            _column.localScale = new Vector3(radius * 2f, h * 0.5f, radius * 2f);
            _column.localPosition = new Vector3(0f, h * 0.5f, 0f);
        }

        // --- Halo (расширяющееся кольцо) ---
        if (_halo != null)
        {
            float ringT = _ringStart < 0f ? 0f : Mathf.Clamp01((age - _ringStart) / Mathf.Max(0.05f, ringLife));
            float s = haloSize * (0.25f + ringT * 1.4f) * drop;
            _halo.localScale = new Vector3(s, s, 1f);
            Color pool = color; pool.a = (1f - ringT) * alpha * 0.9f;
            Colorize(_haloMat, pool);
        }

        // --- Цвета столба ---
        Color beamC = color; beamC.a = alpha;
        Color coreC = coreColor; coreC.a = alpha;
        if (_columnMat != null)
        {
            if (_columnMat.HasProperty("_BaseColor")) _columnMat.SetColor("_BaseColor", beamC);
            if (_columnMat.HasProperty("_CoreColor")) _columnMat.SetColor("_CoreColor", coreC);
            if (_columnMat.HasProperty("_Color")) _columnMat.SetColor("_Color", beamC);
        }

        // --- Свет с вспышкой ---
        if (_light != null)
        {
            float flash = 1f + flashBoost * Mathf.Exp(-age * flashDecay);
            _light.enabled = on;
            _light.intensity = intensity * alpha * flash;
            _light.color = color;
            _light.range = lightRange;
            _light.transform.localPosition = new Vector3(0f, Mathf.Max(1.2f, h * 0.45f), 0f);
        }
    }

    void EnsureBuilt()
    {
        if (_root != null) return;

        var shaft = Shader.Find("VFX/ArrivalShaft");
        _columnMat = MakeMat(shaft, color, coreColor);
        _haloMat = MakeMat(null, color, color);

        var rootGo = new GameObject("ArrivalVfx");
        rootGo.transform.SetParent(transform, false);
        _root = rootGo.transform;

        var col = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Object.Destroy(col.GetComponent<Collider>());
        col.name = "Column";
        col.transform.SetParent(_root, false);
        _column = col.transform;
        var r = col.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.sharedMaterial = _columnMat;

        _halo = MakeQuad("Pool", _root, new Vector3(0f, 0.05f, 0f), Quaternion.Euler(90f, 0f, 0f),
            new Vector3(haloSize, haloSize, 1f), _haloMat);

        var lamp = new GameObject("Fill");
        lamp.transform.SetParent(_root, false);
        _light = lamp.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.range = lightRange;
        _light.shadows = shadows;
        _light.color = color;
        _light.intensity = 0f;

        if (spawnBurst) _burst = MakeBurst(_root, color);
    }

    ParticleSystem MakeBurst(Transform parent, Color c)
    {
        var go = new GameObject("Burst");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 0.3f, 0f);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.duration = 1f;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.22f);
        main.startColor = c;
        main.gravityModifier = -0.15f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 128;

        var emission = ps.emission;
        emission.enabled = false;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 35f;
        shape.radius = 0.4f;
        shape.rotation = new Vector3(-90f, 0f, 0f);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
        );
        col.color = new ParticleSystem.MinMaxGradient(grad);

        var rend = ps.GetComponent<ParticleSystemRenderer>();
        var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var mat = new Material(sh);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = 3000;
        rend.sharedMaterial = mat;
        rend.renderMode = ParticleSystemRenderMode.Billboard;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;

        return ps;
    }

    static Transform MakeQuad(string name, Transform parent, Vector3 localPos, Quaternion localRot, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.Destroy(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.sharedMaterial = mat;
        return go.transform;
    }

    static Texture2D MakePoolTex()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = x / (float)(n - 1) * 2f - 1f;
                float v = y / (float)(n - 1) * 2f - 1f;
                float d = Mathf.Sqrt(u * u + v * v);
                float ring = Mathf.Exp(-Mathf.Pow((d - 0.6f) * 3.5f, 2f));
                float fade = Mathf.Clamp01(1f - d);
                px[y * n + x] = new Color(1f, 1f, 1f, ring * fade);
            }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return tex;
    }

    static Material MakeMat(Shader prefer, Color baseC, Color coreC)
    {
        Shader sh = prefer;
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseC);
        if (m.HasProperty("_CoreColor")) m.SetColor("_CoreColor", coreC);
        if (m.HasProperty("_Color")) m.SetColor("_Color", baseC);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = 3000;
        return m;
    }

    static void Colorize(Material m, Color c)
    {
        if (m == null) return;
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.96f, 0.86f, 0.5f);
        Vector3 baseP = transform.position;
        Vector3 top = baseP + Vector3.up * height;
        Gizmos.DrawLine(baseP, top);
        Gizmos.DrawWireSphere(baseP, radius);
        Gizmos.DrawWireSphere(top, radius);
        Gizmos.DrawLine(baseP + Vector3.right * radius, top + Vector3.right * radius);
        Gizmos.DrawLine(baseP - Vector3.right * radius, top - Vector3.right * radius);
        Gizmos.DrawLine(baseP + Vector3.forward * radius, top + Vector3.forward * radius);
        Gizmos.DrawLine(baseP - Vector3.forward * radius, top - Vector3.forward * radius);
    }
#endif
}