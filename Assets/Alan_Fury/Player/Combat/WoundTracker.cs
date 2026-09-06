using UnityEngine;

/// <summary>
/// Раны v1: зона → пробитие → урон → ступень. Эффект — только кровотечение на зону.
/// Без хромоты и именных ран. Пишет в лог каждую новую/усиленную рану.
/// Вешать на цель рядом с IDamageable (волк / позже игрок).
/// </summary>
public class WoundTracker : MonoBehaviour
{
    [Header("Сопротивление")]
    [Tooltip("Базовое сопротивление пробитию. Выше — чаще Glance/None.")]
    public float baseResistance = 1f;

    [Header("Пороги ступени от finalDamage")]
    public float scratchMax = 3f;
    public float woundMax = 8f;
    public float seriousMax = 15f;

    [Header("Кровотечение (HP/сек по ступени)")]
    public float bleedScratch = 0.05f;
    public float bleedWound = 0.2f;
    public float bleedSerious = 0.45f;
    public float bleedCritical = 0.9f;

    [Header("Лог")]
    public bool logWounds = true;

    // per-zone state
    private readonly WoundStage[] _stage = new WoundStage[6];
    private readonly float[] _bleed = new float[6];
    private float _bleedTick;

    private WerewolfStats _stats;
    private IDamageable _damageable;

    public WoundStage GetStage(BodyZone z) => _stage[(int)z];
    public float GetBleed(BodyZone z) => _bleed[(int)z];

    void Awake()
    {
        _stats = GetComponent<WerewolfStats>();
        _damageable = GetComponent<IDamageable>();
    }

    /// <summary>
    /// Главный вход: считает пробитие и ступень, режет HP, включает bleed, логирует.
    /// </summary>
    public HitInfo ApplyHit(HitInfo hit)
    {
        // --- 1. Зона уже задана атакующим ---
        // --- 2. Пробитие ---
        float resist = Mathf.Max(0.05f, baseResistance);
        float score = hit.penetrationScore;
        if (score <= 0f)
            score = hit.weaponPenetration * (0.6f + hit.chargePercent * 0.8f) * (hit.isHeavy ? 1.25f : 1f);

        if (hit.stepBoost) score *= 1.2f;
        if (hit.intent == HitIntent.ThrustLine) score *= 1.15f;

        float ratio = score / resist;
        hit.penetration = RatioToPenetration(ratio);

        // --- 3. Урон из пробития ---
        float penMult = hit.penetration switch
        {
            PenetrationResult.None => 0.12f,
            PenetrationResult.Glance => 0.35f,
            PenetrationResult.Shallow => 0.7f,
            PenetrationResult.Deep => 1f,
            PenetrationResult.Through => 1.3f,
            _ => 1f
        };
        hit.finalDamage = hit.rawDamage * penMult;

        // --- 4. Ступень из цифры урона ---
        hit.stage = DamageToStage(hit.finalDamage);

        // HP (fear + popup один раз на удар)
        if (_stats != null && _stats.IsAlive)
        {
            _stats.ApplyHealthDamage(hit.finalDamage, hit, reportFear: true, showPopup: true);
        }
        else if (_damageable != null)
        {
            _damageable.TakeDamage(hit.finalDamage, hit.sourcePosition);
        }

        // bleed: берём max по зоне
        int zi = (int)hit.zone;
        float bleedRate = StageBleed(hit.stage);
        if (hit.stage >= _stage[zi] || bleedRate > _bleed[zi])
        {
            _stage[zi] = (WoundStage)Mathf.Max((int)_stage[zi], (int)hit.stage);
            _bleed[zi] = Mathf.Max(_bleed[zi], bleedRate);
        }

        if (logWounds)
        {
            string boost = hit.stepBoost ? " stepBoost" : "";
            string heavy = hit.isHeavy ? " heavy" : " light";
            Debug.Log(
                $"[Wound] {name} zone={hit.zone} pen={hit.penetration} " +
                $"dmg={hit.finalDamage:F1} (raw={hit.rawDamage:F1}) stage={hit.stage} " +
                $"intent={hit.intent}{heavy}{boost} score={score:F2}/{resist:F2}");
        }

        return hit;
    }

    void Update()
    {
        // кровотечение раз в 0.5с суммарно по зонам
        _bleedTick += Time.deltaTime;
        if (_bleedTick < 0.5f) return;
        float step = _bleedTick;
        _bleedTick = 0f;

        float total = 0f;
        for (int i = 0; i < 6; i++)
            total += _bleed[i];

        if (total <= 0f) return;
        float dmg = total * step;
        if (_stats != null && _stats.IsAlive)
            _stats.ApplyHealthDamage(dmg, default, reportFear: false, showPopup: false);
        else if (_damageable != null)
            _damageable.TakeDamage(dmg);
    }

    static PenetrationResult RatioToPenetration(float ratio)
    {
        if (ratio < 0.35f) return PenetrationResult.None;
        if (ratio < 0.7f) return PenetrationResult.Glance;
        if (ratio < 1.1f) return PenetrationResult.Shallow;
        if (ratio < 1.7f) return PenetrationResult.Deep;
        return PenetrationResult.Through;
    }

    WoundStage DamageToStage(float dmg)
    {
        if (dmg <= scratchMax) return WoundStage.Scratch;
        if (dmg <= woundMax) return WoundStage.Wound;
        if (dmg <= seriousMax) return WoundStage.Serious;
        return WoundStage.Critical;
    }

    float StageBleed(WoundStage s) => s switch
    {
        WoundStage.Scratch => bleedScratch,
        WoundStage.Wound => bleedWound,
        WoundStage.Serious => bleedSerious,
        WoundStage.Critical => bleedCritical,
        _ => 0f
    };
}
