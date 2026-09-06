using UnityEngine;

public enum CombatStance { Neutral, High, Low, Mid }

/// <summary>
/// Neutral — мир. Mid — боевой покой. High — после лёгкого. Low — задняя, после заряда.
/// High/Low через stanceDuration сгорают в Mid. Mid в бою не сбрасывается.
/// Animator: int Stance 0 Neutral / 1 High / 2 Low / 3 Mid;
/// triggers EnterNeutral, EnterHigh, EnterLow, EnterMid.
/// </summary>
public class PlayerStance : MonoBehaviour
{
    [Header("Стойки")]
    [Tooltip("Аниматор игрока. Пусто — найдётся на объекте.")]
    public Animator animator;
    [Tooltip("Сколько держать High / заднюю Low, прежде чем вернуть Mid.")]
    public float stanceDuration = 2f;

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

    /// <summary>High/Low сгорают в Mid, только если сейчас не бьём.</summary>
    public void Tick(bool isInCombat, bool isArmed, bool attackHold = false)
    {
        if (!isArmed || !isInCombat)
        {
            if (Current != CombatStance.Neutral)
                ResetToNeutral();
            return;
        }

        if (attackHold) return;
        if (Current != CombatStance.High && Current != CombatStance.Low)
            return;

        _stanceTimer -= Time.deltaTime;
        if (_stanceTimer <= 0f)
            Enter(CombatStance.Mid);
    }

    /// <summary>Повтор триггера текущей стойки — звать в конце удара, иначе замах съедает Enter*.</summary>
    public void PulseCurrent()
    {
        if (HasParam("Stance") && animator != null)
            animator.SetInteger("Stance", (int)Current);
        FireTrig(Current);
        if (Current == CombatStance.High || Current == CombatStance.Low)
            _stanceTimer = stanceDuration;
    }

    public void Enter(CombatStance s)
    {
        if (Current == s)
        {
            if (s == CombatStance.High || s == CombatStance.Low)
                _stanceTimer = stanceDuration;
            return;
        }

        Current = s;
        _stanceTimer = (s == CombatStance.High || s == CombatStance.Low) ? stanceDuration : 0f;

        if (HasParam("Stance") && animator != null)
            animator.SetInteger("Stance", (int)s);

        FireTrig(s);
    }

    void FireTrig(CombatStance s)
    {
        switch (s)
        {
            case CombatStance.High:
                SetTrig("EnterHigh");
                break;
            case CombatStance.Low:
                SetTrig("EnterLow");
                break;
            case CombatStance.Mid:
                SetTrig("EnterMid");
                break;
            default:
                SetTrig("EnterNeutral");
                break;
        }
    }

    public void ResetToNeutral()
    {
        if (Current == CombatStance.Neutral)
        {
            _stanceTimer = 0f;
            return;
        }

        Current = CombatStance.Neutral;
        _stanceTimer = 0f;
        if (HasParam("Stance") && animator != null)
            animator.SetInteger("Stance", 0);
        SetTrig("EnterNeutral");
    }

    bool HasParam(string name)
    {
        return animator != null && _animParams != null && _animParams.Contains(name);
    }

    void SetTrig(string name)
    {
        if (animator == null || _animParams == null || !_animParams.Contains(name)) return;
        animator.ResetTrigger(name);
        animator.SetTrigger(name);
    }
}
