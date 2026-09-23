using UnityEngine;

/// <summary>
/// HP манекена. Игрок бьёт как обычного IDamageable.
/// Удары самого манекена — preview: цифра есть, HP цели нет
/// (см. WeaponHitbox.ApplyHit).
/// </summary>
public class TrainingDummyStats : MonoBehaviour, IDamageable
{
    [Header("Здоровье")]
    public float maxHealth = 999f;
    public float healthRegenPerSecond = 6f;

    [Header("Масса / отброс")]
    [Tooltip("Как PlayerResources.mass. Тяжелее — меньше отброс.")]
    public float mass = 90f;

    [Header("Удары манекена")]
    [Tooltip("Попадания манекена не режут HP, только цифра + лёгкий отброс.")]
    public bool previewHits = true;
    [Range(0f, 1.5f)] public float previewKnockback = 0.55f;

    [Header("Прерывание своего замаха")]
    public float interruptMinDamage = 8f;
    public float interruptMinStagger = 3.5f;

    public float Health => _health;
    public float HealthPercent => maxHealth > 0f ? _health / maxHealth : 0f;
    public bool IsAlive => _health > 0f;

    public System.Action OnDeath;

    private float _health;
    private HumanoidCombat _combat;
    private HumanoidLocomotion _loco;
    private bool _dead;

    public static readonly System.Collections.Generic.List<TrainingDummyStats> Alive
        = new System.Collections.Generic.List<TrainingDummyStats>(4);

    public static bool IsPreviewAttacker(Component attacker)
    {
        if (attacker == null) return false;
        var stats = attacker.GetComponentInParent<TrainingDummyStats>();
        return stats != null && stats.previewHits && stats.IsAlive;
    }

    void Awake()
    {
        _health = maxHealth;
        _combat = GetComponent<HumanoidCombat>();
        _loco = GetComponent<HumanoidLocomotion>();
    }

    void OnEnable()
    {
        if (!Alive.Contains(this)) Alive.Add(this);
    }

    void OnDisable()
    {
        Alive.Remove(this);
    }

    void Update()
    {
        if (!IsAlive || healthRegenPerSecond <= 0f) return;
        if (_health < maxHealth)
            _health = Mathf.Min(maxHealth, _health + healthRegenPerSecond * Time.deltaTime);
    }

    public void TakeDamage(float amount)
    {
        ApplyHealth(amount, showPopup: true);
    }

    public void TakeDamage(float amount, Vector3 sourcePosition) => TakeDamage(amount);

    public void TakeHit(HitInfo hit)
    {
        if (!IsAlive) return;
        float dmg = hit.finalDamage > 0f ? hit.finalDamage : hit.rawDamage;
        if (_combat != null && ShouldInterrupt(hit, dmg))
            _combat.ReceiveHitShock(hit.isHeavy || hit.stagger >= 5.5f || hit.kind == DamageKind.Blunt);
        ApplyHealth(dmg, showPopup: true);
    }

    bool ShouldInterrupt(HitInfo hit, float dmg)
    {
        if (_loco != null && (_loco.IsDodging || _loco.IsRolling)) return false;
        return dmg >= interruptMinDamage || hit.stagger >= interruptMinStagger;
    }

    void ApplyHealth(float amount, bool showPopup)
    {
        if (!IsAlive || amount <= 0f) return;
        _health = Mathf.Max(0f, _health - amount);
        if (showPopup)
            DamagePopup.Spawn(transform.position + Vector3.up * 2f, amount, Color.white);
        if (_health <= 0f) Die();
    }

    public void ApplyKnockback(Vector3 force)
    {
        if (_loco == null || force.sqrMagnitude < 0.0001f) return;
        float scale = 80f / Mathf.Max(40f, mass);
        _loco.ApplyHitRecoil(force * scale, false);
    }

    void Die()
    {
        if (_dead) return;
        _dead = true;
        _health = 0f;
        OnDeath?.Invoke();
        Alive.Remove(this);
        if (_combat != null)
        {
            if (_combat.IsCharging) _combat.CancelCharge();
            _combat.SetBlocking(false);
            _combat.ClearTarget();
        }
        if (_loco != null) _loco.SetMove(Vector3.zero, 1, false);
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;
        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        enabled = false;
    }
}
