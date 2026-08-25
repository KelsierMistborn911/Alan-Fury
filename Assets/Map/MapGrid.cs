using UnityEngine;
using System;

/// <summary>
/// Единая сетка занятости карты. Один источник правды для placement / pathfinding.
///
/// Уровни:
///   base   — клетка террейна (tileSize)
///   sector — sectorSize × sectorSize base (по умолчанию 10)
///   region — regionSize × regionSize base (по умолчанию 100)
///
/// Build() после HeightMap. Occupy/Clear при генерации объектов.
/// Pathfinder / ObjectPlacer читают IsBlocked / CanPlace.
/// </summary>
public class MapGrid : MonoBehaviour
{
    [Flags]
    public enum OccupancyFlags : byte
    {
        None = 0,
        Tree = 1 << 0,
        Road = 1 << 1,
    }

    [Header("Источники")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;

    [Header("Уровни сетки")]
    [Tooltip("Sector = N×N base-клеток.")]
    public int sectorSize = 10;
    [Tooltip("Region = N×N base-клеток.")]
    public int regionSize = 100;

    [Header("Блокировка")]
    [Tooltip("Какие флаги считаются стеной для IsBlocked / CanPlace.")]
    public OccupancyFlags blockMask = OccupancyFlags.Tree;

    [Header("Гизмо")]
    public bool drawGizmos = true;
    public bool drawRoads = true;
    public bool drawSectors = false;
    public bool drawRegions = false;
    public Color treeLabelColor = new Color(0.2f, 0.85f, 0.25f, 1f);
    public Color roadFillColor = new Color(0.45f, 0.35f, 0.2f, 0.35f);
    public Color sectorColor = new Color(0.3f, 0.6f, 1f, 0.15f);
    public Color regionColor = new Color(1f, 0.5f, 0.1f, 0.1f);
    public float gizmoYOffset = 0.08f;

    // --- runtime ---
    public bool IsReady => _ready;
    public int Width => _w;
    public int Depth => _d;
    public float TileSize => _ts;
    public Vector3 Origin => _origin;

    private int _w, _d;
    private float _ts;
    private Vector3 _origin;
    private bool _ready;

    private OccupancyFlags[] _flags; // base, length = w*d
    private float[] _cost;           // base cost (1 = normal)

    // sector / region aggregates: has any blocked cell
    private bool[] _sectorBlocked;
    private bool[] _regionBlocked;
    private int _sw, _sd; // sector counts
    private int _rw, _rd; // region counts

    // ============ Lifecycle ============

    /// <summary>Создаёт пустую сетку по размеру HeightMap. Звать после Generate высот.</summary>
    public void Build()
    {
        if (heightSource == null) heightSource = GetComponent<HeightMapGenerator>();
        if (chunkedBuilder == null) chunkedBuilder = GetComponent<ChunkedTerrainBuilder>();

        if (heightSource == null || !heightSource.isGenerated)
        {
            Debug.LogError("MapGrid: HeightMap не готов!");
            _ready = false;
            return;
        }

        _w = heightSource.width;
        _d = heightSource.depth;
        _ts = ResolveTileSize();
        _origin = new Vector3(-_w * _ts / 2f, 0f, -_d * _ts / 2f);

        int n = _w * _d;
        _flags = new OccupancyFlags[n];
        _cost = new float[n];
        for (int i = 0; i < n; i++)
            _cost[i] = 1f;

        int ss = Mathf.Max(1, sectorSize);
        int rs = Mathf.Max(1, regionSize);
        _sw = (_w + ss - 1) / ss;
        _sd = (_d + ss - 1) / ss;
        _rw = (_w + rs - 1) / rs;
        _rd = (_d + rs - 1) / rs;
        _sectorBlocked = new bool[_sw * _sd];
        _regionBlocked = new bool[_rw * _rd];

        _ready = true;
        Debug.Log($"MapGrid: {_w}×{_d} base, sector {ss}→{_sw}×{_sd}, region {rs}→{_rw}×{_rd}, tileSize={_ts}");
    }

    public void Clear()
    {
        _flags = null;
        _cost = null;
        _sectorBlocked = null;
        _regionBlocked = null;
        _ready = false;
    }

    // ============ Write ============

    /// <summary>Можно ли поставить объект sizeX×sizeZ с якорем (cx,cz) = юго-западный угол (или центр — см. anchorCenter).</summary>
    public bool CanPlace(int cx, int cz, int sizeX, int sizeZ, bool anchorCenter = true)
    {
        if (!_ready) return false;
        GetBounds(cx, cz, sizeX, sizeZ, anchorCenter, out int x0, out int z0, out int x1, out int z1);
        if (x0 < 0 || z0 < 0 || x1 >= _w || z1 >= _d) return false;

        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                if (IsBlocked(x, z)) return false;
        return true;
    }

    /// <summary>Пометить клетки флагами. size 3×3 дерево → все 9 клеток.</summary>
    public void Occupy(int cx, int cz, int sizeX, int sizeZ, OccupancyFlags flags, bool anchorCenter = true)
    {
        if (!_ready || flags == OccupancyFlags.None) return;
        GetBounds(cx, cz, sizeX, sizeZ, anchorCenter, out int x0, out int z0, out int x1, out int z1);

        for (int x = Mathf.Max(0, x0); x <= Mathf.Min(_w - 1, x1); x++)
        {
            for (int z = Mathf.Max(0, z0); z <= Mathf.Min(_d - 1, z1); z++)
            {
                int i = Idx(x, z);
                _flags[i] |= flags;
                if (((byte)(_flags[i] & blockMask)) != 0)
                    MarkAggregatesBlocked(x, z);
            }
        }
    }

    /// <summary>Снять флаги с клеток (по умолчанию все).</summary>
    public void ClearOccupancy(int cx, int cz, int sizeX, int sizeZ,
        OccupancyFlags flags = (OccupancyFlags)0xFF, bool anchorCenter = true)
    {
        if (!_ready) return;
        GetBounds(cx, cz, sizeX, sizeZ, anchorCenter, out int x0, out int z0, out int x1, out int z1);

        for (int x = Mathf.Max(0, x0); x <= Mathf.Min(_w - 1, x1); x++)
        {
            for (int z = Mathf.Max(0, z0); z <= Mathf.Min(_d - 1, z1); z++)
            {
                int i = Idx(x, z);
                _flags[i] &= ~flags;
            }
        }
        // Простой пересчёт затронутых sector/region
        RebuildAggregatesInBounds(
            Mathf.Max(0, x0), Mathf.Max(0, z0),
            Mathf.Min(_w - 1, x1), Mathf.Min(_d - 1, z1));
    }

    public void SetCost(int cx, int cz, float cost)
    {
        if (!_ready || cx < 0 || cz < 0 || cx >= _w || cz >= _d) return;
        _cost[Idx(cx, cz)] = Mathf.Max(0.01f, cost);
    }

    // ============ Read ============

    public bool IsBlocked(int cx, int cz)
    {
        if (!_ready || cx < 0 || cz < 0 || cx >= _w || cz >= _d) return true;
        return ((byte)(_flags[Idx(cx, cz)] & blockMask)) != 0;
    }

    public float GetCost(int cx, int cz)
    {
        if (!_ready || cx < 0 || cz < 0 || cx >= _w || cz >= _d) return 1f;
        return _cost[Idx(cx, cz)];
    }

    public OccupancyFlags GetFlags(int cx, int cz)
    {
        if (!_ready || cx < 0 || cz < 0 || cx >= _w || cz >= _d) return OccupancyFlags.None;
        return _flags[Idx(cx, cz)];
    }

    public bool HasFlag(int cx, int cz, OccupancyFlags flag)
        => (GetFlags(cx, cz) & flag) != 0;

    public bool SectorHasBlocked(int sx, int sz)
    {
        if (!_ready || sx < 0 || sz < 0 || sx >= _sw || sz >= _sd) return true;
        return _sectorBlocked[sx * _sd + sz];
    }

    public bool RegionHasBlocked(int rx, int rz)
    {
        if (!_ready || rx < 0 || rz < 0 || rx >= _rw || rz >= _rd) return true;
        return _regionBlocked[rx * _rd + rz];
    }

    // ============ Coords ============

    public void WorldToCell(Vector3 world, out int cx, out int cz)
    {
        if (!_ready) { cx = cz = 0; return; }
        cx = Mathf.Clamp(Mathf.FloorToInt((world.x - _origin.x) / _ts + 0.5f), 0, _w - 1);
        cz = Mathf.Clamp(Mathf.FloorToInt((world.z - _origin.z) / _ts + 0.5f), 0, _d - 1);
    }

    /// <summary>Мировая точка клетки (как Pathfinder: origin + index * tileSize, y = высота).</summary>
    public Vector3 CellToWorld(int cx, int cz)
    {
        float h = (heightSource != null && heightSource.isGenerated)
            ? heightSource.GetHeight(Mathf.Clamp(cx, 0, _w - 1), Mathf.Clamp(cz, 0, _d - 1))
            : 0f;
        return new Vector3(_origin.x + cx * _ts, h, _origin.z + cz * _ts);
    }

    public Vector3 CellCenterWorld(int cx, int cz)
    {
        Vector3 p = CellToWorld(cx, cz);
        // Pathfinder/ObjectPlacer якорь = SW-угол по формуле origin+index*ts;
        // визуальный центр — сдвиг на полклетки.
        p.x += _ts * 0.5f;
        p.z += _ts * 0.5f;
        return p;
    }

    // ============ Internals ============

    private int Idx(int x, int z) => x * _d + z;

    private float ResolveTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.tileSize;
        return 4f;
    }

