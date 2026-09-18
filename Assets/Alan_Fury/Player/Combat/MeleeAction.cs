using UnityEngine;

/// <summary>
/// Исполнение ближнего удара. Не выбирает форму и не играет замах/анимацию —
/// это хозяин (CombatController / WerewolfCombat).
/// Сюда: пояс, зона-меш через WeaponHitbox, урон.
/// Telegraph = замах (зона без урона). Play = проход удара по зоне. Stop = прерывание.
/// </summary>
public class MeleeAction : MonoBehaviour
{
    public WeaponHitbox hitbox;
    [Tooltip("Пусто — встроенная таблица.")]
    public CombatRangeTable table;

    public CombatRangeTable Table => table != null ? table : CombatRangeTable.Default;

    public bool IsPlaying { get; private set; }
    public bool IsTelegraphing => hitbox != null && hitbox.IsTelegraphing;

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
        public float sweepSign;
        public HitInfo info;
        public WeaponData weapon;
        public Transform target;
    }

    void Awake()
    {
        if (hitbox == null) hitbox = GetComponentInChildren<WeaponHitbox>();
    }

    public void Telegraph(in Request req)
    {
        if (hitbox == null) return;
        Resolve(req, out float range, out _, out Vector3 dir, out _);
        hitbox.ShowTelegraph(
            range,
            req.radius,
            req.height,
            req.offset,
            dir,
            req.layers,
            req.cone,
            req.shape,
            req.innerRadius,
            req.yawOffset,
            req.sweepSign == 0f ? 1f : req.sweepSign
        );
        IsPlaying = false;
    }

    public void Play(in Request req)
    {
        if (hitbox == null) return;

        Resolve(req, out float range, out float damage, out Vector3 dir, out HitInfo info);

        hitbox.SetHitInfo(info);
        hitbox.Activate(
            range,
            req.radius,
            req.height,
            req.offset,
            dir,
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
            req.yawOffset,
            req.sweepSign == 0f ? 1f : req.sweepSign
        );
        IsPlaying = true;
    }

    public void Stop()
    {
        IsPlaying = false;
        if (hitbox != null) hitbox.Deactivate();
    }

    void Resolve(in Request req, out float range, out float damage, out Vector3 dir, out HitInfo info)
    {
        damage = req.damage;
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

        range = req.range > 0f ? req.range : Table.Outer(band);
        dir = req.direction.sqrMagnitude > 0.001f ? req.direction.normalized : transform.forward;

        info = req.info;
        if (info.rawDamage <= 0f && damage > 0f)
            info = HitInfo.Basic(damage, transform.position);
        info.rawDamage = damage;
        info.finalDamage = damage;
        info.stagger = req.stagger;
        info.hitDirection = dir;
        info.sourcePosition = transform.position;
    }
}
