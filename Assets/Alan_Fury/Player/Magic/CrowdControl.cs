using UnityEngine;

/// <summary>
/// Короткий стан / ослепление. Мозги сами делают early-out по IsStunned.
/// </summary>
public class CrowdControl : MonoBehaviour
{
    float _stunUntil;
    float _blindUntil;

    public bool IsStunned => Time.time < _stunUntil;
    public bool IsBlind => Time.time < _blindUntil;

    public void Stun(float seconds)
    {
        _stunUntil = Mathf.Max(_stunUntil, Time.time + Mathf.Max(0f, seconds));
    }

    public void Blind(float seconds)
    {
        _blindUntil = Mathf.Max(_blindUntil, Time.time + Mathf.Max(0f, seconds));
    }

    public static CrowdControl On(Component c)
    {
        if (c == null) return null;
        var cc = c.GetComponent<CrowdControl>();
        if (cc == null) cc = c.gameObject.AddComponent<CrowdControl>();
        return cc;
    }
}
