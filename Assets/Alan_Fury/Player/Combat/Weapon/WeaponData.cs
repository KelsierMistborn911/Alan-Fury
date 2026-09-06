using UnityEngine;

[CreateAssetMenu(fileName = "NewWeapon", menuName = "Combat/Weapon Data")]
public class WeaponData : ScriptableObject
{
    public enum WeaponType { Sword, Shield, Spear, Axe, Dagger, Bow, Staff }

    [Header("Основное")]
    public WeaponType type;
    public string weaponName = "Оружие";
    public Sprite icon;

    [Header("Урон")]
    public float damage = 10f;
    public float staggerForce = 5f;
    [Tooltip("Базовое пробитие оружия. Сравнивается с WoundTracker.baseResistance.")]
    public float penetration = 1f;

    [Header("Хитбокс")]
    [Tooltip("База. Боевой контроллер множит на RangeScale (сейчас +50%).")]
    public float attackRange = 2f;
    public float attackRadius = 1f;
    public float attackHeight = 1.5f;
    public Vector3 hitboxOffset = Vector3.forward;

    [Header("Множители поясов (0 = этим поясом не бьёт)")]
    [Tooltip("Grab/clinch ещё нет — 1, иначе в упор урон становился нулём.")]
    public float multPointBlank = 1f;
    public float multClinch = 1f;
    public float multClose = 1f;
    public float multMid = 1f;
    public float multFar = 1f;
    public float multSpecial = 0f;

    public float BandMultiplier(CombatRange band)
    {
        switch (band)
        {
            case CombatRange.PointBlank: return Mathf.Max(0f, multPointBlank);
            case CombatRange.Clinch: return Mathf.Max(0f, multClinch);
            case CombatRange.Close: return Mathf.Max(0f, multClose);
            case CombatRange.Mid: return Mathf.Max(0f, multMid);
            case CombatRange.Far: return Mathf.Max(0f, multFar);
            default: return Mathf.Max(0f, multSpecial);
        }
    }

    public CombatRange FarthestBand
    {
        get
        {
            if (multSpecial > 0f) return CombatRange.Special;
            if (multFar > 0f) return CombatRange.Far;
            if (multMid > 0f) return CombatRange.Mid;
            if (multClose > 0f) return CombatRange.Close;
            if (multClinch > 0f) return CombatRange.Clinch;
            return CombatRange.PointBlank;
        }
    }

    public const float RangeScale = 1.5f;
    public const float WindupScale = 0.7f;

    public float ScaledRange => (attackRange > 0f ? attackRange : 2f) * RangeScale;

    public float Reach(CombatRangeTable table = null)
    {
        var t = table != null ? table : CombatRangeTable.Default;
        float cap = t.Outer(FarthestBand);
        return Mathf.Min(ScaledRange, cap);
    }

    [Header("Заряд / дальний бой")]
    public bool isRanged;
    public bool useCharge;
    public float chargeDuration = 1f;
    public float minChargePercent = 0.3f;
    public float maxHoldTime = 3f;
    public GameObject projectilePrefab;
    public float projectileSpeed = 20f;
    public float projectileLifetime = 3f;
    public int projectilesPerShot = 1;
    public float spreadAngle = 0f;

    [Header("Тайминги")]
    [Tooltip("База. CombatController множит на WindupScale (сейчас −30%).")]
    public float windupDuration = 0.15f;
    public float attackDuration = 0.2f;
    public float cooldownDuration = 0.3f;

    [Header("Тик хитбокса")]
    public float tickInterval = 0.1f;

    [Header("Стамина")]
    public float staminaCost = 15f;

    [Header("Вторичная атака (устарело для меча+щит)")]
    [Tooltip("Есть ли у оружия вторичная функция. У меча+щит укол теперь авто.")]
    public bool hasSecondary = false;
    public string secondaryTrigger = "Thrust";
    public float secondaryDamageMult = 1f;
    public float secondaryRangeMult = 1.3f;
    public float secondaryRadiusMult = 0.35f;
    public float secondaryConeHalfAngle = 15f;
    public float secondaryStaminaMult = 1f;

    [Header("Щит")]
    [Tooltip("Максимальная прочность щита. 0 = бесконечная.")]
    public float maxDurability = 100f;
    [Tooltip("Множитель окна блока (задел под будущую настройку).")]
    public float blockWindowMult = 1.2f;

    [Header("Слои")]
    public LayerMask targetLayers;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (attackRange <= 0f) attackRange = 2f;
        if (attackRadius <= 0f) attackRadius = 1f;
        if (attackHeight <= 0f) attackHeight = 1.5f;
        if (staminaCost < 0f) staminaCost = 0f;
        if (isRanged && projectilePrefab == null)
            Debug.LogWarning($"{weaponName}: нет projectilePrefab для ranged оружия");
    }
#endif
}
