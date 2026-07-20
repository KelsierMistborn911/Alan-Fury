using UnityEngine;

public interface IDamageable
{
    void TakeDamage(float amount);
    // ”рон с известной позицией источника (дл€ направленного блока).
    // ѕо умолчанию Ч обычный урон, реализаци€м без направленности ничего мен€ть не надо.
    void TakeDamage(float amount, Vector3 sourcePosition) => TakeDamage(amount);
    void ApplyKnockback(Vector3 force);
}