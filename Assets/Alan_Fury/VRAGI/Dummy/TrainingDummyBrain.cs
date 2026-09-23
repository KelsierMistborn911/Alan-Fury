using UnityEngine;

/// <summary>
/// Манекен: стоит на месте, смотрит на игрока, бьёт цикл
/// SlashLeft → SlashRight → Thrust. Движение и удар — Humanoid*.
/// </summary>
[RequireComponent(typeof(HumanoidLocomotion))]
[RequireComponent(typeof(HumanoidCombat))]
[RequireComponent(typeof(TrainingDummyStats))]
[RequireComponent(typeof(CharacterController))]
public class TrainingDummyBrain : MonoBehaviour
{
    public enum DummyForm { SlashLeft, SlashRight, Thrust }

    [Header("Ссылки")]
    public HumanoidLocomotion locomotion;
    public HumanoidCombat combat;
    public TrainingDummyStats stats;
    public PlayerLoadout loadout;
    public WeaponHitbox hitbox;

    [Header("Дистанция")]
    [Tooltip("Ближе — начинает цикл ударов. Дальше только смотрит.")]
    public float engageRange = 5.2f;
    public float noticeRange = 18f;

    [Header("Цикл")]
    public bool autoAttack = true;
    public float attackCooldown = 1.35f;
    public DummyForm startForm = DummyForm.SlashLeft;

    [Header("Стойка")]
    [Tooltip("Не подшагивать и не уходить в клинч-формы.")]
    public bool plantFeet = true;

    public Transform CurrentTarget { get; private set; }
    public DummyForm NextForm => _next;

    private DummyForm _next;
    private float _readyTime;

    void Awake()
    {
        if (locomotion == null) locomotion = GetComponent<HumanoidLocomotion>();
        if (combat == null) combat = GetComponent<HumanoidCombat>();
        if (stats == null) stats = GetComponent<TrainingDummyStats>();
        if (loadout == null) loadout = GetComponent<PlayerLoadout>();
        if (loadout == null) loadout = gameObject.AddComponent<PlayerLoadout>();
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
        if (hitbox == null) hitbox = gameObject.AddComponent<WeaponHitbox>();

        if (combat != null)
        {
            combat.loadout = loadout;
            combat.hitbox = hitbox;
            combat.lockNamedForms = true;
            if (plantFeet)
            {
                combat.spacingMaxStep = 0f;
                combat.spacingHeavyStep = 0f;
                combat.targetMagnetRange = 0f;
            }
        }

        EnsureWeapon();
        _next = startForm;
    }

    void Start()
    {
        if (stats != null) stats.OnDeath += HandleDeath;
        if (combat != null) combat.DrawAll();
    }

    void OnDestroy()
    {
        if (stats != null) stats.OnDeath -= HandleDeath;
    }

    void HandleDeath()
    {
        CurrentTarget = null;
        if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
        enabled = false;
    }

    void Update()
    {
        if (stats != null && !stats.IsAlive) return;
        var cc = GetComponent<CrowdControl>();
        if (cc != null && cc.IsStunned)
        {
            if (locomotion != null) locomotion.SetMove(Vector3.zero, 1, false);
            return;
        }

        if (locomotion != null)
            locomotion.SetMove(Vector3.zero, 1, false);

        CurrentTarget = Acquire();
        if (CurrentTarget == null)
        {
            if (combat != null) combat.ClearTarget();
            return;
        }

        Vector3 to = CurrentTarget.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        Vector3 face = dist > 0.01f ? to / dist : transform.forward;

        if (locomotion != null) locomotion.SetFace(face);
        if (combat != null)
        {
            combat.CommandTarget = CurrentTarget;
            combat.AimDirection = face;
            if (!combat.IsArmed) combat.DrawAll();
        }

        if (!autoAttack) return;
        if (dist > engageRange) return;
        if (combat == null || combat.IsInAttackPipeline || combat.IsInShock) return;
        if (Time.time < _readyTime) return;

        if (TrySwing(_next))
        {
            _readyTime = Time.time + attackCooldown;
            _next = Advance(_next);
        }
    }

    bool TrySwing(DummyForm form)
    {
        switch (form)
        {
            case DummyForm.SlashLeft:
                return combat.TryLightForm(HumanoidCombat.AttackForm.SlashLeft);
            case DummyForm.SlashRight:
                return combat.TryLightForm(HumanoidCombat.AttackForm.SlashRight);
            default:
                return combat.TryLightForm(HumanoidCombat.AttackForm.Thrust);
        }
    }

    static DummyForm Advance(DummyForm form)
    {
        switch (form)
        {
            case DummyForm.SlashLeft: return DummyForm.SlashRight;
            case DummyForm.SlashRight: return DummyForm.Thrust;
            default: return DummyForm.SlashLeft;
        }
    }

    Transform Acquire()
    {
        if (PlayerRegistry.Instance != null)
        {
            var nearest = PlayerRegistry.Instance.GetNearestFlat(transform.position, noticeRange);
            if (nearest != null) return nearest;
        }
        return PlayerRegistry.ResolvePrimary();
    }

    void EnsureWeapon()
    {
        if (loadout == null) return;
        if (loadout.rightHandWeapon != null) return;

        var w = ScriptableObject.CreateInstance<WeaponData>();
        w.weaponName = "Dummy Sword";
        w.type = WeaponData.WeaponType.Sword;
        w.damage = 10f;
        w.staggerForce = 5f;
        w.penetration = 1f;
        w.attackRange = 2f;
        w.attackRadius = 1f;
        w.attackHeight = 1.5f;
        w.hitboxOffset = Vector3.forward;
        w.windupDuration = 0.18f;
        w.staminaCost = 0f;
        w.minChargePercent = 0.3f;
        loadout.rightHandWeapon = w;
    }
}
