using UnityEngine;

public enum CombatRange
{
    PointBlank = 0, // захват
    Clinch = 1,     // св€зка тел
    Close = 2,      // рука / нож / укус
    Mid = 3,        // обычное оружие / удар волка
    Far = 4,        // длинный клинок / секира
    Special = 5     // пика / кистень
}

/// <summary>
/// ќдна линейка ближнего бо€. ћетры только здесь.
/// јссет не об€зателен Ч есть встроенные значени€.
/// </summary>
[CreateAssetMenu(fileName = "CombatRangeTable", menuName = "Combat/Range Table")]
public class CombatRangeTable : ScriptableObject
{
    [Header("¬нешний край по€са (м)")]
    public float pointBlank = 0.8f;
    public float clinch = 1.35f;
    public float close = 2.1f;
    public float mid = 3.3f;
    public float far = 4.6f;
    public float special = 6.0f;

    static CombatRangeTable _builtin;

    public static CombatRangeTable Default
    {
        get
        {
            if (_builtin == null)
            {
                _builtin = CreateInstance<CombatRangeTable>();
                _builtin.name = "CombatRangeTable (builtin)";
            }
            return _builtin;
        }
    }

    public float Outer(CombatRange band)
    {
        switch (band)
        {
            case CombatRange.PointBlank: return pointBlank;
            case CombatRange.Clinch: return clinch;
            case CombatRange.Close: return close;
            case CombatRange.Mid: return mid;
            case CombatRange.Far: return far;
            default: return special;
        }
    }

    public float Inner(CombatRange band)
    {
        if (band == CombatRange.PointBlank) return 0f;
        return Outer(band - 1);
    }

    public CombatRange Band(float distance)
    {
        float d = Mathf.Max(0f, distance);
        if (d <= pointBlank) return CombatRange.PointBlank;
        if (d <= clinch) return CombatRange.Clinch;
        if (d <= close) return CombatRange.Close;
        if (d <= mid) return CombatRange.Mid;
        if (d <= far) return CombatRange.Far;
        return CombatRange.Special;
    }

    public bool InBand(float distance, CombatRange band)
    {
        return distance > Inner(band) && distance <= Outer(band);
    }
}
