using UnityEngine;

/// <summary>
/// Зона тела. Порядок в каноне ран: сначала зона, потом пробитие.
/// </summary>
public enum BodyZone
{
    Torso = 0,
    Head = 1,
    LeftArm = 2,
    RightArm = 3,
    LeftLeg = 4,
    RightLeg = 5
}

/// <summary>
/// 5 ступеней пробития: не пробил … на вылет.
/// </summary>
public enum PenetrationResult
{
    None = 0,      // не пробил
    Glance = 1,    // скользнуло
    Shallow = 2,   // неглубоко
    Deep = 3,      // глубоко
    Through = 4    // на вылет
}

/// <summary>
/// Ступень травмы из цифры урона.
/// </summary>
public enum WoundStage
{
    Scratch = 0,   // Царапина
    Wound = 1,     // Рана
    Serious = 2,   // Серьёзная
    Critical = 3   // Критическая
}

/// <summary>
/// Намерение удара от шагов (heavy). Лёгкие бьют Neutral.
/// </summary>
public enum HitIntent
{
    Neutral = 0,
    ThrustLine = 1, // вперёд — укол / торс–голова
    Bypass = 2,     // вбок — обход, ↑ range
    Limb = 3        // назад — конечности
}

/// <summary>
/// Тип кинетики удара. Пока влияет только на отброс.
/// Оглушение / нокаут — следующая ступень той же шкалы.
/// </summary>
public enum DamageKind
{
    Slash = 0,
    Pierce = 1,
    Blunt = 2
}

/// <summary>
/// Контекст одного попадания. Собирает CombatController, тащит WeaponHitbox, принимает WoundTracker.
/// </summary>
public struct HitInfo
{
    public float rawDamage;          // до пробития
    public float finalDamage;        // после пробития (пишет WoundTracker / TakeHit)
    public float stagger;
    public Vector3 sourcePosition;
    public Vector3 hitDirection;

    public BodyZone zone;
    public PenetrationResult penetration;
    public WoundStage stage;

    public HitIntent intent;
    public DamageKind kind;
    public bool isHeavy;
    public bool isInfight;           // локоть / рукоять / плечо / таран щитом
    public CombatRange band;
    public bool stepBoost;           // два согласованных шага
    public float chargePercent;
    public float penetrationScore;   // сырой скор до сравнения с сопротивлением
    public float weaponPenetration;  // с WeaponData

    public static HitInfo Basic(float damage, Vector3 source)
    {
        return new HitInfo
        {
            rawDamage = damage,
            finalDamage = damage,
            sourcePosition = source,
            zone = BodyZone.Torso,
            penetration = PenetrationResult.Shallow,
            stage = WoundStage.Wound,
            intent = HitIntent.Neutral,
            kind = DamageKind.Slash
        };
    }

    /// <summary>Множитель отброса. Колющий почти не двигает, дробящий толкает сильнее.</summary>
    public static float KnockbackOf(DamageKind kind)
    {
        switch (kind)
        {
            case DamageKind.Pierce: return 0.18f;
            case DamageKind.Blunt: return 1.55f;
            default: return 0.72f;
        }
    }

    public float KnockbackImpulse => stagger * KnockbackOf(kind);
}
