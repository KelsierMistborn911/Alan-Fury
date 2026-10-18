using UnityEngine;

/// <summary>
/// Устарело. Хаб/дорога А→Б больше не используются.
/// Генерация идёт от гексагональной карты регионов через LocationStream.
/// Файл оставлен, чтобы старые ссылки в сцене не отвалились.
/// </summary>
public interface ICellGround
{
    float Height(int x, int z);
    NaturePlacement.Zone ZoneAt(int x, int z);
    bool IsRoad(int x, int z);
    Vector3 CellOrigin { get; }
}

public enum SiteKind : byte
{
    Hub = 0,
    Road = 1,
}

[System.Serializable]
public struct SitePoint
{
    public string name;
    public int x;
    public int z;

    public static SitePoint At(string name, int x, int z)
    {
        return new SitePoint { name = name, x = x, z = z };
    }
}

/// <summary>
/// Раскладка текущей локации. Не хранит клетки.
/// </summary>
public static class WorldSite
{
    public const int HubCells = 150;

    public static bool InHub(int x, int z)
    {
        return (uint)x < HubCells && (uint)z < HubCells;
    }

    public static bool IsHubExit(int x, int z)
    {
        if (InHub(x, z)) return false;
        int ax = x < 0 ? -1 : (x >= HubCells ? 1 : 0);
        int az = z < 0 ? -1 : (z >= HubCells ? 1 : 0);
        return ax != 0 || az != 0;
    }

    public static bool Near(int x, int z, SitePoint p, int radius)
    {
        int r = Mathf.Max(0, radius);
        int dx = x - p.x;
        int dz = z - p.z;
        return dx * dx + dz * dz <= r * r;
    }

    public static bool OnSegment(int x, int z, SitePoint a, SitePoint b, int width)
    {
        long ax = b.x - a.x;
        long az = b.z - a.z;
        long len2 = ax * ax + az * az;
        int half = Mathf.Max(0, width / 2);
        if (len2 <= 0) return Near(x, z, a, half);
        long vx = x - a.x;
        long vz = z - a.z;
        long dot = vx * ax + vz * az;
        if (dot < 0 || dot > len2) return false;
        long cross = vx * az - vz * ax;
        return cross * cross <= (long)half * half * len2;
    }

    public static NaturePlacement.Zone ForestZone(float noise)
    {
        if (noise > 0.74f) return NaturePlacement.Zone.Thicket;
        if (noise < 0.30f) return NaturePlacement.Zone.Grove;
        return NaturePlacement.Zone.Forest;
    }
}
