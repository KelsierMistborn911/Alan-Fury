using UnityEngine;

/// <summary>
/// Заметность и шум персонажа. Данные для восприятия AI (зрение/обоняние позже).
/// IsSneaking и gait живут в PlayerMovement3D — отсюда только читаем.
/// Множители и условия расширяются по мере стелса (туман, раны, атаки…).
/// </summary>
public class PlayerStealth : MonoBehaviour
{
    [Header("База")]
    [Tooltip("Базовая заметность персонажа (1 = обычный). Позже — стат/экипировка.")]
    public float baseNoticeability = 1f;

    [Header("Множители шума от gait / скрытности")]
    [Tooltip("Стоит на месте.")]
    public float idleMult = 0.15f;
    [Tooltip("Обычная ходьба (Gait 1, не sneak).")]
    public float walkMult = 0.6f;
    [Tooltip("Обычный бег (Gait 2).")]
    public float runMult = 1.0f;
    [Tooltip("Обычный спринт (Gait 3).")]
    public float sprintMult = 1.6f;
    [Tooltip("Крадущаяся ходьба (Sneaking + Gait 1).")]
    public float sneakWalkMult = 0.2f;
    [Tooltip("Крадущийся бег (Sneaking + Gait 2).")]
    public float sneakRunMult = 0.4f;
    [Tooltip("Скрытный спринт (Sneaking + Gait 3).")]
    public float sneakSprintMult = 0.7f;

    [Header("Заготовка под действия")]
    [Tooltip("Множитель, пока идёт атака/замах (пока заглушка — CombatController позже).")]
    public float attackingMult = 1.4f;
    [Tooltip("Множитель при блоке.")]
    public float blockingMult = 1.1f;

    /// <summary>Текущий шум (0…∞). AI сравнивает с порогами / бросками.</summary>
    public float CurrentNoise { get; private set; }

    /// <summary>Итоговая заметность (пока = base; позже статы, баффы, раны).</summary>
    public float Noticeability => baseNoticeability;

    private PlayerMovement3D _movement;
    private CombatController3D _combat;

    void Awake()
    {
        _movement = GetComponent<PlayerMovement3D>();
        _combat = GetComponent<CombatController3D>();
    }

    void Update()
    {
        CurrentNoise = ComputeNoise();
    }

    float ComputeNoise()
    {
        if (_movement == null) return baseNoticeability;

        float gaitMult;
        bool sneaking = _movement.IsSneaking;
        int gait = _movement.CurrentGaitLevel;
        bool moving = _movement.CurrentSpeed > _movement.moveThreshold;

        if (!moving)
            gaitMult = idleMult;
        else if (sneaking)
        {
            if (gait >= 3) gaitMult = sneakSprintMult;
            else if (gait == 2) gaitMult = sneakRunMult;
            else gaitMult = sneakWalkMult;
        }
        else
        {
            if (gait >= 3) gaitMult = sprintMult;
            else if (gait == 2) gaitMult = runMult;
            else gaitMult = walkMult;
        }

        float actionMult = 1f;
        if (_combat != null)
        {
            if (_combat.IsInAttackPipeline)
                actionMult *= attackingMult;
            if (_combat.IsBlocking)
                actionMult *= blockingMult;
        }

        return baseNoticeability * gaitMult * actionMult;
    }
}
