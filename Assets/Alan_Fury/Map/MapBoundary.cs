using UnityEngine;

/// <summary>
/// Мягкая граница карты: чем ближе игрок к краю, тем сильнее «вязнет»
/// его движение наружу (вязкость), а у самого края — жёсткий стоп,
/// который нельзя проскочить даже перекатом.
///
/// Вешается на тот же GameObject, что и PlayerMovement3D.
/// PlayerMovement3D пропускает все горизонтальные перемещения через Constrain().
/// </summary>
public class MapBoundary : MonoBehaviour
{
    [Header("Источники размеров")]
    public HeightMapGenerator heightSource;
    public ChunkedTerrainBuilder chunkedBuilder;     // приоритетный источник tileSize
    public SeamlessTerrainBuilder seamlessBuilder;   // запасной

    [Header("Поведение")]
    [Tooltip("Ширина приграничной полосы (м), где нарастает вязкость.")]
    public float bandWidth = 9f;
    [Tooltip("Минимальный зазор до самого края карты (м).")]
    public float edgePadding = 1f;
    [Tooltip("Профиль вязкости: 0 у края → 1 вне полосы. EaseInOut = плавно.")]
    public AnimationCurve falloff = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Ручное переопределение (если нет heightSource)")]
    public bool useManualBounds = false;
    public float manualHalfWidth = 100f;
    public float manualHalfDepth = 100f;

    private float halfW, halfD;
    private bool ready;

    public bool IsReady => ready;
    public float HalfWidth => halfW;
    public float HalfDepth => halfD;

    void Start() => Recompute();

    /// <summary>Пересчитать границы. Вызывать после (ре)генерации карты.</summary>
    public void Recompute()
    {
        if (useManualBounds)
        {
            halfW = manualHalfWidth;
            halfD = manualHalfDepth;
            ready = true;
            return;
        }

        if (heightSource == null)
        {
            ready = false;
            return;
        }

        float ts = GetTileSize();
        halfW = heightSource.width * ts / 2f;
        halfD = heightSource.depth * ts / 2f;
        ready = true;
    }

    /// <summary>
    /// Корректирует горизонтальную дельту перемещения (XZ).
    /// Y не трогаем — гравитация работает как обычно.
    /// </summary>
    public Vector3 Constrain(Vector3 currentPos, Vector3 delta)
    {
        if (!ready) return delta;
        delta.x = DampAxis(currentPos.x, delta.x, halfW);
        delta.z = DampAxis(currentPos.z, delta.z, halfD);
        return delta;
    }

    private float DampAxis(float pos, float d, float half)
    {
        float limit = half - edgePadding;

        // Если уже за пределами — мягко вернуть внутрь, наружу не пускать.
        if (pos > limit) return Mathf.Min(d, limit - pos);
        if (pos < -limit) return Mathf.Max(d, -limit - pos);

        bool outward = (pos > 0f && d > 0f) || (pos < 0f && d < 0f);
        if (!outward) return d; // движение внутрь — без сопротивления

        float distToEdge = limit - Mathf.Abs(pos);             // >= 0 здесь
        float t = Mathf.Clamp01(distToEdge / Mathf.Max(0.001f, bandWidth));
        float visc = Mathf.Clamp01(falloff.Evaluate(t));       // 0 у края, 1 вне полосы
        d *= visc;

        // Жёсткий стоп: не пересекать limit ни при каких условиях (в т.ч. перекатом).
        if (Mathf.Abs(d) > distToEdge)
            d = Mathf.Sign(d) * distToEdge;

        return d;
    }

    private float GetTileSize()
    {
        if (chunkedBuilder != null) return chunkedBuilder.TileSize;
        if (seamlessBuilder != null) return seamlessBuilder.tileSize;
        return 4f;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) Recompute();
        if (!ready) return;

        // Внешний контур карты
        Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(halfW * 2f, 2f, halfD * 2f));

        // Внутренняя граница полосы вязкости
        float iw = (halfW - bandWidth) * 2f;
        float id = (halfD - bandWidth) * 2f;
        if (iw > 0f && id > 0f)
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.6f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(iw, 2f, id));
        }
    }
#endif
}
