using UnityEngine;

/// <summary>
/// Переключает визуал оружия/щита между «в руке» и «в ножнах».
/// Вызывается из CombatController3D при Draw / Sheath.
/// </summary>
public class WeaponVisual : MonoBehaviour
{
    [Header("Меч")]
    [Tooltip("Скинненный меч в руке (AKER SWORD). Включается когда меч достат.")]
    public GameObject combatSword;
    [Tooltip("Жёсткая копия меча на бедре (AKER SWORD_Sheathed). Включается когда меч убран.")]
    public GameObject sheathedSword;

    [Header("Щит")]
    [Tooltip("Щит в руке / на предплечье.")]
    public GameObject combatShield;
    [Tooltip("Щит на спине.")]
    public GameObject sheathedShield;

    // --- Меч ---

    public void SetSwordDrawn()
    {
        if (combatSword != null) combatSword.SetActive(true);
        if (sheathedSword != null) sheathedSword.SetActive(false);
    }

    public void SetSwordSheathed()
    {
        if (combatSword != null) combatSword.SetActive(false);
        if (sheathedSword != null) sheathedSword.SetActive(true);
    }

    // --- Щит ---

    public void SetShieldDrawn()
    {
        if (combatShield != null) combatShield.SetActive(true);
        if (sheathedShield != null) sheathedShield.SetActive(false);
    }

    public void SetShieldSheathed()
    {
        if (combatShield != null) combatShield.SetActive(false);
        if (sheathedShield != null) sheathedShield.SetActive(true);
    }

    // --- Совместимость со старым API ---

    public void SetDrawn()
    {
        SetSwordDrawn();
        SetShieldDrawn();
    }

    public void SetSheathed()
    {
        SetSwordSheathed();
        SetShieldSheathed();
    }
}
