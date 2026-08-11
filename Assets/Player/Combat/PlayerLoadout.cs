using UnityEngine;

public class PlayerLoadout : MonoBehaviour
{
    [Header("Руки")]
    public WeaponData rightHandWeapon;
    public WeaponData leftHandWeapon;

    [Header("Прочность щита (runtime)")]
    [Tooltip("Текущая прочность экипированного щита. При смене щита сбрасывается на max.")]
    public float currentShieldDurability = 100f;

    public WeaponData GetMainWeapon() => rightHandWeapon;
    public WeaponData GetOffhandWeapon() => leftHandWeapon;

    public bool HasShield()
    {
        return leftHandWeapon != null
            && leftHandWeapon.type == WeaponData.WeaponType.Shield
            && currentShieldDurability > 0f;
    }

    public float ShieldDurabilityPercent
    {
        get
        {
            if (leftHandWeapon == null || leftHandWeapon.type != WeaponData.WeaponType.Shield)
                return 0f;
            float max = Mathf.Max(1f, leftHandWeapon.maxDurability);
            return Mathf.Clamp01(currentShieldDurability / max);
        }
    }

    /// <summary>Нанести урон прочности щита. Возвращает true, если щит ещё жив.</summary>
    public bool DamageShield(float amount)
    {
        if (!HasShield()) return false;
        currentShieldDurability = Mathf.Max(0f, currentShieldDurability - amount);
        return currentShieldDurability > 0f;
    }

    /// <summary>Восстановить прочность до максимума (при экипировке нового щита и т.п.).</summary>
    public void ResetShieldDurability()
    {
        if (leftHandWeapon != null && leftHandWeapon.type == WeaponData.WeaponType.Shield)
            currentShieldDurability = leftHandWeapon.maxDurability > 0f
                ? leftHandWeapon.maxDurability
                : 100f;
        else
            currentShieldDurability = 0f;
    }

    void Start()
    {
        if (leftHandWeapon != null && leftHandWeapon.type == WeaponData.WeaponType.Shield
            && currentShieldDurability <= 0f)
        {
            ResetShieldDurability();
        }
    }
}
