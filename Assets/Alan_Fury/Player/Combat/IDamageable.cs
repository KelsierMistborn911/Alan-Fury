using UnityEngine;

public interface IDamageable
{
    bool IsAlive => true;

    void TakeDamage(float amount);
    // Урон с позицией источника (для направленного откидывания).
    // По умолчанию — тот же урон, направление использует только тот, кому нужно.
    void TakeDamage(float amount, Vector3 sourcePosition) => TakeDamage(amount);

    /// <summary>
    /// Полный хит с зоной/пробитием. По умолчанию — просто finalDamage/rawDamage.
    /// WoundTracker + WerewolfStats переопределяют.
    /// </summary>
    void TakeHit(HitInfo hit) => TakeDamage(hit.finalDamage > 0f ? hit.finalDamage : hit.rawDamage, hit.sourcePosition);

    void ApplyKnockback(Vector3 force);
}
