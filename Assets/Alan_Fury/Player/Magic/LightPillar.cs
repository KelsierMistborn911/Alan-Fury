using UnityEngine;

/// <summary>
/// Столб света по клеткам MapGrid. Сначала подсветка, потом падение колонны, затем тики урона.
/// </summary>
public class LightPillar : MonoBehaviour
{
    public float duration = 4f;
    public float tick = 0.35f;
    public float damagePerTick = 6f;
    public float height = 10f;
    public float telegraph = 0.45f;
    public float fallTime = 0.4f;

    MapGrid _grid;
    Vector2Int[] _cells;
    float _born;
    float _end;
    float _nextTick;
    Light _light;
    Transform _beam;
    Transform _halo;
    readonly System.Collections.Generic.List<Transform> _pads = new System.Collections.Generic.List<Transform>();
    static Material _glowMat;
    static Material _beamMat;

    public static LightPillar Spawn(MapGrid grid, Vector2Int[] cells, float duration, float invested)
    {
        var go = new GameObject("LightPillar");
        var p = go.AddComponent<LightPillar>();
        p._grid = grid;
        p._cells = cells;
        p.duration = duration;
        p.damagePerTick = 5f + invested * 0.15f;
        if (grid != null && cells != null && cells.Length > 0)
        {
            Vector3 c = grid.CellCenterWorld(cells[0].x, cells[0].y);
            go.transform.position = c;
        }
        p._born = Time.time;
        p._end = Time.time + p.telegraph + p.fallTime + duration;
        p._nextTick = p._born + p.telegraph + p.fallTime;
        p.Build();
        return p;
    }

