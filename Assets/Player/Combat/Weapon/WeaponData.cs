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

    [Header("Хитбокс")]
    public float attackRange = 2f;
    public float attackRadius = 1f;
    public float attackHeight = 1.5f;
    public Vector3 hitboxOffset = Vector3.forward;

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
}
