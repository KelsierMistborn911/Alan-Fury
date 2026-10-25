using UnityEngine;

/// <summary>
/// Точка логова. Ставится рандомно вблизи центра Воющей пущи.
/// Позже сюда маркер и префаб. Сейчас только позиция.
/// </summary>
public class WolfDenPoint : MonoBehaviour
{
    [Tooltip("Сдвиг от центра гекса (м).")]
    public float nearMin = 14f;
    public float nearMax = 48f;
    public int seed = 0;

    public Vector3 Position => transform.position;

    void Start()
    {
        PlaceNearHowling();
        var pack = WerewolfPackManager.Instance;
        if (pack != null)
        {
            pack.den = this;
            if (pack.spawnCenter == null) pack.spawnCenter = transform;
        }
    }

    void PlaceNearHowling()
    {
        var stream = FindObjectOfType<LocationStream>();
        Vector3 center = Vector3.zero;
        if (stream != null)
            center = stream.HexToWorld(RegionHexMap.HowlingQ, RegionHexMap.HowlingR);
        else
        {
            // запасной расчёт pointy-top, сторона 200
            float hexSize = 200f;
            int q = RegionHexMap.HowlingQ, r = RegionHexMap.HowlingR;
            center = new Vector3(
                hexSize * (Mathf.Sqrt(3f) * q + Mathf.Sqrt(3f) * 0.5f * r),
                0f,
                hexSize * (1.5f * r));
        }

        var rnd = seed != 0 ? new System.Random(seed) : new System.Random();
        float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
        float dist = Mathf.Lerp(nearMin, nearMax, (float)rnd.NextDouble());
        var pos = center + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
        pos.y = center.y;
        transform.position = pos;
        Debug.Log($"WolfDenPoint: логово у {pos}, центр пущи {center}");
    }
}
