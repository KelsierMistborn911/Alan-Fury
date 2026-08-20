using UnityEngine;

public enum CombatStance { Neutral, High, Low }

/// <summary>
/// Стойки игрока (Neutral / High / Low). Вынесено из CombatController3D.
/// Триггеры аниматора сохранены: integer "Stance" (0/1/2), "EnterHigh", "EnterLow".
/// </summary>
public class PlayerStance : MonoBehaviour
{
    [Header("Стойки")]
    [Tooltip("Аниматор игрока. Пусто — найдётся на объекте.")]
    public Animator animator;
    [Tooltip("Длительность High/Low стойки после удара или удержания клавиши (сек).")]
    public float stanceDuration = 5f;
    [Tooltip("Удержание → задняя (Low) стойка. После отпускания таймер догорает.")]
    public KeyCode lowStanceKey = KeyCode.E;

    public CombatStance Current { get; private set; } = CombatStance.Neutral;

    private float _stanceTimer;
    private System.Collections.Generic.HashSet<string> _animParams;

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        CacheAnimParams();
    }

    void CacheAnimParams()
    {
        _animParams = new System.Collections.Generic.HashSet<string>();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters) _animParams.Add(p.name);
    }

    /// <summary>Вызывать каждый кадр из CombatController. Нужны IsArmed и ForcePeace.</summary>
    public void Tick(bool isArmed, bool forcePeace)
    {
        // Удержание E → задняя (Low) стойка, таймер обновляется пока клавиша зажата
        if (isArmed && !forcePeace && Input.GetKey(lowStanceKey))
        {
            if (Current != CombatStance.Low)
                Enter(CombatStance.Low);
            else
                _stanceTimer = stanceDuration;
            return;
        }

        if (Current == CombatStance.Neutral)
            return;

        _stanceTimer -= Time.deltaTime;
        if (_stanceTimer <= 0f)
            Enter(CombatStance.Neutral);
    }

    public void Enter(CombatStance s)
    {
        if (Current == s)
        {
            _stanceTimer = stanceDuration;
            return;
        }

        CombatStance prev = Current;
        Current = s;
        _stanceTimer = (s == CombatStance.Neutral) ? 0f : stanceDuration;

        // Animator: int Stance 0=Neutral 1=High 2=Low
        if (HasParam("Stance") && animator != null)
            animator.SetInteger("Stance", (int)s);

        // Триггеры входа в стойку (замах) — имена прежние
        if (s == CombatStance.High && prev == CombatStance.Neutral)
            SetTrig("EnterHigh");
        else if (s == CombatStance.Low && prev != CombatStance.Low)
            SetTrig("EnterLow");
    }

    public void ResetToNeutral()
    {
        Current = CombatStance.Neutral;
        _stanceTimer = 0f;
        if (HasParam("Stance") && animator != null)
            animator.SetInteger("Stance", 0);
    }

    bool HasParam(string name)
    {
        return animator != null && _animParams != null && _animParams.Contains(name);
    }

    void SetTrig(string name)
    {
        if (animator != null && _animParams != null && _animParams.Contains(name))
            animator.SetTrigger(name);
    }
}