    private void GetBounds(int cx, int cz, int sizeX, int sizeZ, bool anchorCenter,
        out int x0, out int z0, out int x1, out int z1)
    {
        sizeX = Mathf.Max(1, sizeX);
        sizeZ = Mathf.Max(1, sizeZ);
        if (anchorCenter)
        {
            int hx = sizeX / 2;
            int hz = sizeZ / 2;
            x0 = cx - hx;
            z0 = cz - hz;
            x1 = x0 + sizeX - 1;
            z1 = z0 + sizeZ - 1;
        }
        else
        {
            x0 = cx;
            z0 = cz;
            x1 = cx + sizeX - 1;
            z1 = cz + sizeZ - 1;
        }
    }

    private void MarkAggregatesBlocked(int x, int z)
    {
        int ss = Mathf.Max(1, sectorSize);
        int rs = Mathf.Max(1, regionSize);
        int sx = x / ss, sz = z / ss;
        int rx = x / rs, rz = z / rs;
        if (sx >= 0 && sz >= 0 && sx < _sw && sz < _sd)
            _sectorBlocked[sx * _sd + sz] = true;
        if (rx >= 0 && rz >= 0 && rx < _rw && rz < _rd)
            _regionBlocked[rx * _rd + rz] = true;
    }

    private void RebuildAggregatesInBounds(int x0, int z0, int x1, int z1)
    {
        int ss = Mathf.Max(1, sectorSize);
        int rs = Mathf.Max(1, regionSize);
        int sx0 = x0 / ss, sz0 = z0 / ss, sx1 = x1 / ss, sz1 = z1 / ss;
        int rx0 = x0 / rs, rz0 = z0 / rs, rx1 = x1 / rs, rz1 = z1 / rs;

        for (int sx = sx0; sx <= sx1; sx++)
            for (int sz = sz0; sz <= sz1; sz++)
            {
                if (sx < 0 || sz < 0 || sx >= _sw || sz >= _sd) continue;
                bool blocked = false;
                int bx0 = sx * ss, bz0 = sz * ss;
                int bx1 = Mathf.Min(_w, bx0 + ss), bz1 = Mathf.Min(_d, bz0 + ss);
                for (int x = bx0; x < bx1 && !blocked; x++)
                    for (int z = bz0; z < bz1 && !blocked; z++)
                        if (IsBlocked(x, z)) blocked = true;
                _sectorBlocked[sx * _sd + sz] = blocked;
            }

        for (int rx = rx0; rx <= rx1; rx++)
            for (int rz = rz0; rz <= rz1; rz++)
            {
                if (rx < 0 || rz < 0 || rx >= _rw || rz >= _rd) continue;
                bool blocked = false;
                int bx0 = rx * rs, bz0 = rz * rs;
                int bx1 = Mathf.Min(_w, bx0 + rs), bz1 = Mathf.Min(_d, bz0 + rs);
                for (int x = bx0; x < bx1 && !blocked; x++)
                    for (int z = bz0; z < bz1 && !blocked; z++)
                        if (IsBlocked(x, z)) blocked = true;
                _regionBlocked[rx * _rd + rz] = blocked;
            }
    }

