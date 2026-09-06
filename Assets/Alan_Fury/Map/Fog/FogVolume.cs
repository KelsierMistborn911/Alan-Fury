using UnityEngine;

/// <summary>
/// Геймплейный объём тумана — «правда» о тумане для логики (прятки NPC/игрока).
/// Висит на префабе очага вместе с частицами из ассета.
/// Сам регистрируется в FogManager; активность частиц включает/гасит менеджер.
/// </summary>
[DisallowMultipleComponent]
public class FogVolume : MonoBehaviour
{
    [Tooltip("Радиус геймплейного объёма (м). Внутри прячутся NPC.")]
    public float radius = 10f;

    [Tooltip("ParticleSystem(ы) очага. Если пусто — берутся из детей при старте.")]
    public ParticleSystem[] systems;

    /// <summary>Рисуются ли сейчас частицы (управляется FogManager).</summary>
    public bool IsActive { get; private set; } = true;

    void Awake()
    {
        if (systems == null || systems.Length == 0)
            systems = GetComponentsInChildren<ParticleSystem>(true);
    }

    void OnEnable() => FogManager.Register(this);
    void OnDisable() => FogManager.Unregister(this);

    /// <summary>Точка внутри объёма? Проверка плоская — туман стелется низом.</summary>
    public bool Contains(Vector3 worldPos)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        return d.sqrMagnitude <= radius * radius;
    }

    public float FlatSqrDistance(Vector3 worldPos)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        return d.sqrMagnitude;
    }

    /// <summary>Включить/погасить отрисовку. Гашение — мягкое (частицы доживают).</summary>
    public void SetActive(bool on)
    {
        if (on == IsActive) return;
        IsActive = on;
        if (systems == null) return;

        foreach (var ps in systems)
        {
            if (ps == null) continue;
            if (on)
            {
                if (!ps.isPlaying) ps.Play(true);
            }
            else
            {
                // Перестаём эмитить, живые частицы доживают и гаснут — без «попа».
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.7f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
#endif
}
