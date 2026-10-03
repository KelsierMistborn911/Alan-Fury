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
    [Tooltip("Аниматор игрока. Пусто — ищется на объекте и в детях.")]
    public Animator animator;
    [Tooltip("Сколько держать High / заднюю Low, прежде чем вернуть Mid.")]
    public float stanceDuration = 2f;

    public CombatStance Current { get; private set; } = CombatStance.Neutral;

    private float _stanceTimer;
    private System.Collections.Generic.HashSet<string> _animParams;

    void Awake()
    {
        EnsureAnimator();
        CacheAnimParams();
    }

    void EnsureAnimator()
    {
        if (animator != null) return;
        animator = GetComponent<Animator>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
    }

    void CacheAnimParams()
    {
        _animParams = new System.Collections.Generic.HashSet<string>();
        EnsureAnimator();
        if (animator == null || animator.runtimeAnimatorController == null) return;
        foreach (var p in animator.parameters) _animParams.Add(p.name);
    }

    bool HasParam(string name)
    {
        if (animator == null) EnsureAnimator();
        if (_animParams == null || (_animParams.Count == 0 && animator != null))
            CacheAnimParams();
        return animator != null && _animParams != null && _animParams.Contains(name);
    }

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

    public void PulseCurrent()
    {
        WriteStance((int)Current);
        FireTrig(Current);
        if (Current == CombatStance.High || Current == CombatStance.Low)
            _stanceTimer = stanceDuration;
    }

    public void Enter(CombatStance s, bool writeAnim = true)
    {
        if (Current == s)
        {
            if (s == CombatStance.High || s == CombatStance.Low)
                _stanceTimer = stanceDuration;
            if (writeAnim)
                WriteStance((int)s);
            return;
        }

        Current = s;
        _stanceTimer = (s == CombatStance.High || s == CombatStance.Low) ? stanceDuration : 0f;
        if (writeAnim)
            WriteStance((int)s);

        if (writeAnim && s != CombatStance.High && s != CombatStance.Low)
            FireTrig(s);
    }

    public void SuppressAnim()
    {
        WriteStance((int)CombatStance.Mid);
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
        WriteStance(0);
        SetTrig("EnterNeutral");
    }

    void WriteStance(int value)
    {
        if (!HasParam("Stance") || animator == null) return;
        animator.SetInteger("Stance", value);
    }

    void SetTrig(string name)
    {
        if (animator == null || !HasParam(name)) return;
        animator.ResetTrigger(name);
        animator.SetTrigger(name);
    }
}
