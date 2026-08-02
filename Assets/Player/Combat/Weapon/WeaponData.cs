using UnityEngine;

[CreateAssetMenu(fileName = "NewWeapon", menuName = "Combat/Weapon Data")]
public class WeaponData : ScriptableObject
{
    public enum WeaponType { Sword, Shield, Spear, Axe, Dagger, Bow, Staff }

    [Header("Тип")]
    public WeaponType type;
    public string weaponName = "Оружие";
    public Sprite icon;

    [Header("Урон")]
    public float damage = 10f;
    public float staggerForce = 5f;

    [Header("Ближний бой")]
    public float attackRange = 2f;
    public float attackRadius = 1f;
    public float attackHeight = 1.5f;
    public Vector3 hitboxOffset = Vector3.forward;

    [Header("Дальний бой")]
    public bool isRanged;
    public bool useCharge;
    public float chargeDuration = 1f;
    public float minChargePercent = 0.3f;
    public float maxHoldTime = 3f;        // Максимальное время удержания замаха
    public GameObject projectilePrefab;
    public float projectileSpeed = 20f;
    public float projectileLifetime = 3f;
    public int projectilesPerShot = 1;
    public float spreadAngle = 0f;

    [Header("Тайминги")]
    public float windupDuration = 0.15f;
    public float attackDuration = 0.2f;
    public float cooldownDuration = 0.3f;

    [Header("Тик урона")]
    public float tickInterval = 0.1f;      // Интервал между тиками урона

    [Header("Ресурсы")]
    public float staminaCost = 15f;


    [Header("Вторичная атака")]
    [Tooltip("Есть ли у оружия вторичная функция. У меча — колющий удар. Клавиша назначается в CombatController3D.")]
    public bool hasSecondary = false;
    [Tooltip("Имя триггера аниматора для вторичной атаки. Пусто — сыграет обычный AttackLeft/AttackRight.")]
    public string secondaryTrigger = "Thrust";
    [Tooltip("Множитель урона относительно damage.")]
    public float secondaryDamageMult = 1f;
    [Tooltip("Множитель дальности относительно attackRange. Работает только без блока: под щитом длина обычная.")]
    public float secondaryRangeMult = 1.3f;
    [Tooltip("Множитель полуширины зоны относительно attackRadius. Меньше — уже укол.")]
    public float secondaryRadiusMult = 0.35f;
    [Tooltip("Полуугол конуса зоны вторичной атаки (град). Меньше — уже веер.")]
    public float secondaryConeHalfAngle = 15f;
    [Tooltip("Множитель стоимости стамины относительно staminaCost.")]
    public float secondaryStaminaMult = 1f;

    [Header("Слои")]
    public LayerMask targetLayers;
}
