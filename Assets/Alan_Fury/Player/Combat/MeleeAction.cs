using UnityEngine;

/// <summary>
/// »сполнение ближнего удара. Ќе выбирает форму и не играет замах/анимацию Ч
/// это хоз€ин (CombatController / WerewolfCombat).
/// —юда: по€с, зона, спрайт через WeaponHitbox, урон.
/// </summary>
public class MeleeAction : MonoBehaviour
{
    public WeaponHitbox hitbox;
    [Tooltip("ѕусто Ч встроенна€ таблица.")]
    public CombatRangeTable table;

    public CombatRangeTable Table => table != null ? table : CombatRangeTable.Default;

    public bool IsPlaying { get; private set; }

    public struct Request
    {
        public CombatRange band;
        public float range, radius, height;
        public Vector3 offset, direction;
        public float damage, stagger;
        public LayerMask layers;
        public float duration, tick, charge, cone;
        public int combo;
        public HitZoneShape shape;
        public float innerRadius;
        public float yawOffset;
        public HitInfo info;
        public WeaponData weapon;
        public Transform target;
    }

    void Awake()
    {
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
    }

    public void Play(in Request req)
    {
        if (hitbox == null) return;

        float damage = req.damage;
        CombatRange band = req.band;
        if (req.target != null)
        {
            Vector3 d = req.target.position - transform.position;
            d.y = 0f;
            band = Table.Band(d.magnitude);
        }

        if (req.weapon != null)
        {
            float m = req.weapon.BandMultiplier(band);
            if (m > 0f) damage *= m;
        }

        HitInfo info = req.info;
        if (info.rawDamage <= 0f && damage > 0f)
            info = HitInfo.Basic(damage, transform.position);
        info.rawDamage = damage;
        info.finalDamage = damage;
        info.stagger = req.stagger;
        info.hitDirection = req.direction.sqrMagnitude > 0.001f ? req.direction.normalized : transform.forward;
        info.sourcePosition = transform.position;

        hitbox.SetHitInfo(info);
        hitbox.Activate(
            req.range > 0f ? req.range : Table.Outer(band),
            req.radius,
            req.height,
            req.offset,
            info.hitDirection,
            damage,
            req.stagger,
            req.layers,
            req.duration,
            req.tick,
            req.charge,
            req.combo,
            req.cone,
            req.shape,
            req.innerRadius,
            req.yawOffset
        );
        IsPlaying = true;
    }

    public void Stop()
    {
        IsPlaying = false;
    }
}