    void Build()
    {
        EnsureMats();
        float ts = _grid != null ? _grid.TileSize : 2f;

        if (_grid != null && _cells != null)
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                Vector3 p = _grid.CellCenterWorld(_cells[i].x, _cells[i].y);
                var pad = MakeQuad("Pad", p + Vector3.up * 0.08f, Quaternion.Euler(90f, 0f, 0f),
                    new Vector3(ts * 0.92f, ts * 0.92f, 1f), _glowMat);
                _pads.Add(pad);
            }
        }

        _halo = MakeQuad("Halo", transform.position + Vector3.up * 0.12f, Quaternion.Euler(90f, 0f, 0f),
            new Vector3(ts * 2.2f, ts * 2.2f, 1f), _glowMat);

        var beamGo = new GameObject("Beam");
        beamGo.transform.SetParent(transform, false);
        _beam = beamGo.transform;
        AddBeamQuad(0f);
        AddBeamQuad(90f);

        var lamp = new GameObject("Lamp");
        lamp.transform.SetParent(transform, false);
        lamp.transform.localPosition = new Vector3(0f, height * 0.4f, 0f);
        _light = lamp.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.color = new Color(1f, 0.93f, 0.62f);
        _light.intensity = 0f;
        _light.range = 16f;
        _light.shadows = LightShadows.Soft;
    }

    void AddBeamQuad(float yaw)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.Destroy(q.GetComponent<Collider>());
        q.name = "Ray";
        q.transform.SetParent(_beam, false);
        q.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        q.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
        q.transform.localScale = new Vector3(1.15f, height, 1f);
        var r = q.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.sharedMaterial = _beamMat;
    }

    Transform MakeQuad(string name, Vector3 pos, Quaternion rot, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Object.Destroy(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(transform, true);
        go.transform.position = pos;
        go.transform.rotation = rot;
        go.transform.localScale = scale;
        var r = go.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.sharedMaterial = mat;
        return go.transform;
    }

    void Update()
    {
        if (Time.time >= _end)
        {
            Destroy(gameObject);
            return;
        }

        float age = Time.time - _born;
        float land = telegraph + fallTime;
        float pulse = 0.55f + 0.45f * Mathf.Sin(Time.time * 8f);

        if (age < telegraph)
        {
            float t = age / telegraph;
            SetBeam(Mathf.Lerp(18f, height + 2f, t), 0.08f + t * 0.12f, t * 0.25f);
            if (_light != null) _light.intensity = t * 1.6f;
            ScalePads(0.7f + t * 0.4f * pulse);
        }
        else if (age < land)
        {
            float t = (age - telegraph) / Mathf.Max(0.01f, fallTime);
            float y = Mathf.Lerp(height + 6f, height * 0.5f, t * t);
            float h = Mathf.Lerp(0.4f, height, t);
            SetBeam(y, h, 0.35f + t * 0.65f);
            if (_light != null) _light.intensity = Mathf.Lerp(1.6f, 7.5f, t);
            ScalePads(1.05f + 0.12f * pulse);
        }
        else
        {
            float left = _end - Time.time;
            float fade = left < 0.45f ? left / 0.45f : 1f;
            SetBeam(height * 0.5f, height, fade);
            if (_light != null) _light.intensity = 5.5f * fade * (0.85f + 0.15f * pulse);
            ScalePads((0.95f + 0.08f * pulse) * fade);
            if (Time.time >= _nextTick)
            {
                _nextTick = Time.time + tick;
                Pulse();
            }
        }
    }

    void SetBeam(float localY, float beamHeight, float alpha)
    {
        if (_beam == null) return;
        _beam.gameObject.SetActive(alpha > 0.02f);
        for (int i = 0; i < _beam.childCount; i++)
        {
            var c = _beam.GetChild(i);
            c.localPosition = new Vector3(0f, localY, 0f);
            c.localScale = new Vector3(1.2f, Mathf.Max(0.15f, beamHeight), 1f);
        }
        Colorize(_beamMat, new Color(1f, 0.96f, 0.72f, Mathf.Clamp01(alpha) * 0.55f));
        Colorize(_glowMat, new Color(1f, 0.88f, 0.35f, Mathf.Clamp01(alpha) * 0.7f));
    }

    void ScalePads(float k)
    {
        float ts = _grid != null ? _grid.TileSize : 2f;
        for (int i = 0; i < _pads.Count; i++)
        {
            if (_pads[i] == null) continue;
            _pads[i].localScale = new Vector3(ts * 0.92f * k, ts * 0.92f * k, 1f);
        }
        if (_halo != null)
            _halo.localScale = new Vector3(ts * 2.15f * k, ts * 2.15f * k, 1f);
    }

    void Pulse()
    {
        if (_grid == null || _cells == null) return;
        var hits = Physics.OverlapSphere(transform.position, 14f);
        for (int i = 0; i < hits.Length; i++)
        {
            var dmg = hits[i].GetComponentInParent<IDamageable>();
            if (dmg == null || !dmg.IsAlive) continue;
            if (hits[i].GetComponentInParent<PlayerResources>() != null) continue;
            _grid.WorldToCell(hits[i].transform.position, out int cx, out int cz);
            if (!Contains(cx, cz)) continue;
            float extra = hits[i].GetComponentInParent<GhostStats>() != null ? 1.6f : 1f;
            dmg.TakeDamage(damagePerTick * extra, transform.position);
        }
    }

    bool Contains(int cx, int cz)
    {
        for (int i = 0; i < _cells.Length; i++)
            if (_cells[i].x == cx && _cells[i].y == cz) return true;
        return false;
    }

    static void EnsureMats()
    {
        if (_glowMat == null) _glowMat = MakeMat(new Color(1f, 0.86f, 0.3f, 0.65f));
        if (_beamMat == null) _beamMat = MakeMat(new Color(1f, 0.95f, 0.7f, 0.4f));
    }

    static Material MakeMat(Color c)
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        Colorize(m, c);
        if (m.HasProperty("_Surface"))
        {
            m.SetFloat("_Surface", 1f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            m.SetInt("_ZWrite", 0);
            m.renderQueue = 3000;
        }
        return m;
    }

    static void Colorize(Material m, Color c)
    {
        if (m == null) return;
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }
}
