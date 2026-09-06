using UnityEngine;

/// <summary>
/// Столб света на клетках MapGrid. Тикает урон по врагам, чья клетка в наборе.
/// </summary>
public class LightPillar : MonoBehaviour
{
    public float duration = 4f;
    public float tick = 0.35f;
    public float damagePerTick = 6f;
    public float height = 8f;

    MapGrid _grid;
    Vector2Int[] _cells;
    float _end;
    float _nextTick;
    Light _light;
    static Mesh _quad;

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
        p._end = Time.time + duration;
        p._nextTick = Time.time;
        p.BuildLight();
        return p;
    }

    void BuildLight()
    {
        var lamp = new GameObject("Lamp");
        lamp.transform.SetParent(transform, false);
        lamp.transform.localPosition = new Vector3(0f, height * 0.45f, 0f);
        _light = lamp.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.color = new Color(1f, 0.95f, 0.7f);
        _light.intensity = 4.5f;
        _light.range = 14f;
        _light.shadows = LightShadows.Soft;
    }

    void Update()
    {
        if (Time.time >= _end)
        {
            Destroy(gameObject);
            return;
        }
        if (Time.time >= _nextTick)
        {
            _nextTick = Time.time + tick;
            Pulse();
        }
    }

    void Pulse()
    {
        if (_grid == null || _cells == null) return;
        var hits = Physics.OverlapSphere(transform.position, 12f);
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

    void OnRenderObject()
    {
        if (_grid == null || _cells == null) return;
        if (_quad == null) _quad = BuildQuad();
        var mat = SpellController.CellMarkMaterial;
        if (mat == null) return;
        mat.SetPass(0);
        float ts = _grid.TileSize;
        float live = Mathf.Clamp01((_end - Time.time) / Mathf.Max(0.01f, duration));
        for (int i = 0; i < _cells.Length; i++)
        {
            Vector3 p = _grid.CellCenterWorld(_cells[i].x, _cells[i].y);
            p.y += 0.06f;
            Graphics.DrawMeshNow(_quad, Matrix4x4.TRS(p, Quaternion.Euler(90f, 0f, 0f), new Vector3(ts * 0.92f, ts * 0.92f, 1f)));
        }
        Vector3 col = transform.position;
        var cube = Matrix4x4.TRS(
            col + Vector3.up * (height * 0.5f),
            Quaternion.identity,
            new Vector3(0.35f, height, 0.35f));
        Graphics.DrawMeshNow(_quad, cube);
        _ = live;
    }

    static Mesh BuildQuad()
    {
        var m = new Mesh();
        m.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f)
        };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        m.RecalculateBounds();
        return m;
    }
}