    // ============ Gizmos ============

    void OnDrawGizmos()
    {
        if (!drawGizmos || !_ready || _flags == null) return;
        DrawOccupancyGizmos();
        if (drawSectors) DrawSectorFrames();
        if (drawRegions) DrawRegionFrames();
    }

    void OnDrawGizmosSelected()
    {
        if (!_ready || _flags == null) return;
        if (!drawGizmos) DrawOccupancyGizmos(); // always show when selected
        if (!drawSectors) { /* skip */ }
    }

    private void DrawOccupancyGizmos()
    {
        float half = _ts * 0.5f;

        for (int x = 0; x < _w; x++)
        {
            for (int z = 0; z < _d; z++)
            {
                var f = _flags[Idx(x, z)];
                if (f == OccupancyFlags.None) continue;

                Vector3 center = CellCenterWorld(x, z);
                center.y += gizmoYOffset;

                if (drawRoads && (f & OccupancyFlags.Road) != 0)
                {
                    Gizmos.color = roadFillColor;
                    Gizmos.DrawCube(center, new Vector3(_ts * 0.95f, 0.02f, _ts * 0.95f));
                }

                if ((f & OccupancyFlags.Tree) != 0)
                {
#if UNITY_EDITOR
                    UnityEditor.Handles.color = treeLabelColor;
                    UnityEditor.Handles.Label(center + Vector3.up * 0.15f, "T");
#endif
                }
            }
        }
    }

