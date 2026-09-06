using UnityEngine;

public interface IDamageable
{
    bool IsAlive => true;

    void TakeDamage(float amount);
    // ”рон с позицией источника (дл€ направленного откидывани€).
    // ѕо умолчанию Ч тот же урон, направление использует только тот, кому нужно.
    void TakeDamage(float amount, Vector3 sourcePosition) => TakeDamage(amount);

    /// <summary>
    /// ѕолный хит с зоной/пробитием. ѕо умолчанию Ч просто finalDamage/rawDamage.
    /// WoundTracker + WerewolfStats переопредел€ют.
    /// </summary>
    void TakeHit(HitInfo hit) => TakeDamage(hit.finalDamage > 0f ? hit.finalDamage : hit.rawDamage, hit.sourcePosition);

    void ApplyKnockback(Vector3 force);
}