    private void DrawSectorFrames()
    {
        int ss = Mathf.Max(1, sectorSize);
        Gizmos.color = sectorColor;
        for (int sx = 0; sx < _sw; sx++)
        {
            for (int sz = 0; sz < _sd; sz++)
            {
                float x0 = _origin.x + sx * ss * _ts;
                float z0 = _origin.z + sz * ss * _ts;
                float x1 = _origin.x + Mathf.Min(_w, (sx + 1) * ss) * _ts;
                float z1 = _origin.z + Mathf.Min(_d, (sz + 1) * ss) * _ts;
                float y = gizmoYOffset;
                Vector3 a = new Vector3(x0, y, z0);
                Vector3 b = new Vector3(x1, y, z0);
                Vector3 c = new Vector3(x1, y, z1);
                Vector3 d = new Vector3(x0, y, z1);
                Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c);
                Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
            }
        }
    }

    private void DrawRegionFrames()
    {
        int rs = Mathf.Max(1, regionSize);
        Gizmos.color = regionColor;
        for (int rx = 0; rx < _rw; rx++)
        {
            for (int rz = 0; rz < _rd; rz++)
            {
                float x0 = _origin.x + rx * rs * _ts;
                float z0 = _origin.z + rz * rs * _ts;
                float x1 = _origin.x + Mathf.Min(_w, (rx + 1) * rs) * _ts;
                float z1 = _origin.z + Mathf.Min(_d, (rz + 1) * rs) * _ts;
                float y = gizmoYOffset;
                Vector3 a = new Vector3(x0, y, z0);
                Vector3 b = new Vector3(x1, y, z0);
                Vector3 c = new Vector3(x1, y, z1);
                Vector3 d = new Vector3(x0, y, z1);
                Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c);
                Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
            }
        }
    }
}
